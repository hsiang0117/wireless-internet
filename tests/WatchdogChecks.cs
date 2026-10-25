using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using WirelessInternet;

// Isolated watchdog checks. The only real subprocesses are this executable's
// --fake-relay children. No SDK, ADB server, phone, adapter, or live state is used.
internal static class WatchdogChecks
{
    const string Token = "11111111111111111111111111111111";
    const string OtherToken = "22222222222222222222222222222222";
    const string Phone = "WATCHDOG_TEST";
    static readonly string Executable = typeof(WatchdogChecks).Assembly.Location;
    static readonly List<object> Results = new List<object>();
    static int assertions, failures;

    static int Main(string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] == "--fake-relay")
        {
            Console.WriteLine("READY " + arguments[1]);
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        Case("strict OK renews beyond initial deadline and writes QPC heartbeat", delegate(FakeContext c) {
            c.QuitAfterHeartbeatWrites = 8;
            Equal(0, Run(c), "run result");
            Equal(8, c.HeartbeatWrites, "heartbeat writes");
            Equal(8, c.Beats, "strict acknowledgements");
            Equal(35.0, c.HeartbeatTimes.Last(), "renewal past initial 30 seconds");
            Equal(0, c.Deletes, "no healthy cleanup");
            Equal(0, c.Stops, "no healthy phone stop");
            Check(c.Children[0].IsAlive, "healthy relay remains alive");
            Check(c.History.All(s => s.RawFields != null && (string)s.RawFields["unknownTestField"] == "preserved"), "unknown state data survives heartbeat writes");
        });

        Case("REFUSED acknowledgement is immediately terminal", delegate(FakeContext c) {
            c.BeatReply = delegate { return Reply(1, "REFUSED " + Token + " EXPIRED"); };
            Equal(0, Run(c), "run result");
            Equal(1, c.Beats, "one terminal beat");
            Equal(0, c.HeartbeatWrites, "REFUSED cannot renew");
            Equal(0.0, c.Seconds, "immediate cleanup");
            AssertCleaned(c);
        });

        Case("empty acknowledgement never renews and expires at 30 seconds", delegate(FakeContext c) {
            c.BeatReply = delegate { return Reply(0, ""); };
            Equal(0, Run(c), "run result");
            Equal(6, c.Beats, "five-second unhealthy sweeps");
            Equal(0, c.HeartbeatWrites, "empty acknowledgement cannot renew");
            Equal(30.0, c.Seconds, "monotonic deadline");
            AssertCleaned(c);
        });

        Case("late OK acknowledgement cannot revive an expired monitor", delegate(FakeContext c) {
            c.BeatReply = delegate { c.Advance(31); return Reply(0, "OK " + Token); };
            Equal(0, Run(c), "run result");
            Equal(1, c.Beats, "late answer is terminal");
            Equal(0, c.HeartbeatWrites, "no late heartbeat persistence");
            Equal(31.0, c.Seconds, "late-response clock");
            AssertCleaned(c);
        });

        Case("same reverse name with another destination is never rebound", delegate(FakeContext c) {
            c.ReverseDestination = "tcp:31337";
            Equal(0, Run(c), "run result");
            Equal(0, c.ReverseRepairs, "no conflicting reverse overwrite");
            Equal(0, c.Beats, "conflict prevents renewal");
            Equal(30.0, c.Seconds, "conflict expires normally");
            AssertCleaned(c);
        });

        Case("missing owned reverse uses no-rebind and is verified", delegate(FakeContext c) {
            c.ReverseDestination = null;
            c.QuitAfterHeartbeatWrites = 8;
            Equal(0, Run(c), "run result");
            Equal(1, c.ReverseRepairs, "one safe reverse repair");
            Equal("tcp:31416", c.ReverseDestination, "exact reverse destination");
            Equal(8, c.HeartbeatWrites, "healthy after repair");
            Check(c.Children[0].IsAlive, "repair retains relay");
        });

        Case("changed session token exits without cleanup", delegate(FakeContext c) {
            c.State.ProtectionSessionId = OtherToken;
            AssertReplacementExit(c);
        });

        Case("changed monitor PID exits without cleanup", delegate(FakeContext c) {
            c.State.WatchdogProcessId++;
            AssertReplacementExit(c);
        });

        Case("changed monitor start ticks exits without cleanup", delegate(FakeContext c) {
            c.State.WatchdogStartTimeUtcTicks = (Int64.Parse(c.State.WatchdogStartTimeUtcTicks, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
            AssertReplacementExit(c);
        });

        Case("session replacement before beat prevents phone mutation", delegate(FakeContext c) {
            c.BeforeRead = delegate {
                if (c.VpnReads != 0) c.State.ProtectionSessionId = OtherToken;
            };
            Equal(0, Run(c), "run result");
            Equal(0, c.Beats, "recheck rejects beat");
            Equal(0, c.Stops, "replacement phone is not stopped");
            Equal(0, c.Deletes, "replacement state remains");
            Check(c.Children[0].IsAlive, "replacement relay remains");
        });

        Case("terminal cleanup retries busy mutex without another beat", delegate(FakeContext c) {
            c.BeatReply = delegate {
                c.BusyMutexAttempts = 2;
                return Reply(1, "REFUSED " + Token + " CANCELLED");
            };
            Equal(0, Run(c), "run result");
            Equal(2, c.BusyMutexReturns, "cleanup mutex retried");
            Equal(1.0, c.Seconds, "bounded cleanup retry interval");
            Equal(1, c.Beats, "no beat after terminal refusal");
            Equal(0, c.HeartbeatWrites, "no terminal renewal");
            AssertCleaned(c);
        });

        Case("deadline remains terminal when cleanup sees a longer timeout", delegate(FakeContext c) {
            c.BeatReply = delegate { return Reply(0, ""); };
            bool changed = false;
            c.BeforeRead = delegate {
                if (!changed && c.Seconds >= 30) {
                    changed = true;
                    c.State.ProtectionTimeoutSeconds = 120;
                    c.BusyMutexAttempts = 1;
                }
            };
            Equal(0, Run(c), "run result");
            Check(changed, "cleanup observed updated timeout");
            Equal(6, c.Beats, "no restart after original expiry");
            Equal(1, c.BusyMutexReturns, "cleanup mutation retry");
            Equal(30.5, c.Seconds, "sticky terminal cleanup");
            AssertCleaned(c);
        });

        Case("wrong relay PID ownership never kills either child", delegate(FakeContext c) {
            Child decoy = c.AddChild();
            c.State.RelayProcessId = decoy.Id;
            c.State.RelayStartTimeUtcTicks = decoy.StartTicks;
            Equal(0, Run(c), "run result");
            Equal(0, c.Beats, "unowned relay cannot renew");
            Equal(0, c.PinnedRelayRequests, "unowned relay is never selected for kill");
            Check(c.Children[0].IsAlive && decoy.IsAlive, "both tracked test children remain alive");
            Equal(1, c.Deletes, "only expired own-token state removed");
        });

        Case("stale relay start ticks cannot target a recycled PID", delegate(FakeContext c) {
            c.State.RelayStartTimeUtcTicks = (Int64.Parse(c.State.RelayStartTimeUtcTicks, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
            Equal(0, Run(c), "run result");
            Equal(0, c.Beats, "stale identity cannot renew");
            Equal(0, c.PinnedRelayRequests, "stale identity never reaches kill");
            Check(c.Children[0].IsAlive, "PID reuse protection retains unrelated lifetime");
        });

        Case("native reconnection verifies identity and persists actual endpoint", delegate(FakeContext c) {
            c.FailInitialIdentity = true;
            c.DiscoveryText = "adb-" + Phone + "-native _adb-tls-connect._tcp. 192.0.2.90:40123";
            c.QuitAfterHeartbeatWrites = 8;
            Equal(0, Run(c), "run result");
            Equal(1, c.Connects, "one native reconnect");
            Equal("192.0.2.1:40123", c.LastConnectedSerial, "dial verified hotspot gateway with discovered port");
            Equal(1, c.EndpointWrites, "one atomic endpoint update");
            Equal(8, c.HeartbeatWrites, "reconnected strict heartbeats");
            Equal(c.LastConnectedSerial, c.State.ConnectEndpoint, "persisted endpoint");
            Check(c.Children[0].IsAlive, "reconnection never replaces relay");
        });

        Case("wrong phone identity prevents reconnect and phone stop", delegate(FakeContext c) {
            c.ReturnedPhoneIdentity = "ANOTHER_PHONE";
            Equal(0, Run(c), "run result");
            Equal(0, c.Connects, "no reconnect over a verified wrong phone");
            Equal(0, c.Beats, "no heartbeat to wrong phone");
            Equal(0, c.Stops, "no stop sent to wrong phone");
            Equal(1, c.Deletes, "own expired state cleaned");
            Check(!c.Children[0].IsAlive, "owned expired relay is stopped");
        });

        Case("another port listener prevents renewal without killing it", delegate(FakeContext c) {
            Child decoy = c.AddChild();
            c.ExtraListener = decoy.Id;
            Equal(0, Run(c), "run result");
            Equal(0, c.Beats, "ambiguous listener cannot renew");
            Check(!c.Children[0].IsAlive, "owned expired relay stopped");
            Check(decoy.IsAlive, "other listener process retained");
        });

        Case("missing established phone socket cannot renew or restart VPN", delegate(FakeContext c) {
            c.HasEstablishedConnection = false;
            Equal(0, Run(c), "run result");
            Equal(0, c.Beats, "no established socket prevents renewal");
            Equal(30.0, c.Seconds, "independent expiry");
            AssertCleaned(c);
        });

        Case("unexpected successful acknowledgement is terminal", delegate(FakeContext c) {
            c.BeatReply = delegate { return Reply(0, "OK " + OtherToken); };
            Equal(0, Run(c), "run result");
            Equal(1, c.Beats, "unexpected response ends renewal");
            Equal(0, c.HeartbeatWrites, "wrong token cannot renew");
            Equal(0.0, c.Seconds, "unexpected nonempty success is terminal");
            AssertCleaned(c);
        });

        Console.WriteLine(new JavaScriptSerializer().Serialize(new {
            suite = "WatchdogChecks", cases = Results.Count, assertions = assertions,
            failures = failures, realAdbCalls = 0, results = Results
        }));
        return failures == 0 ? 0 : 1;
    }

    static int Run(FakeContext context) { return new WatchdogController(context).Run(Token); }

    static void AssertReplacementExit(FakeContext c)
    {
        Equal(0, Run(c), "run result");
        Equal(0, c.Commands.Count, "no ADB action after replacement");
        Equal(0, c.Stops, "no phone cleanup");
        Equal(0, c.Deletes, "no state deletion");
        Check(c.Children[0].IsAlive, "replacement does not kill relay");
    }

    static void AssertCleaned(FakeContext c)
    {
        Equal(1, c.Stops, "one owned-phone stop");
        Equal(1, c.Deletes, "one own-token state deletion");
        Equal(1, c.PinnedRelayRequests, "one pinned relay kill selection");
        Check(!c.Children[0].IsAlive, "owned expired relay stopped");
    }

    static void Case(string name, Action<FakeContext> test)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        int before = assertions;
        string error = null;
        FakeContext context = null;
        try {
            using (context = new FakeContext()) {
                test(context);
                Equal(0, context.Violations.Count, "all fake I/O and short-lock contracts: " + String.Join("; ", context.Violations));
                Equal(0, context.ForbiddenActions, "no VPN start, guard arm, global ADB action, or external program");
                Equal(0, context.LockDepth, "all mutex leases released");
            }
        }
        catch (Exception ex) { failures++; error = ex.ToString(); }
        Results.Add(new {
            name = name, passed = error == null, assertions = assertions - before,
            virtualSeconds = context == null ? 0 : context.Seconds,
            elapsedMilliseconds = elapsed.ElapsedMilliseconds, error = error,
            violations = context == null ? null : context.Violations.ToArray(),
            commands = error != null && context != null ? context.Commands.ToArray() : null
        });
    }

    static void Check(bool value, string message)
    {
        assertions++;
        if (!value) throw new InvalidOperationException(message);
    }

    static void Equal<T>(T expected, T actual, string message)
    {
        Check(EqualityComparer<T>.Default.Equals(expected, actual), message + ": expected " + expected + ", actual " + actual);
    }

    static RuntimeCommandResult Reply(int exitCode, string text) { return new RuntimeCommandResult { ExitCode = exitCode, Output = text }; }

    sealed class Child : IDisposable
    {
        readonly Process process;
        public readonly int Id;
        public readonly string StartTicks;
        public Child()
        {
            string nonce = Guid.NewGuid().ToString("N");
            process = Process.Start(new ProcessStartInfo(Executable, "--fake-relay " + nonce) {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
            });
            try {
                // Pin this lifetime before storing its ID. Only this object is torn down.
                IntPtr pin = process.Handle;
                Id = process.Id;
                StartTicks = process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                var ready = process.StandardOutput.ReadLineAsync();
                if (!ready.Wait(3000) || ready.Result != "READY " + nonce)
                    throw new InvalidOperationException("Fake relay did not become ready.");
                if (!String.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(Executable), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Fake relay executable identity differs.");
            }
            catch { Dispose(); throw; }
        }
        public bool IsAlive { get { return !process.HasExited; } }
        public Process GetVerifiedProcess(bool pin)
        {
            return RuntimeContext.GetOwnedProcess(Id, StartTicks, Executable, pin, true);
        }
        public void Dispose()
        {
            try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } }
            finally { process.Dispose(); }
        }
    }

    sealed class FakeContext : RuntimeContext, IDisposable
    {
        public readonly List<Child> Children = new List<Child>();
        public readonly List<string> Commands = new List<string>();
        public readonly List<string> Violations = new List<string>();
        public readonly List<RuntimeState> History = new List<RuntimeState>();
        public readonly List<double> HeartbeatTimes = new List<double>();
        public RuntimeState State;
        public Action BeforeRead;
        public Func<RuntimeCommandResult> BeatReply;
        public int Beats, Stops, Deletes, HeartbeatWrites, EndpointWrites, ReverseRepairs, Connects, VpnReads;
        public int PinnedRelayRequests, ForbiddenActions, LockDepth, BusyMutexAttempts, BusyMutexReturns;
        public int QuitAfterHeartbeatWrites, ExtraListener;
        public string ReverseDestination = "tcp:31416", ReturnedPhoneIdentity = Phone;
        public string DiscoveryText = "", LastConnectedSerial;
        public bool FailInitialIdentity, HasEstablishedConnection = true;
        public double Seconds;
        int sleeps;

        public FakeContext() : base(Path.Combine(Path.GetTempPath(), "WirelessInternet-WatchdogChecks-" + Guid.NewGuid().ToString("N")))
        {
            Child relay = AddChild();
            using (Process current = Process.GetCurrentProcess()) {
                State = new RuntimeState {
                    Transport = "NativeTls", ProtectionSessionId = Token,
                    ProtectionTimeoutSeconds = 30, PhoneIdentity = Phone,
                    WirelessSerial = "192.0.2.1:40111", ConnectEndpoint = "192.0.2.1:40111", PhoneGateway = "192.0.2.1",
                    WifiInterfaceIndex = 11, EthernetInterfaceIndex = 22,
                    WifiInterfaceGuid = "00000000-0000-0000-0000-000000000011",
                    EthernetInterfaceGuid = "00000000-0000-0000-0000-000000000022",
                    WifiInterfaceAlias = "TEST_WIFI", EthernetInterfaceAlias = "TEST_ETHERNET",
                    WifiRouteCost = 30, EthernetRouteCost = 25,
                    RelayPath = RelayPath, RelayProcessId = relay.Id, RelayStartTimeUtcTicks = relay.StartTicks,
                    WatchdogProcessId = current.Id,
                    WatchdogStartTimeUtcTicks = current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                    WatchdogPath = WorkerPath, RuntimeEngine = "CSharp",
                    PhoneControlPath = "/data/local/tmp/codex-wireless-internet/" + Token + "/control.sh",
                    RawFields = new Dictionary<string, object> { { "unknownTestField", "preserved" } }
                };
            }
        }

        public Child AddChild() { Child child = new Child(); Children.Add(child); return child; }
        public void Advance(double seconds) { Seconds += seconds; }
        public override long Timestamp { get { return 10000000000L + (long)Math.Round(Seconds * Stopwatch.Frequency); } }
        public override double ElapsedSeconds(long since) { return (Timestamp - since) / (double)Stopwatch.Frequency; }
        public override void Sleep(int milliseconds)
        {
            Require(LockDepth == 0, "sleep cannot hold core mutex");
            Require(milliseconds > 0 && milliseconds <= 5000, "bounded monitor sleep");
            if (++sleeps > 200 || Seconds > 600) throw new InvalidOperationException("Fake watchdog did not terminate.");
            Advance(milliseconds / 1000.0);
        }

        public override IDisposable TryEnterCoreMutex()
        {
            if (BusyMutexAttempts > 0) { BusyMutexAttempts--; BusyMutexReturns++; return null; }
            Require(LockDepth == 0, "no recursive mutex lease");
            LockDepth++;
            return new Lease(this);
        }
        public override IDisposable EnterCoreMutex(int timeoutMs = 6000)
        {
            throw Forbidden("blocking core-mutex API");
        }
        sealed class Lease : IDisposable
        {
            FakeContext owner;
            public Lease(FakeContext owner) { this.owner = owner; }
            public void Dispose() { if (owner != null) { owner.LockDepth--; owner = null; } }
        }

        public override RuntimeState ReadState()
        {
            Require(LockDepth == 1, "state read requires short core lease");
            if (BeforeRead != null) BeforeRead();
            if (QuitAfterHeartbeatWrites > 0 && HeartbeatWrites >= QuitAfterHeartbeatWrites && State != null)
                State.ProtectionSessionId = OtherToken;
            return Clone(State);
        }
        public override void WriteState(RuntimeState state)
        {
            Require(LockDepth == 1, "atomic state write requires core lease");
            if (State != null && state.MonitorHeartbeatQpcTicks != State.MonitorHeartbeatQpcTicks) {
                Require(state.MonitorHeartbeatQpcTicks == Timestamp.ToString(CultureInfo.InvariantCulture), "heartbeat QPC equals observation clock");
                HeartbeatWrites++;
                HeartbeatTimes.Add(Seconds);
            }
            if (State != null && state.WirelessSerial != State.WirelessSerial) EndpointWrites++;
            State = Clone(state);
            History.Add(Clone(state));
        }
        public override void DeleteStateIfToken(string token)
        {
            Require(LockDepth == 1, "state delete requires core lease");
            Require(State != null && State.ProtectionSessionId == token, "only same-token state deletion");
            Deletes++;
            State = null;
        }
        static RuntimeState Clone(RuntimeState source)
        {
            if (source == null) return null;
            JavaScriptSerializer json = new JavaScriptSerializer();
            RuntimeState copy = json.Deserialize<RuntimeState>(json.Serialize(source));
            copy.RawFields = source.RawFields == null ? null : new Dictionary<string, object>(source.RawFields);
            return copy;
        }

        public override NetworkLink ResolveNetwork(IDictionary<string, object> config, RuntimeState expected = null, bool wifiOnly = false)
        {
            Require(LockDepth == 0, "network observation outside core lease");
            Require(config.Count == 2 && (int)config["WifiInterfaceIndex"] == 11 && (int)config["EthernetInterfaceIndex"] == 22, "monitor pins only session interfaces");
            Require(expected != null && expected.WifiInterfaceGuid == State.WifiInterfaceGuid && expected.EthernetInterfaceGuid == State.EthernetInterfaceGuid, "network observation pins session GUIDs");
            Require(!wifiOnly, "monitor requires wired upstream validation");
            return new NetworkLink { PhoneGateway = "192.0.2.1" };
        }
        public override Process GetOwnedRelay(RuntimeState state, bool pinHandle = false)
        {
            Child relay = Children[0];
            if (state.RelayProcessId != relay.Id || state.RelayStartTimeUtcTicks != relay.StartTicks || state.RelayPath != RelayPath)
                throw new IOException("Fake relay full ownership mismatch.");
            if (pinHandle) {
                Require(LockDepth == 1, "relay kill handle selected under final core lease");
                PinnedRelayRequests++;
            }
            return relay.GetVerifiedProcess(pinHandle);
        }
        public override int[] GetTcpPortOwners(int port, bool established = false)
        {
            Require(port == 31416, "only the protected relay port is observed");
            if (established) return HasEstablishedConnection ? new int[] { Children[0].Id } : new int[0];
            return ExtraListener == 0 ? new int[] { Children[0].Id } : new int[] { Children[0].Id, ExtraListener };
        }

        public override RuntimeCommandResult Adb(string[] arguments, bool allowFailure = false, int timeoutMs = 8000)
        {
            Commands.Add(String.Join(" ", arguments));
            Require(allowFailure, "monitor commands use explicit best-effort results");
            Require(timeoutMs > 0 && timeoutMs <= 5000, "bounded command timeout");
            if (Is(arguments, "mdns", "services")) return Reply(0, DiscoveryText);
            if (arguments.Length == 2 && arguments[0] == "connect") {
                RequireMutation(); Connects++; LastConnectedSerial = arguments[1];
                return Reply(0, "connected to " + arguments[1]);
            }
            if (arguments.Length >= 3 && arguments[0] == "-s") {
                string[] command = arguments.Skip(2).ToArray();
                if (Is(command, "shell", "getprop", "ro.serialno")) {
                    if (FailInitialIdentity && Connects == 0) return Reply(1, "");
                    return Reply(0, ReturnedPhoneIdentity);
                }
                if (Is(command, "reverse", "--list"))
                    return Reply(0, ReverseDestination == null ? "" : "fake-transport localabstract:gnirehtet " + ReverseDestination);
                if (Is(command, "reverse", "--no-rebind", "localabstract:gnirehtet", "tcp:31416")) {
                    RequireMutation();
                    Require(ReverseDestination == null, "reverse no-rebind must not replace any existing mapping");
                    ReverseRepairs++; ReverseDestination = "tcp:31416"; return Reply(0, "");
                }
                if (Is(command, "shell", "dumpsys", "connectivity")) {
                    VpnReads++;
                    return Reply(0, "Current Networks:\n  NetworkAgentInfo{network{42} ni{VPN CONNECTED extra: VPN:com.genymobile.gnirehtet} lp{InterfaceName: tun0}");
                }
                if (command.Length == 5 && command[0] == "shell" && command[1] == "sh" &&
                    command[2] == "/data/local/tmp/codex-wireless-internet/" + Token + "/control.sh" && command[4] == Token) {
                    RequireMutation();
                    if (command[3] == "beat") { Beats++; return BeatReply == null ? Reply(0, "OK " + Token) : BeatReply(); }
                    if (command[3] == "stop") { Stops++; return Reply(0, "STOPPED " + Token); }
                }
            }
            throw Forbidden("unexpected ADB command: " + String.Join(" ", arguments));
        }
        static bool Is(string[] actual, params string[] expected) { return actual.SequenceEqual(expected); }
        void RequireMutation() { Require(LockDepth == 1, "ADB mutation must recheck own session under short core lease"); }
        void Require(bool value, string message)
        {
            if (!value) { Violations.Add(message); throw new InvalidOperationException(message); }
        }
        Exception Forbidden(string message) { ForbiddenActions++; return new InvalidOperationException("Forbidden fake operation: " + message); }

        public override Dictionary<string, object> ReadConfig() { throw Forbidden("config read"); }
        public override RuntimeCommandResult Run(string executable, string[] arguments, bool allowFailure = false, int timeoutMs = 8000, IDictionary<string, string> environment = null) { throw Forbidden("external process execution"); }
        public override RuntimeCommandResult Gnirehtet(string[] arguments, bool allowFailure = false, int timeoutMs = 8000) { throw Forbidden("gnirehtet start or stop"); }
        public override RuntimeCommandResult Guard(RuntimeState state, string command, bool allowFailure = false, int timeoutMs = 8000) { throw Forbidden("guard API: " + command); }
        public override Process StartDetached(string executable, string[] arguments) { throw Forbidden("detached process"); }
        public override Process GetOwnedMonitor(RuntimeState state, bool pinHandle = false) { throw Forbidden("another monitor process"); }
        public override PhoneGuardLease ExtractPhoneGuard() { throw Forbidden("guard extraction"); }
        public override bool PhoneVpnConnected(string serial) { throw Forbidden("unmocked VPN helper"); }
        public override bool WifiHasIPv6Default(int index) { throw Forbidden("live routing helper"); }
        public void Dispose()
        {
            foreach (Child child in Children) child.Dispose();
        }
    }
}
