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
        string report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetDirectoryName(Executable), "connection-lifecycle-results.json");
        fixtures = CreateFixtureDirectory(Path.GetDirectoryName(report), "lc-");

        Case("fresh Start commits owned resources and releases core only before first monitor acknowledgement", delegate(FakeContext c) {
            Equal(0, Start(c), "start result");
            Check(c.State != null && c.State.ProtectionTimeoutSeconds == 30, "state retains default thirty second timeout");
            Equal(Serial, c.State.WirelessSerial, "mDNS advertised address is replaced by selected hotspot gateway");
            Equal(1, c.HostStarts, "one relay host"); Equal(1, c.MonitorStarts, "one monitor");
            Equal(1, c.Arms, "one guard arm"); Equal(1, c.Beats, "one startup heartbeat");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn && c.GuardArmed, "all committed resources remain active");
            Before(c, "guard:arm", "state:write", "guard before state commit");
            Before(c, "state:write", "gnirehtet:tunnel", "state before tunnel");
            Before(c, "gnirehtet:start", "guard:beat", "VPN before startup heartbeat");
            Before(c, "guard:beat", "monitor:start", "heartbeat before monitor");
            Before(c, "monitor:start", "lock:release", "monitor recorded before operation lease release");
            Before(c, "lock:release", "lock:try", "first acknowledgement is observed under a new short lease");
            Check(c.Events.Any(x => x == "ack:renew"), "fake independent monitor renews only after release");
            Check(c.Output.Any(x => x.Contains("about 30 seconds")), "public progress reports unchanged timeout");
        });
        Case("repeated Start reuses a healthy owned relay monitor and phone guard", delegate(FakeContext c) {
            Equal(0, Start(c), "first start"); int relay = c.State.RelayProcessId, monitor = c.State.WatchdogProcessId; string token = c.State.ProtectionSessionId;
            c.Output.Clear(); Equal(0, Start(c), "repeated start");
            Equal(relay, c.State.RelayProcessId, "same relay"); Equal(monitor, c.State.WatchdogProcessId, "same monitor"); Equal(token, c.State.ProtectionSessionId, "same token");
            Equal(1, c.HostStarts, "no extra host"); Equal(1, c.MonitorStarts, "no extra monitor"); Equal(1, c.Arms, "no extra guard"); Equal(0, c.GuardStops, "healthy guard not stopped");
        });
        Case("Stop verifies VPN shutdown before guard cancellation and owned process cleanup; repeated Stop succeeds", delegate(FakeContext c) {
            Equal(0, Start(c), "setup start"); c.Events.Clear();
            Equal(0, Stop(c), "stop result"); Check(c.State == null && !c.Relay.IsAlive && !c.Monitor.IsAlive && !c.Vpn, "owned resources stopped and state removed");
            Before(c, "gnirehtet:stop", "guard:cancel", "VPN stop before cancellation");
            Before(c, "guard:cancel", "monitor:pin", "cancellation before monitor cleanup");
            Before(c, "monitor:pin", "relay:pin", "monitor before relay cleanup");
            Before(c, "relay:pin", "state:delete", "relay before state delete");
            Before(c, "state:delete", "adb:disconnect", "state before transport disconnect");
            Equal(0, Stop(c), "idempotent repeated stop"); Equal(1, c.Deletes, "state removed only once");
        });
        Case("wrong phone identity fails before resource creation", delegate(FakeContext c) {
            c.ReturnedIdentity = "OTHER_PHONE"; Equal(1, Start(c), "rejected"); NoCreated(c); Equal(0, c.Arms, "no phone guard");
        });
        Case("phone non-owner profile is rejected", delegate(FakeContext c) { c.OwnerUser = "10"; Equal(1, Start(c), "rejected"); NoCreated(c); });
        Case("wireless debugging disabled is rejected without changing settings", delegate(FakeContext c) { c.WirelessEnabled = "0"; Equal(1, Start(c), "rejected"); NoCreated(c); });
        Case("VPN restrictions reject Start without changing existing resources", delegate(FakeContext c) {
            c.AlwaysOn = "com.genymobile.gnirehtet"; Equal(1, Start(c), "rejected"); NoCreated(c); Equal(0, c.GnirehtetStarts, "no VPN start");
        });
        Case("untracked listener prevents Start and remains alive", delegate(FakeContext c) {
            c.Extra = c.AddChild(c.RelayHostPath); Equal(1, Start(c), "rejected"); NoCreated(c); Check(c.Extra.IsAlive, "unrelated fixture listener not killed");
        });
        Case("relay host creation failure leaves no phone or session state", delegate(FakeContext c) {
            c.Fail = "host-create"; Equal(1, Start(c), "failure"); Equal(0, c.Arms, "no guard"); Check(c.State == null, "no state");
        });
        Case("relay readiness timeout leaves unaccepted host to its own protocol", delegate(FakeContext c) {
            c.Fail = "host-ready"; Equal(1, Start(c), "failure"); Equal(60, c.ReadinessSleeps, "bounded sixty readiness waits");
            Check(c.State == null && c.Arms == 0 && c.RelayPins == 0, "no guard state or direct kill of unaccepted child");
        });
        Case("guard arming failure cleans only unsaved newly owned relay", delegate(FakeContext c) {
            c.Fail = "guard-arm"; Equal(1, Start(c), "failure"); Equal(1, c.GuardStops, "new guard stop attempted");
            Check(c.State == null && !c.Relay.IsAlive, "unsaved relay removed"); Equal(1, c.RelayPins, "one exact pinned relay cleanup");
        });
        Case("first state write failure rolls back unsaved resources", delegate(FakeContext c) {
            c.Fail = "state-first"; Equal(1, Start(c), "failure"); Check(c.State == null && !c.Relay.IsAlive && !c.GuardArmed, "new guard and unsaved relay cleaned");
            Equal(0, c.GnirehtetStarts, "tunnel not started before state commit");
        });
        Case("listener readiness failure retains committed record for normal Stop", delegate(FakeContext c) {
            c.Fail = "listen"; Equal(1, Start(c), "failure"); Check(c.State != null && c.Relay.IsAlive && !c.GuardArmed, "committed relay retained with guard stopped");
            Equal(0, c.RelayPins, "no committed relay killed in catch"); Equal(0, c.MonitorStarts, "no monitor before listener ready");
        });
        Case("phone VPN refusal retains committed relay and cleans new guard", delegate(FakeContext c) {
            c.Fail = "vpn-denied"; Equal(1, Start(c), "failure"); Check(c.State != null && c.Relay.IsAlive && !c.Vpn && !c.GuardArmed, "retained recorded relay");
            Equal(0, c.MonitorStarts, "no monitor for unready tunnel"); Equal(1, c.GuardStops, "guard stopped");
        });
        Case("phone disconnect after state commit retains recovery record and independent guard", delegate(FakeContext c) {
            c.Fail = "tunnel-offline"; Equal(1, Start(c), "failure");
            Check(c.State != null && c.Relay.IsAlive && c.GuardArmed, "recorded relay and unreachable independent guard retained");
            Equal(1, c.GuardStopAttempts, "best effort guard stop attempted"); Equal(0, c.GuardStops, "offline phone not claimed stopped");
            Equal(0, c.MonitorStarts, "unready tunnel cannot start monitor");
        });
        Case("startup heartbeat REFUSED does not start monitor or rearm guard", delegate(FakeContext c) {
            c.Fail = "beat-refused"; Equal(1, Start(c), "failure"); Equal(1, c.Beats, "one refused startup heartbeat"); Equal(1, c.Arms, "no rearm");
            Equal(0, c.MonitorStarts, "no monitor"); Check(c.State != null && c.Relay.IsAlive && !c.Vpn, "committed relay retained and new phone guard stopped");
        });
        Case("monitor creation failure retains committed relay and stops new phone guard", delegate(FakeContext c) {
            c.Fail = "monitor-create"; Equal(1, Start(c), "failure"); Check(c.State != null && c.Relay.IsAlive && !c.Vpn, "safe recovery record retained"); Equal(1, c.GuardStops, "new guard stopped");
        });
        Case("missing first acknowledged heartbeat times out after releasing operation lease", delegate(FakeContext c) {
            c.Fail = "ack"; Equal(1, Start(c), "failure"); Equal(60, c.AckSleeps, "bounded acknowledgement waits");
            Equal(1, c.LocklessGuardStops, "existing catch uses token-protected phone stop after releasing PC lease");
            Check(c.State != null && c.Relay.IsAlive && !c.Vpn, "committed recovery record retained");
        });
        Case("session replacement during first acknowledgement never stops new phone session", delegate(FakeContext c) {
            c.ReplaceOnAck = true; Equal(1, Start(c), "failure"); Equal(OtherToken, c.State.ProtectionSessionId, "new session state retained");
            Equal(1, c.GuardStopAttempts, "old cleanup attempt is token scoped"); Equal(0, c.GuardStops, "old guard cannot stop newer session");
            Check(c.Vpn && c.Relay.IsAlive, "newer phone session and relay remain active"); Equal(0, c.Deletes, "no state deletion");
        });
        Case("Stop with stale relay start ticks leaves process phone and state untouched", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.State.RelayStartTimeUtcTicks = "1"; Equal(1, Stop(c), "rejected");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn && c.State != null, "wrong identity not killed"); Equal(0, c.GnirehtetStops, "ownership checked before VPN stop");
        });
        Case("Stop with wrong relay executable path leaves all resources untouched", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.State.RelayPath = Path.Combine(c.Root, "different.exe"); Equal(1, Stop(c), "rejected");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn, "wrong path not killed"); Equal(0, c.GnirehtetStops, "no VPN mutation");
        });
        Case("Stop with stale monitor identity preserves monitor and relay for retry", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.State.WatchdogStartTimeUtcTicks = "1"; Equal(1, Stop(c), "rejected");
            Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.State != null, "stale monitor identity cannot kill process or delete record");
            Equal(0, c.RelayPins, "relay cleanup not reached after monitor identity failure");
        });
        Case("Stop refuses a self PID without terminating the test", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); using (Process self = Process.GetCurrentProcess()) { c.State.RelayProcessId = self.Id; c.State.RelayStartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture); }
            Equal(1, Stop(c), "self target rejected"); Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn, "fixture resources remain alive");
        });
        Case("Stop runtime restriction failure preserves VPN relay monitor and state", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.RuntimeRestrictions = true; Equal(1, Stop(c), "rejected");
            Check(c.Vpn && c.Relay.IsAlive && c.Monitor.IsAlive && c.State != null, "no cleanup before verified restrictions"); Equal(0, c.GnirehtetStops, "no phone stop");
        });
        Case("VPN that refuses Stop keeps guard processes and state for retry", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.Fail = "vpn-stop"; Equal(1, Stop(c), "failure");
            Check(c.Vpn && c.GuardArmed && c.Relay.IsAlive && c.Monitor.IsAlive && c.State != null, "unconfirmed VPN stop cannot clean independent protection"); Equal(0, c.Cancels, "guard not cancelled");
        });
        Case("failed VPN shutdown observation cannot cancel protection or clean process state", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.Fail = "vpn-observation"; Equal(1, Stop(c), "failure");
            Check(c.GuardArmed && c.Relay.IsAlive && c.Monitor.IsAlive && c.State != null, "uncertain observation keeps protection resources");
            Equal(0, c.Cancels, "no cancellation without verified VPN stop");
        });
        Case("Stop never removes another reverse destination", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.ReverseDestination = "tcp:39999"; Equal(0, Stop(c), "normal owned cleanup");
            Equal(0, c.ReverseRemoves, "different reverse preserved"); Equal("tcp:39999", c.ReverseDestination, "mapping unchanged");
        });
        Case("new listener during Stop is not killed and preserves recovery state", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.Extra = c.AddChild(c.RelayHostPath); Equal(1, Stop(c), "failure");
            Check(c.Relay.IsAlive && c.Extra.IsAlive && c.State != null, "owned relay and unknown listener not killed"); Equal(0, c.Deletes, "no deletion after ownership set changes");
        });
        Case("state token replacement at deletion preserves newer record", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.ReplaceOnDelete = true; Equal(0, Stop(c), "stop result");
            Equal(OtherToken, c.State.ProtectionSessionId, "new record retained by token predicate"); Equal(0, c.Deletes, "new record not deleted");
        });
        Case("disconnect transport failure is a warning after owned cleanup", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.Fail = "disconnect"; Equal(0, Stop(c), "successful cleanup despite disconnect warning");
            Check(c.State == null && !c.Relay.IsAlive && !c.Monitor.IsAlive, "resources cleaned"); Check(c.Output.Any(x => x.Contains("disconnect warning")), "warning surfaced");
        });
        Case("invalid timeout and ambiguous discovery fail before resources", delegate(FakeContext c) {
            c.Config["DisconnectTimeoutSeconds"] = 121; Equal(1, Start(c), "invalid timeout rejected"); NoCreated(c);
            c.Config["DisconnectTimeoutSeconds"] = 30; c.Discovery += "\nadb-" + Phone + "-second _adb-tls-connect._tcp. 203.0.113.9:39999";
            Equal(1, Start(c), "ambiguous discovery rejected"); NoCreated(c); Equal(5, c.DiscoverySleeps, "same six discovery attempts retained");
        });
        Case("saved endpoint from another hotspot cannot be used as fallback", delegate(FakeContext c) {
            c.Discovery = ""; c.Config["ConnectEndpoint"] = "192.0.2.99:37123"; Equal(1, Start(c), "gateway mismatch rejected"); NoCreated(c); Equal(0, c.Connects, "no connect to wrong gateway");
        });
        Case("legacy session and changed phone binding are refused before network work", delegate(FakeContext c) {
            Equal(0, Start(c), "setup"); c.State.RuntimeEngine = "PowerShell"; int previous = c.Connects;
            Equal(1, Start(c), "legacy engine rejected"); Equal(previous, c.Connects, "legacy record not reconnected");
            c.State.RuntimeEngine = "CSharp"; c.State.PhoneIdentity = "DIFFERENT_PHONE"; Equal(1, Start(c), "changed binding rejected");
            Equal(previous, c.Connects, "other phone not contacted"); Check(c.Relay.IsAlive && c.Monitor.IsAlive && c.Vpn, "old shared session retained");
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
        public int LockDepth, LockViolations, Illegal, HostStarts, MonitorStarts, Arms, Beats, GuardStops, GuardStopAttempts, LocklessGuardStops, Cancels, Deletes;
        public int GnirehtetStarts, GnirehtetStops, Connects, RelayPins, ReverseRemoves, ReadinessSleeps, AckSleeps, DiscoverySleeps;
        public string Fail = "", ReturnedIdentity = Phone, OwnerUser = "0", WirelessEnabled = "1", AlwaysOn = "null", PhoneToken = "", ReverseDestination;
        public string Discovery = "adb-" + Phone + "-fixture _adb-tls-connect._tcp. 203.0.113.5:37123";
        public bool Vpn, GuardArmed, RuntimeRestrictions, ReplaceOnAck, ReplaceOnDelete;
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
            if (ReplaceOnAck && !replaced && State != null) { State.ProtectionSessionId = OtherToken; PhoneToken = OtherToken; replaced = true; }
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
                Need(arguments[1] == PhoneToken, "monitor startup carries only current guard token");
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
                if (tail.Length == 4 && tail[0] == "shell" && tail[1] == "mkdir" && tail[2] == "-p" && tail[3].StartsWith("/data/local/tmp/codex-wireless-internet/", StringComparison.Ordinal)) return Reply(0, "");
                if (tail.Length == 3 && tail[0] == "push" && File.Exists(tail[1]) && tail[2].StartsWith("/data/local/tmp/codex-wireless-internet/", StringComparison.Ordinal)) return Reply(0, "pushed fixture guard");
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
        public override PhoneGuardLease ExtractPhoneGuard()
        {
            Locked(); Trace("guard:extract"); string directory = Path.Combine(Root, "guard-input"); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "control.sh");
            File.WriteAllText(path, PhoneGuardSource.Text.Replace("\r\n", "\n"), new UTF8Encoding(false));
            return new PhoneGuardLease(directory, path);
        }
        public override RuntimeCommandResult Guard(RuntimeState state, string command, bool allowFailure = false, int timeoutMs = 8000)
        {
            if (LockDepth != 1) { Need(command == "stop" && released, "only existing post-ack failure cleanup may be outside PC lease"); LocklessGuardStops++; }
            Need(ProtectionValid(state) && state.WirelessSerial == Serial, "guard token/path/phone valid"); Events.Add("guard:" + command);
            Trace("guard:args:" + command + ":timeout=" + state.ProtectionTimeoutSeconds + ":path=" + state.PhoneControlPath);
            string token = state.ProtectionSessionId;
            if (command == "arm") { Arms++; PhoneToken = token; GuardArmed = true; return Reply(0, (Fail == "guard-arm" ? "NOT_READY " : "ARMED ") + token); }
            if (command == "status") return Reply(GuardArmed && PhoneToken == token ? 0 : 1, (GuardArmed && PhoneToken == token ? "ACTIVE " : "STATE ") + token + " 1000 1000");
            if (command == "beat") { Beats++; return Reply(0, (Fail == "beat-refused" ? "REFUSED " + token + " EXPIRED" : "OK " + token)); }
            if (command == "stop") { GuardStopAttempts++; if (Fail == "tunnel-offline") return Reply(1, "phone offline"); if (PhoneToken != token) return Reply(2, "STATE " + token + " SUPERSEDED"); GuardStops++; GuardArmed = Vpn = false; return Reply(0, "STATE " + token + " STOPPED"); }
            if (command == "cancel") { Need(!Vpn && PhoneToken == token, "only cancelled own inactive VPN"); Cancels++; GuardArmed = false; return Reply(0, "CANCELLED " + token); }
            throw Unexpected("guard command");
        }
        public override RuntimeCommandResult Run(string executable, string[] arguments, bool allowFailure = false, int timeoutMs = 8000, IDictionary<string, string> environment = null) { throw Unexpected("real executable"); }
        public void Dispose() { foreach (Child child in Children) child.Dispose(); }
    }
}
