using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WirelessInternet;

// Uses only a unique local fixture directory and this executable's --child
// subprocesses. No ADB, network, adapter, phone or production session is used.
internal static class RuntimeServiceChecks
{
    static readonly List<object> Results = new List<object>();
    static readonly List<Process> Children = new List<Process>();
    static readonly List<object> DetachedDiagnostics = new List<object>();
    static readonly string Self = typeof(RuntimeServiceChecks).Assembly.Location;
    static void Check(bool value, string name)
    { Results.Add(new { Name = name, Passed = value }); if (!value) throw new Exception(name); }
    static void Reject(Action action, string name)
    { bool rejected = false; try { action(); } catch (IOException) { rejected = true; } Check(rejected, name); }
    static RuntimeState State(RuntimeContext context)
    {
        string token = new string('a', 32);
        return new RuntimeState { PhoneIdentity = "RUNTIME_FIXTURE", WirelessSerial = "192.0.2.1:37123",
            ProtectionSessionId = token, PhoneControlPath = "/data/local/tmp/codex-wireless-internet/" + token + "/control.sh",
            RelayPath = context.RelayPath, WatchdogPath = context.WorkerPath, RuntimeEngine = "CSharp" };
    }
    sealed class ValidationContext : RuntimeContext
    {
        public int ProtectionCalls;
        public bool Accept = true;
        public ValidationContext(string root) : base(root) { }
        public override bool ProtectionValid(RuntimeState state) { ProtectionCalls++; return Accept && base.ProtectionValid(state); }
    }
    sealed class ReadOverrideContext : RuntimeContext
    {
        public int Reads;
        public RuntimeState Supplied;
        public ReadOverrideContext(string root) : base(root) { }
        public override RuntimeState ReadState() { Reads++; return Supplied; }
    }
    static void WriteConfig(string root, string content)
    { File.WriteAllText(Path.Combine(root, "wireless-config.json"), content, new UTF8Encoding(false)); }
    static Process StartChild(string executable, string marker)
    {
        Process child = Process.Start(new ProcessStartInfo(executable, "--child " + ProcessExecution.Quote(marker)) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        Children.Add(child);
        if (child.StandardOutput.ReadLine() != "READY") throw new IOException("Fixture child did not become ready.");
        return child;
    }
    static int Child(string marker)
    {
        using (Process self = Process.GetCurrentProcess())
            File.WriteAllText(marker, self.Id + "|" + self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "|" + self.MainModule.FileName);
        Console.WriteLine("READY"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); return 0;
    }
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetStdHandle(int id, IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    static int DetachedChild(string marker)
    {
        // Match WorkerHost's monitor startup contract, before any Console I/O.
        HashSet<IntPtr> handles = new HashSet<IntPtr>();
        foreach (int id in new int[] { -10, -11, -12 })
        {
            IntPtr handle = GetStdHandle(id); SetStdHandle(id, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) handles.Add(handle);
        }
        Console.SetIn(TextReader.Null); Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
        foreach (IntPtr handle in handles) CloseHandle(handle);
        using (Process self = Process.GetCurrentProcess())
            File.WriteAllText(marker, self.Id + "|" + self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "|" + self.MainModule.FileName);
        Thread.Sleep(Timeout.Infinite); return 0;
    }
    static int DetachedStarter(string root, string marker, string childMode)
    {
        RuntimeContext context = new RuntimeContext(root);
        using (Process child = context.StartDetached(Self, new string[] { childMode, marker }))
        {
            string owner = child.Id + "|" + child.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Self;
            // Record ownership as soon as the launching API returns, independently
            // of managed child startup, Console readiness or redirected pipe EOF.
            File.WriteAllText(marker + ".created", owner);
            if (childMode == "--child")
                owner = child.Id + "|" + child.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + "|" + child.MainModule.FileName;
            Console.WriteLine(owner);
        }
        return 0;
    }
    static void StopMarkerChild(string marker)
    {
        if (!File.Exists(marker)) return;
        string[] pieces = File.ReadAllText(marker).Split('|');
        if (pieces.Length != 3) throw new IOException("Invalid test child marker.");
        using (Process owned = OwnedProcessManager.GetOwnedProcess(Int32.Parse(pieces[0]), pieces[1], pieces[2], true, true))
            OwnedProcessManager.TerminatePinned(owned, 2000);
    }
    sealed class DetachedEvidence
    {
        public string Mode, Output = "", Error = "", OutputAfterCleanup = "", ErrorAfterCleanup = "", Failure;
        public bool StarterExited, OutputCompleted, ErrorCompleted, ChildAliveBeforeCleanup;
        public bool OutputCompletedAfterCleanup, ErrorCompletedAfterCleanup, StarterStoppedDuringCleanup;
        public int? ExitCode;
        public long StarterWaitMilliseconds, EofWaitMilliseconds;
    }
    static DetachedEvidence ProbeDetached(string root, string marker, string childMode)
    {
        DetachedEvidence evidence = new DetachedEvidence { Mode = childMode };
        Process starter = null;
        string starterTicks = null;
        Task<string> output = null, error = null;
        try
        {
            starter = Process.Start(new ProcessStartInfo(Self, "--detached-starter " + ProcessExecution.Quote(root) + " " + ProcessExecution.Quote(marker) + " " + childMode) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            IntPtr pinned = starter.Handle;
            starterTicks = starter.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            output = starter.StandardOutput.ReadToEndAsync(); error = starter.StandardError.ReadToEndAsync();
            Stopwatch watch = Stopwatch.StartNew();
            evidence.StarterExited = starter.WaitForExit(3000);
            evidence.StarterWaitMilliseconds = watch.ElapsedMilliseconds;
            if (evidence.StarterExited) evidence.ExitCode = starter.ExitCode;
            watch.Restart();
            Task.WaitAll(new Task[] { output, error }, 1500);
            evidence.EofWaitMilliseconds = watch.ElapsedMilliseconds;
            evidence.OutputCompleted = output.IsCompleted; evidence.ErrorCompleted = error.IsCompleted;
            if (output.IsCompleted) evidence.Output = output.Result;
            if (error.IsCompleted) evidence.Error = error.Result;
            string ownerPath = File.Exists(marker + ".created") ? marker + ".created" : marker;
            if (File.Exists(ownerPath))
            {
                string[] pieces = File.ReadAllText(ownerPath).Split('|');
                if (pieces.Length != 3) throw new IOException("Invalid detached test child ownership record.");
                using (Process child = OwnedProcessManager.GetOwnedProcess(Int32.Parse(pieces[0]), pieces[1], pieces[2], true, true))
                    evidence.ChildAliveBeforeCleanup = child != null;
            }
        }
        catch (Exception exception) { evidence.Failure = exception.ToString(); }
        finally
        {
            // Cleanup never enumerates or kills by name/tree. The own starter's
            // exact handle and the child's independent ownership triple suffice.
            try
            {
                if (starter != null && !starter.HasExited)
                    using (Process owned = OwnedProcessManager.GetOwnedProcess(starter.Id, starterTicks, Self, true, true))
                    { OwnedProcessManager.TerminatePinned(owned, 2000); evidence.StarterStoppedDuringCleanup = true; }
                StopMarkerChild(marker + ".created");
                StopMarkerChild(marker);
                if (output != null && error != null)
                {
                    Task.WaitAll(new Task[] { output, error }, 1500);
                    evidence.OutputCompletedAfterCleanup = output.IsCompleted; evidence.ErrorCompletedAfterCleanup = error.IsCompleted;
                    if (output.IsCompleted) evidence.OutputAfterCleanup = output.Result;
                    if (error.IsCompleted) evidence.ErrorAfterCleanup = error.Result;
                }
            }
            catch (Exception exception) { evidence.Failure = (evidence.Failure ?? "") + "\nCleanup: " + exception; }
            finally { if (starter != null) starter.Dispose(); }
        }
        return evidence;
    }
    static int DetachedProbeMain(string directory, string mode, int repetitions)
    {
        string root = Path.Combine(Path.GetFullPath(directory), "detached-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        List<DetachedEvidence> results = new List<DetachedEvidence>();
        bool passed = true;
        for (int i = 0; i < repetitions; i++)
        {
            DetachedEvidence result = ProbeDetached(root, Path.Combine(root, "child-" + i), mode == "plain" ? "--child" : "--detached-child");
            results.Add(result);
            bool current = result.Failure == null && result.StarterExited && result.ExitCode == 0 && result.OutputCompleted && result.ErrorCompleted && result.ChildAliveBeforeCleanup;
            if (!current) passed = false;
            Console.WriteLine(new JavaScriptSerializer().Serialize(result));
        }
        File.WriteAllText(Path.Combine(root, "detached-probe-results.json"), new JavaScriptSerializer().Serialize(new { Passed = passed, Mode = mode, Results = results }));
        // The plain-child mode diagnoses a deliberately different lifecycle contract.
        return mode == "plain" || passed ? 0 : 1;
    }
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--child") return Child(args[1]);
        if (args.Length == 2 && args[0] == "--detached-child") return DetachedChild(args[1]);
        if (args.Length == 4 && args[0] == "--detached-starter") return DetachedStarter(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--detached-probe") return DetachedProbeMain(args[1], args[2], Int32.Parse(args[3], CultureInfo.InvariantCulture));
        string directory = args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
        string root = Path.Combine(directory, "runtime-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string detachedMarker = Path.Combine(root, "detached-child.txt");
        string failure = null;
        try
        {
            RuntimeContext context = new RuntimeContext(root);
            RuntimeSessionStore store = new RuntimeSessionStore(context.Root, context.StatePath);
            Check(!Directory.Exists(Path.Combine(root, "state")), "constructors do not create runtime state");
            Check(context.ReadState() == null, "missing state directory returns null");
            bool missingConfig = false; try { context.ReadConfig(); } catch (FileNotFoundException) { missingConfig = true; }
            Check(missingConfig, "missing configuration preserves file error");
            WriteConfig(root, "{\"Transport\":\"NativeTls\",\"PhoneIdentity\":\"RUNTIME_FIXTURE\",\"Unknown\":17}");
            var configuration = context.ReadConfig();
            Check(Convert.ToInt32(configuration["Unknown"]) == 17, "configuration unknown fields survive");
            Check(!configuration.ContainsKey("DisconnectTimeoutSeconds"), "default timeout validation does not rewrite configuration");
            foreach (int value in new int[] { 15, 30, 120 })
            {
                WriteConfig(root, "{\"Transport\":\"NativeTls\",\"PhoneIdentity\":\"RUNTIME_FIXTURE\",\"DisconnectTimeoutSeconds\":" + value + "}");
                Check(RuntimeContext.GetInt(context.ReadConfig(), "DisconnectTimeoutSeconds", 30) == value, "accepted timeout " + value);
            }
            foreach (int value in new int[] { 0, 14, 121 })
            {
                WriteConfig(root, "{\"Transport\":\"NativeTls\",\"PhoneIdentity\":\"RUNTIME_FIXTURE\",\"DisconnectTimeoutSeconds\":" + value + "}");
                Reject(delegate { context.ReadConfig(); }, "rejected timeout " + value);
            }
            WriteConfig(root, "{\"Transport\":\"Other\",\"PhoneIdentity\":\"RUNTIME_FIXTURE\"}");
            Reject(delegate { context.ReadConfig(); }, "non-native transport rejected");
            WriteConfig(root, "{\"Transport\":\"NativeTls\",\"PhoneIdentity\":\"\"}");
            Reject(delegate { context.ReadConfig(); }, "unbound identity rejected");
            WriteConfig(root, "{\"Transport\":\"NativeTls\",\"PhoneIdentity\":\"RUNTIME_FIXTURE\",\"DisconnectTimeoutSeconds\":\"30.5\"}");
            Reject(delegate { context.ReadConfig(); }, "non-integer configuration field rejected");
            WriteConfig(root, new string(' ', 262145)); Reject(delegate { context.ReadConfig(); }, "configuration size limit preserved");

            RuntimeState state = State(context);
            state.RawFields = new Dictionary<string, object> { { "Unknown", new Dictionary<string, object> { { "Value", 17 } } }, { "PhoneIdentity", "must-not-override" } };
            context.WriteState(state);
            RuntimeState round = context.ReadState();
            Check(round.PhoneIdentity == state.PhoneIdentity && ((Dictionary<string, object>)round.RawFields["Unknown"])["Value"].ToString() == "17", "canonical state fields and unknown nested fields preserved");
            Check(round.ProtectionTimeoutSeconds == 30, "state default protection stays thirty seconds");
            byte[] bytes = File.ReadAllBytes(context.StatePath);
            Check(bytes.Length < 3 || bytes[0] != 239 || bytes[1] != 187 || bytes[2] != 191, "state remains UTF8 without BOM");
            using (FileStream oldReader = new FileStream(context.StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                round.MonitorHeartbeatQpcTicks = "12345"; context.WriteState(round);
                using (StreamReader reader = new StreamReader(oldReader, Encoding.UTF8))
                    Check(!reader.ReadToEnd().Contains("12345"), "atomic replace leaves already open reader on intact old version");
            }
            Check(context.ReadState().MonitorHeartbeatQpcTicks == "12345", "new readers see complete replacement");
            Check(Directory.GetFiles(Path.Combine(root, "state"), "*.tmp").Length == 0, "atomic writes remove temporary files");
            context.DeleteStateIfToken(new string('b',32)); Check(File.Exists(context.StatePath), "wrong session cannot delete state");
            context.DeleteStateIfToken(new string('A',32)); Check(File.Exists(context.StatePath), "session token comparison remains ordinal");
            ValidationContext validation = new ValidationContext(root); validation.Accept = false;
            Reject(delegate { validation.ReadState(); }, "facade state validation calls virtual protection validator");
            Check(validation.ProtectionCalls == 1, "virtual protection validator invoked once");
            state.WirelessSerial = "999.0.2.1:37123"; context.WriteState(state);
            Reject(delegate { context.ReadState(); }, "invalid endpoint rejected before use");
            state = State(context); state.ProtectionSessionId = "bad"; context.WriteState(state);
            Reject(delegate { context.ReadState(); }, "invalid protection session rejected before use");
            File.WriteAllText(context.StatePath, new string(' ',262145)); Reject(delegate { context.ReadState(); }, "state size limit preserved");
            context.WriteState(State(context));
            ReadOverrideContext overrideRead = new ReadOverrideContext(root); overrideRead.Supplied = State(context);
            overrideRead.DeleteStateIfToken(overrideRead.Supplied.ProtectionSessionId);
            Check(overrideRead.Reads == 1 && !File.Exists(context.StatePath), "token deletion dispatches through virtual ReadState");
            context.DeleteStateIfToken(new string('a',32)); Check(context.ReadState() == null, "deleting absent state is harmless");
            context.WriteState(State(context));
            store.DeleteStateIfToken(new string('b',32), context.ReadState); Check(File.Exists(context.StatePath), "direct store respects read callback and wrong token");
            store.DeleteStateIfToken(new string('a',32), context.ReadState); Check(!File.Exists(context.StatePath), "direct store deletes exact token only");
            Check(RuntimeContext.GetString(null,"missing") == "" && RuntimeContext.GetInt(null,"missing",30) == 30, "configuration facade helpers preserve fallbacks");

            Process child = StartChild(Self, Path.Combine(root,"child.txt"));
            string ticks = child.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            Check(OwnedProcessManager.GetOwnedProcess(0,"",Self,true,true) == null, "nonpositive process id is absent");
            using (Process owned = RuntimeContext.GetOwnedProcess(child.Id,ticks,Self,true,true))
                Check(owned != null && owned.Id == child.Id, "facade pins exact child identity");
            Reject(delegate { using (Process wrong=OwnedProcessManager.GetOwnedProcess(child.Id,"1",Self,true,true)) { } }, "stale process start ticks rejected");
            Reject(delegate { using (Process wrong=OwnedProcessManager.GetOwnedProcess(child.Id,ticks,Path.Combine(root,"wrong.exe"),true,true)) { } }, "wrong process path rejected");
            using (Process self = Process.GetCurrentProcess())
                Reject(delegate { using (Process wrong=RuntimeContext.GetOwnedProcess(self.Id,self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),Self,true,true)) { } }, "self process rejected during ownership validation");
            using (Process self = Process.GetCurrentProcess()) Reject(delegate { OwnedProcessManager.TerminatePinned(self,10); }, "termination refuses current process");
            Check(!child.HasExited, "failed ownership checks leave fixture alive");
            OwnedProcessManager manager = new OwnedProcessManager(root,Self,Self);
            RuntimeState ownedState = State(context); ownedState.RelayPath = ownedState.WatchdogPath = Self;
            ownedState.RelayProcessId = ownedState.WatchdogProcessId = child.Id;
            ownedState.RelayStartTimeUtcTicks = ownedState.WatchdogStartTimeUtcTicks = ticks;
            using (Process relay=manager.GetOwnedRelay(ownedState,true)) Check(relay != null, "relay service verifies tracked identity");
            using (Process monitor=manager.GetOwnedMonitor(ownedState,true)) Check(monitor != null, "monitor service verifies tracked identity");
            ownedState.RelayPath=Path.Combine(root,"foreign.exe"); Reject(delegate { using(Process wrong=manager.GetOwnedRelay(ownedState,true)) { } }, "relay record with another path rejected");
            ownedState.WatchdogPath=Path.Combine(root,"foreign.exe"); Reject(delegate { using(Process wrong=manager.GetOwnedMonitor(ownedState,true)) { } }, "monitor record with another path rejected");
            ownedState.RuntimeEngine="unknown"; Reject(delegate { using(Process wrong=manager.GetOwnedMonitor(ownedState,true)) { } }, "unknown monitor engine rejected");
            using(Process owned=RuntimeContext.GetOwnedProcess(child.Id,ticks,Self,true,true)) OwnedProcessManager.TerminatePinned(owned,2000);
            Check(child.WaitForExit(2000), "termination stops only verified fixture child");
            Check(RuntimeContext.GetOwnedProcess(child.Id,ticks,Self,true,true) == null, "exited tracked child is absent");
            OwnedProcessManager.TerminatePinned(null,100); Check(true, "null owned process termination is harmless");

            DetachedEvidence detached = ProbeDetached(root, detachedMarker, "--detached-child");
            DetachedDiagnostics.Add(detached);
            Check(detached.Failure == null && detached.StarterExited && detached.ExitCode == 0 && detached.OutputCompleted && detached.ErrorCompleted,
                "production-style detached child closes starter output handles");
            string[] ownership = detached.Output.Trim().Split('|');
            Check(ownership.Length == 3, "detached starter reports exact ownership triple");
            Check(detached.ChildAliveBeforeCleanup, "detached child survives its starter and redirected EOF");
        }
        catch(Exception error) { failure=error.ToString(); }
        finally
        {
            foreach(Process child in Children)
            {
                try
                {
                    if(!child.HasExited)
                        using(Process owned=OwnedProcessManager.GetOwnedProcess(child.Id,child.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),Self,true,true))
                            OwnedProcessManager.TerminatePinned(owned,2000);
                }
                catch(Exception error) { if(failure==null) failure=error.ToString(); }
                finally { child.Dispose(); }
            }
            try { StopMarkerChild(detachedMarker + ".created"); StopMarkerChild(detachedMarker); } catch(Exception error) { if(failure==null) failure=error.ToString(); }
        }
        string report=new JavaScriptSerializer().Serialize(new { Passed=failure==null, Count=Results.Count, Failure=failure, Checks=Results, DetachedDiagnostics=DetachedDiagnostics });
        File.WriteAllText(Path.Combine(root,"runtime-service-results.json"),report);
        Console.WriteLine((failure==null?"PASS ":"FAIL ")+Results.Count+" runtime service checks"+(failure==null?"":"\n"+failure));
        return failure==null?0:1;
    }
}
