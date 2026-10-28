using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using WirelessInternet;

// Offline connection lifecycle characterization. SDK names below contain only
// copies of this test executable; ADB, network, phone and real session I/O are
// rejected or simulated by FakeContext. Children are pinned, owned test lifetimes.
internal static class ConnectionLifecycleChecks
{
    const string Phone = "LIFECYCLE_PHONE";
    const string Serial = "192.0.2.1:37123";
    const string OtherToken = "22222222222222222222222222222222";
    static readonly string Executable = typeof(ConnectionLifecycleChecks).Assembly.Location;
    static readonly List<object> Results = new List<object>();
    static string fixtures;
    static bool focused;
    static int assertions, failures;
    internal static void InitializeFixtures(string root) { fixtures = Path.GetFullPath(root); Directory.CreateDirectory(fixtures); }
    internal static string CreateFixtureDirectory(string parent, string prefix)
    {
        Directory.CreateDirectory(parent);
        // Keep legacy .NET/Win32 paths short even below tests/bin/offline-GUID.
        // The exclusive claim prevents two runs from ever sharing a fixture.
        for (int attempt = 0; attempt < 20; attempt++) {
            string path = Path.Combine(parent, prefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            if (Directory.Exists(path) || File.Exists(path)) continue;
            Directory.CreateDirectory(path);
            try { using (new FileStream(Path.Combine(path, ".claim"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } }
            catch (IOException) { continue; }
            return path;
        }
        throw new IOException("Could not reserve an independent short fixture directory.");
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fake-lifecycle")
        { Console.WriteLine("READY " + args[1]); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); return 0; }
        focused = args.Length > 1 && args[1] == "--pc-only";
        string report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetDirectoryName(Executable), "connection-lifecycle-results.json");
        fixtures = CreateFixtureDirectory(Path.GetDirectoryName(report), "lc-");

        Case("fresh PC-only Start commits resources with no phone shell guard", delegate(FakeContext c) {
            Equal(0, Start(c), "start result");
            Check(c.State != null && c.State.ProtectionTimeoutSeconds == 30, "default thirty second PC timeout");
            Equal("PcOnly", c.State.ProtectionMode, "explicit PC-only mode"); Check(String.IsNullOrEmpty(c.State.PhoneControlPath), "no script path");
            Equal(Serial, c.State.WirelessSerial, "hotspot gateway endpoint");
            Equal(1, c.HostStarts, "one host"); Equal(1, c.MonitorStarts, "one PC monitor");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn, "committed resources active");
            Check(!c.Events.Any(x => x.Contains("push") || x.Contains("mkdir") || x.Contains("nohup") || x.Contains("control.sh") || x.Contains("guard:")), "no phone guard operation");
            Before(c, "state:write", "gnirehtet:tunnel", "state before tunnel");
            Before(c, "gnirehtet:start", "monitor:start", "VPN before monitor");
            Before(c, "monitor:start", "lock:release", "monitor before lease release");
            Before(c, "lock:release", "lock:try", "first PC health check after release");
            Check(c.Events.Contains("ack:renew"), "independent PC monitor confirmation");
            Check(c.Output.Any(x => x.Contains("30")), "PC timeout progress retained");
        });
        Case("repeated Start reuses healthy PC-only resources", delegate(FakeContext c) {
            Equal(0, Start(c), "first start"); int relay = c.State.RelayProcessId, monitor = c.State.WatchdogProcessId; string token = c.State.ProtectionSessionId;
            Equal(0, Start(c), "repeat start"); Equal(relay, c.State.RelayProcessId, "same relay"); Equal(monitor, c.State.WatchdogProcessId, "same monitor");
            Equal(token, c.State.ProtectionSessionId, "same token"); Equal(1, c.HostStarts, "no extra host"); Equal(1, c.MonitorStarts, "no extra monitor");
        });
        Case("normal Stop confirms VPN shutdown then cleans exact owned resources", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.Events.Clear(); Equal(0, Stop(c), "stop");
            Check(c.State == null && !c.Relay.IsAlive && !c.Monitor.IsAlive && !c.Vpn, "resources stopped");
            Before(c, "gnirehtet:stop", "monitor:pin", "VPN before PC monitor cleanup");
            Before(c, "monitor:pin", "relay:pin", "monitor before relay"); Before(c, "relay:pin", "state:delete", "relay before state");
            Before(c, "state:delete", "adb:disconnect", "state before disconnect"); Equal(0, Stop(c), "repeat Stop"); Equal(1, c.Deletes, "one deletion");
        });
        Case("wrong phone identity fails before resource creation", delegate(FakeContext c) {
            c.ReturnedIdentity = "OTHER_PHONE"; Equal(1, Start(c), "rejected"); NoCreated(c);
        });
        Case("phone non-owner profile or disabled debugging is rejected", delegate(FakeContext c) {
            c.OwnerUser = "10"; Equal(1, Start(c), "owner rejected"); NoCreated(c);
            c.OwnerUser = "0"; c.WirelessEnabled = "0"; Equal(1, Start(c), "debugging rejected"); NoCreated(c);
        });
        Case("VPN restrictions reject Start before creating resources", delegate(FakeContext c) {
            c.AlwaysOn = "com.genymobile.gnirehtet"; Equal(1, Start(c), "rejected"); NoCreated(c); Equal(0, c.GnirehtetStarts, "no VPN start");
        });
        Case("untracked listener prevents resource creation without killing it", delegate(FakeContext c) {
            c.Extra = c.AddChild(c.RelayHostPath); Equal(1, Start(c), "rejected"); NoCreated(c); Check(c.Extra.IsAlive, "unrelated listener retained");
        });
        Case("first state failure cleans only newly owned uncommitted relay", delegate(FakeContext c) {
            c.Fail = "state-first"; Equal(1, Start(c), "failure"); Check(c.State == null && !c.Relay.IsAlive, "unsaved relay cleaned");
            Equal(0, c.GnirehtetStarts, "VPN never started before commit"); Equal(1, c.RelayPins, "exact relay selected");
        });
        Case("phone disconnect after commit retains recovery record", delegate(FakeContext c) {
            c.Fail = "tunnel-offline"; Equal(1, Start(c), "failure");
            Check(c.State != null && c.Relay.IsAlive, "committed record retained"); Equal(0, c.MonitorStarts, "no monitor for failed tunnel");
            Equal(0, c.GnirehtetStops, "no unstarted VPN stop");
        });
        Case("monitor startup failure rolls back only the VPN started by this attempt", delegate(FakeContext c) {
            c.Fail = "monitor-create"; Equal(1, Start(c), "failure");
            Check(c.State != null && c.Relay.IsAlive && !c.Vpn, "record retained and own new VPN stopped"); Equal(1, c.GnirehtetStops, "one verified rollback");
        });
        Case("preexisting phone VPN is retained when monitor startup fails", delegate(FakeContext c) {
            c.Vpn = true; c.Fail = "monitor-create"; Equal(1, Start(c), "failure");
            Equal(0, c.GnirehtetStarts, "preexisting VPN reused"); Equal(0, c.GnirehtetStops, "preexisting VPN not rolled back"); Check(c.Vpn, "VPN retained");
        });
        Case("missing first PC health confirmation preserves committed session without rollback", delegate(FakeContext c) {
            c.Fail = "ack"; Equal(1, Start(c), "failure"); Equal(60, c.AckSleeps, "bounded confirmation waits");
            Equal(0, c.GnirehtetStops, "no rollback after releasing action lease");
            Check(c.State != null && c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn, "committed session retained for monitor or explicit Stop"); Equal(0, c.LockViolations, "no unsafe rollback");
        });
        Case("session replacement before first confirmation never stops new phone session", delegate(FakeContext c) {
            c.ReplaceOnAck = true; Equal(1, Start(c), "failure"); Equal(OtherToken, c.State.ProtectionSessionId, "new token retained");
            Equal(0, c.GnirehtetStops, "no phone STOP after replacement"); Check(c.Vpn && c.Relay.IsAlive, "new session remains"); Equal(0, c.Deletes, "record retained");
        });
        Case("Stop stale relay lifetime cannot stop phone or recycled process", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.State.RelayStartTimeUtcTicks = "1"; Equal(1, Stop(c), "rejected");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn && c.State != null, "resources retained"); Equal(0, c.GnirehtetStops, "no phone mutation");
        });
        Case("Stop with wrong relay executable path retains all resources", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.State.RelayPath = Path.Combine(c.Root, "different.exe"); Equal(1, Stop(c), "rejected");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn, "wrong path retained"); Equal(0, c.GnirehtetStops, "no phone mutation");
        });
        Case("Stop runtime restriction failure precedes VPN and resource cleanup", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.RuntimeRestrictions = true; Equal(1, Stop(c), "rejected");
            Check(c.Vpn && c.Relay.IsAlive && c.Monitor.IsAlive && c.State != null, "resources retained"); Equal(0, c.GnirehtetStops, "no phone stop");
        });
        Case("unconfirmed VPN shutdown preserves resources for explicit retry", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.Fail = "vpn-stop"; Equal(1, Stop(c), "failure");
            Check(c.Vpn && c.Relay.IsAlive && c.Monitor.IsAlive && c.State != null, "unconfirmed stop retains PC state");
        });
        Case("failed VPN observation cannot claim cleanup succeeded", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.Fail = "vpn-observation"; Equal(1, Stop(c), "failure");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.State != null, "uncertain stop retained");
        });
        Case("Stop preserves a reverse mapping belonging to another destination", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.ReverseDestination = "tcp:39999"; Equal(0, Stop(c), "stop");
            Equal(0, c.ReverseRemoves, "other mapping preserved"); Equal("tcp:39999", c.ReverseDestination, "destination unchanged");
        });
        Case("new port listener during Stop is never killed", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.Extra = c.AddChild(c.RelayHostPath); Equal(1, Stop(c), "failure");
            Check(c.Relay.IsAlive && c.Extra.IsAlive && c.State != null, "recovery retained"); Equal(0, c.Deletes, "state retained");
        });
        Case("token replacement at deletion preserves newer record", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.ReplaceOnDelete = true; Equal(0, Stop(c), "stop");
            Equal(OtherToken, c.State.ProtectionSessionId, "new record retained"); Equal(0, c.Deletes, "token predicate retained");
        });
        Case("legacy phone guard session requires old-version disconnect", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); string token = c.State.ProtectionSessionId; int connects = c.Connects;
            c.State.ProtectionMode = null; c.State.PhoneControlPath = "/data/local/tmp/codex-wireless-internet/" + token + "/control.sh";
            Equal(1, Start(c), "legacy Start rejected"); Equal(1, Stop(c), "legacy Stop rejected");
            Equal(connects, c.Connects, "legacy phone not contacted"); Equal(0, c.GnirehtetStops, "old phone guard not bypassed");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn, "legacy active session retained");
        });
        var result = new { suite = "ConnectionLifecycleChecks", cases = Results.Count, assertions = assertions, failures = failures,
            realAdbCalls = 0, realNetworkCalls = 0, results = Results };
        string json = new JavaScriptSerializer().Serialize(result); File.WriteAllText(report, json, new UTF8Encoding(false));
        Console.WriteLine("RESULT " + new JavaScriptSerializer().Serialize(new { suite = "ConnectionLifecycleChecks", cases = Results.Count, assertions = assertions, failures = failures, report = Path.GetFileName(report) }));
        Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + assertions + " connection lifecycle assertions in " + Results.Count + " cases");
        return failures == 0 ? 0 : 1;
    }

    static int Start(FakeContext c) { c.BeginAction(); return new ConnectionController(c).Start(c.Output.Add); }
    static int Stop(FakeContext c) { c.BeginAction(); return new ConnectionController(c).Stop(c.Output.Add); }
    static void NoCreated(FakeContext c) { Equal(0, c.HostStarts, "no relay created"); Equal(0, c.MonitorStarts, "no monitor created"); Check(c.State == null, "no session written"); }
    static void Before(FakeContext c, string a, string b, string message)
    { int left = c.Events.IndexOf(a), right = c.Events.IndexOf(b); Check(left >= 0 && right > left, message); }
    static void Check(bool ok, string message) { assertions++; if (!ok) throw new InvalidOperationException(message); }
    static void Equal<T>(T expected, T actual, string message) { Check(EqualityComparer<T>.Default.Equals(expected, actual), message + ": expected " + expected + ", actual " + actual); }
    static RuntimeCommandResult Reply(int code, string text) { return new RuntimeCommandResult { ExitCode = code, Output = text }; }
    static void Case(string name, Action<FakeContext> action)
    {
        if (focused && name != "fresh PC-only Start commits resources with no phone shell guard" &&
            name != "normal Stop confirms VPN shutdown then cleans exact owned resources" &&
            name != "monitor startup failure rolls back only the VPN started by this attempt" &&
            name != "missing first PC health confirmation preserves committed session without rollback" &&
            name != "session replacement before first confirmation never stops new phone session" &&
            name != "legacy phone guard session requires old-version disconnect") return;
        int start = assertions; string error = null; FakeContext context = null;
        try {
            using (context = new FakeContext()) {
                action(context); Equal(0, context.LockDepth, "all mutex leases released");
                Equal(0, context.Illegal, "no unmocked I/O"); Equal(0, context.LockViolations, "operation lock contract retained");
            }
        }
        catch (Exception ex) { failures++; error = ex.ToString(); }
        Results.Add(new { name = name, passed = error == null, assertions = assertions - start, error = error,
            events = error != null && context != null ? context.Events.ToArray() : null,
            protocol = context == null ? null : context.Events.ToArray(),
            output = error != null && context != null ? context.Output.ToArray() : null });
        Console.WriteLine((error == null ? "PASS " : "FAIL ") + name);
    }

    internal sealed class Child : IDisposable
    {
        readonly Process held;
        public readonly string Path, Ticks;
        public readonly int Id;
        public Child(string path)
        {
            Path = path; string nonce = Guid.NewGuid().ToString("N");
            held = Process.Start(new ProcessStartInfo(path, "--fake-lifecycle " + nonce) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
            try {
                IntPtr handle = held.Handle; Id = held.Id; Ticks = held.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                var ready = held.StandardOutput.ReadLineAsync();
                if (!ready.Wait(3000) || ready.Result != "READY " + nonce) throw new IOException("Owned fake child did not start.");
                if (!String.Equals(System.IO.Path.GetFullPath(held.MainModule.FileName), path, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fake child path changed.");
            }
            catch { Dispose(); throw; }
        }
        public bool IsAlive { get { return !held.HasExited; } }
        public Process Get() { return RuntimeContext.GetOwnedProcess(Id, Ticks, Path, true, true); }
        public void Dispose() { try { if (!held.HasExited) { held.Kill(); held.WaitForExit(3000); } } finally { held.Dispose(); } }
    }

    internal sealed class FakeContext : RuntimeContext, IDisposable
    {
        public readonly List<Child> Children = new List<Child>();
        public readonly List<string> Events = new List<string>(), Output = new List<string>();
        public readonly Dictionary<string, object> Config = new Dictionary<string, object> { { "Transport", "NativeTls" }, { "PhoneIdentity", Phone }, { "DisconnectTimeoutSeconds", 30 }, { "ConnectEndpoint", Serial } };
        public RuntimeState State;
        public Child Relay, Monitor, Extra;
        public int LockDepth, LockViolations, Illegal, HostStarts, MonitorStarts, Deletes;
        public int GnirehtetStarts, GnirehtetStops, Connects, RelayPins, ReverseRemoves, ReadinessSleeps, AckSleeps, DiscoverySleeps;
        public string Fail = "", ReturnedIdentity = Phone, OwnerUser = "0", WirelessEnabled = "1", AlwaysOn = "null", ReverseDestination;
        public string Discovery = "adb-" + Phone + "-fixture _adb-tls-connect._tcp. 203.0.113.5:37123";
        public bool Vpn, RuntimeRestrictions, ReplaceOnAck, ReplaceOnDelete;
        public bool ThrowOnRelease;
        public int PrimaryReleaseInvocations;
        bool released, replaced;
        double seconds;
        int writes;

        public FakeContext() : base(CreateFixtureDirectory(fixtures, "c-"))
        {
            foreach (string path in new string[] { AdbPath, RelayPath, WorkerPath, RelayHostPath }) {
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.Copy(Executable, path);
            }
        }
        public Child AddChild(string path) { Child child = new Child(path); Children.Add(child); return child; }
        public void BeginAction() { released = replaced = false; PrimaryReleaseInvocations = 0; }
        public override long Timestamp { get { return 10000000000L + (long)Math.Round(seconds * Stopwatch.Frequency); } }
        public override void Sleep(int milliseconds)
        {
            if (milliseconds == 100) ReadinessSleeps++; if (milliseconds == 400) DiscoverySleeps++; if (milliseconds == 200 && LockDepth == 0) AckSleeps++;
            if (milliseconds <= 0 || milliseconds > 5000) throw new InvalidOperationException("Unbounded fake sleep."); seconds += milliseconds / 1000.0;
            Events.Add("sleep:" + milliseconds + ":lock=" + LockDepth);
        }
        public override IDisposable EnterCoreMutex(int timeoutMs = 6000)
        { if (Fail == "lock-acquire") throw new TimeoutException("injected busy operation lock"); Need(LockDepth == 0, "no recursive action lease"); LockDepth++; Events.Add("lock:enter"); return new Lease(this, true); }
        public override IDisposable TryEnterCoreMutex()
        {
            Need(LockDepth == 0 && released, "first ack requires released operation lease"); LockDepth++; Events.Add("lock:try");
            if (ReplaceOnAck && !replaced && State != null) { State.ProtectionSessionId = OtherToken; replaced = true; }
            if (Fail != "ack" && State != null && Monitor != null && Monitor.IsAlive) { State.MonitorHeartbeatQpcTicks = Timestamp.ToString(CultureInfo.InvariantCulture); Events.Add("ack:renew"); }
            return new Lease(this, false);
        }
        sealed class Lease : IDisposable
        {
            FakeContext owner; readonly bool main;
            public Lease(FakeContext owner, bool main) { this.owner = original = owner; this.main = main; }
            readonly FakeContext original;
            public void Dispose()
            {
                if (main && original != null) original.PrimaryReleaseInvocations++;
                if (owner == null) return;
                FakeContext current = owner; owner = null; current.LockDepth--;
                if (main) {
                    current.released = true; current.Events.Add("lock:release");
                    if (current.ThrowOnRelease) { current.ThrowOnRelease = false; throw new IOException("injected operation lease release failure"); }
                }
                else current.Events.Add("lock:short-release");
            }
        }
        void Trace(string value) { Events.Add(Regex.Replace(value.Replace(Root, "<root>"), "(?i)[0-9a-f]{32}", "<token>")); }
        void Need(bool ok, string message) { if (!ok) { LockViolations++; throw new InvalidOperationException(message); } }
        void Locked() { Need(LockDepth == 1, "state/phone/owned process operation retained under action lease"); }
        Exception Unexpected(string operation) { Illegal++; return new InvalidOperationException("Unexpected fake operation: " + operation); }
        public override Dictionary<string, object> ReadConfig() { Locked(); Trace("config:read"); return new Dictionary<string, object>(Config); }
        public override RuntimeState ReadState() { Locked(); Trace("state:read"); return Clone(State); }
        // This fixture models the normal online Stop protocol, never real process/TCP inspection.
        public override StaleSessionInspection InspectStaleSession(RuntimeState state)
        { Locked(); return new StaleSessionInspection(false, true, null); }
        public override void WriteState(RuntimeState state)
        {
            Locked(); writes++; if (Fail == "state-first" && writes == 1) throw new IOException("injected first state failure");
            State = Clone(state); Events.Add("state:write");
        }
        public override void DeleteStateIfToken(string token)
        {
            Locked(); if (ReplaceOnDelete && State != null) State.ProtectionSessionId = OtherToken;
            if (State != null && State.ProtectionSessionId == token) { State = null; Deletes++; Events.Add("state:delete"); }
        }
        static RuntimeState Clone(RuntimeState source)
        {
            if (source == null) return null; JavaScriptSerializer json = new JavaScriptSerializer(); RuntimeState target = json.Deserialize<RuntimeState>(json.Serialize(source));
            target.RawFields = source.RawFields == null ? null : new Dictionary<string, object>(source.RawFields); return target;
        }
        public override NetworkLink ResolveNetwork(IDictionary<string, object> config, RuntimeState expected = null, bool wifiOnly = false)
        { Locked(); Trace("network:resolve:wifiOnly=" + wifiOnly); return new NetworkLink { PhoneGateway = "192.0.2.1", WifiInterfaceIndex = 11, EthernetInterfaceIndex = 22, WifiInterfaceGuid = "00000000-0000-0000-0000-000000000011", EthernetInterfaceGuid = "00000000-0000-0000-0000-000000000022", WifiRouteCost = 30, EthernetRouteCost = 25 }; }
        public override bool WifiHasIPv6Default(int index) { Locked(); Trace("network:ipv6default:" + index); return false; }
        public override Process GetOwnedRelay(RuntimeState state, bool pinHandle = false)
        { Locked(); Trace("relay:ownership:state=" + (state != null) + ":pinned=" + pinHandle); if (pinHandle) { RelayPins++; Events.Add("relay:pin"); } return base.GetOwnedRelay(state, pinHandle); }
        public override Process GetOwnedMonitor(RuntimeState state, bool pinHandle = false)
        { Locked(); Trace("monitor:ownership:state=" + (state != null) + ":pinned=" + pinHandle); if (pinHandle && state != null && state.WatchdogProcessId > 0) Events.Add("monitor:pin"); return base.GetOwnedMonitor(state, pinHandle); }
        public override int[] GetTcpPortOwners(int port, bool established = false)
        {
            Locked(); if (port != 31416) throw Unexpected("TCP port"); Trace("tcp:owners:established=" + established);
            var ids = new List<int>();
            if (Relay != null && Relay.IsAlive && (established ? Vpn && ReverseDestination == "tcp:31416" : Fail != "listen")) ids.Add(Relay.Id);
            if (!established && Extra != null && Extra.IsAlive) ids.Add(Extra.Id); return ids.ToArray();
        }
        public override Process StartDetached(string executable, string[] arguments)
        {
            Locked();
            if (executable == RelayHostPath) {
                using (Process self = Process.GetCurrentProcess()) Need(arguments.Length == 3 && Regex.IsMatch(arguments[0], "^[0-9a-f]{32}$") && arguments[1] == self.Id.ToString(CultureInfo.InvariantCulture) && arguments[2] == self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), "host startup protocol pins action starter");
                HostStarts++; Events.Add("host:start"); if (Fail == "host-create") throw new IOException("injected host failure");
                Relay = AddChild(RelayPath); Child host = AddChild(RelayHostPath);
                RuntimeState ready = new RuntimeState { RelayPath = RelayPath, RelayProcessId = Relay.Id, RelayStartTimeUtcTicks = Relay.Ticks,
                    RelayHostProcessId = host.Id, RelayHostStartTimeUtcTicks = host.Ticks, RelayLogPipe = "WirelessInternet.Log." + arguments[0], LogMode = "MemoryOnly" };
                if (Fail != "host-ready") File.WriteAllText(Path.Combine(Root, "state", "relay-ready-" + arguments[0] + ".json"), new JavaScriptSerializer().Serialize(ready));
                return host.Get();
            }
            if (executable == WorkerPath && arguments.Length == 4 && arguments[0] == "--monitor" && arguments[2] == "--root" && arguments[3] == Root) {
                Need(State != null && arguments[1] == State.ProtectionSessionId && State.ProtectionMode == "PcOnly", "monitor startup carries current PC-only token");
                MonitorStarts++; Events.Add("monitor:start"); if (Fail == "monitor-create") throw new IOException("injected monitor failure");
                Monitor = AddChild(WorkerPath); return Monitor.Get();
            }
            throw Unexpected("detached " + executable);
        }
        public override RuntimeCommandResult Adb(string[] args, bool allowFailure = false, int timeoutMs = 8000)
        {
            Locked(); string command = String.Join(" ", args); Trace("adb:args:" + command);
            if (command == "mdns services") return Reply(0, Discovery);
            if (args.Length == 2 && args[0] == "connect") { Connects++; Events.Add("adb:connect"); return Reply(0, "connected"); }
            if (args.Length == 2 && args[0] == "disconnect") { Events.Add("adb:disconnect"); return Reply(Fail == "disconnect" ? 1 : 0, Fail == "disconnect" ? "injected disconnect warning" : "disconnected"); }
            if (args.Length >= 3 && args[0] == "-s" && args[1] == Serial) {
                string[] tail = args.Skip(2).ToArray(); string rest = String.Join(" ", tail);
                if (rest == "shell getprop ro.serialno") return Reply(0, ReturnedIdentity);
                if (rest == "shell am get-current-user") return Reply(0, OwnerUser);
                if (rest == "shell settings get global adb_wifi_enabled") return Reply(0, WirelessEnabled);
                if (rest == "shell settings get secure always_on_vpn_app") return Reply(0, AlwaysOn);
                if (rest == "shell settings get secure always_on_vpn_lockdown") return Reply(0, "0");
                if (rest == "shell dumpsys vpn_management") return Reply(0, "VPN Managers:\n  0:\n    2026-01-01T00:00:00 - [LockdownAlwaysOn] Mode changed: lockdown=" + (RuntimeRestrictions ? "true" : "false") + " alwaysOn=false\n");
                if (rest == "reverse --list") return Reply(0, ReverseDestination == null ? "" : "fixture localabstract:gnirehtet " + ReverseDestination);
                if (rest == "reverse --remove localabstract:gnirehtet") { Need(ReverseDestination == "tcp:31416", "only own reverse destination removed"); ReverseRemoves++; ReverseDestination = null; return Reply(0, ""); }
            }
            throw Unexpected("ADB " + command);
        }
        public override RuntimeCommandResult Gnirehtet(string[] args, bool allowFailure = false, int timeoutMs = 8000)
        {
            Locked(); if (args.Length != 2 || args[1] != Serial) throw Unexpected("gnirehtet args");
            Trace("gnirehtet:args:" + String.Join(" ", args));
            Events.Add("gnirehtet:" + args[0]);
            if (args[0] == "tunnel") { if (Fail == "tunnel-offline") throw new IOException("injected phone disconnect"); ReverseDestination = "tcp:31416"; return Reply(0, "tunnel"); }
            if (args[0] == "start") { GnirehtetStarts++; Vpn = Fail != "vpn-denied"; return Reply(0, "start"); }
            if (args[0] == "stop") { GnirehtetStops++; if (Fail != "vpn-stop") Vpn = false; return Reply(0, "stop"); }
            throw Unexpected("gnirehtet action");
        }
        public override bool PhoneVpnConnected(string serial) { Locked(); Trace("phone:vpn-observation"); Need(serial == Serial, "configured phone only"); if (Fail == "vpn-observation") throw new IOException("injected unavailable VPN observation"); return Vpn; }
        public override RuntimeCommandResult Run(string executable, string[] arguments, bool allowFailure = false, int timeoutMs = 8000, IDictionary<string, string> environment = null) { throw Unexpected("real executable"); }
        public void Dispose() { foreach (Child child in Children) child.Dispose(); }
    }
}
