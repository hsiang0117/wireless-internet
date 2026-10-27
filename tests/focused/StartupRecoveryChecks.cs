using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using WirelessInternet;

// In-memory sessions and injected process/port observations only. Any control
// or phone API is a trap; this suite never creates a process or queries a network.
internal static class StartupRecoveryChecks
{
    const string Token = "11111111111111111111111111111111";
    const string NextToken = "22222222222222222222222222222222";
    static string fixtures;
    static int passed;
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Case(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
    static ConnectionOperationResult Recover(Fake context)
    {
        bool completedOutsideLock = false;
        List<string> output = new List<string>();
        ConnectionOperationResult result = new ConnectionController(context, delegate(ConnectionStage stage) {
            if (stage == ConnectionStage.Completed) completedOutsideLock = context.Depth == 0;
        }).RecoverStaleWithResult(output.Add);
        Require(context.Depth == 0 && context.ForbiddenCalls == 0, "startup leaked a lease or reached an online control API");
        Require(context.Timeouts.Count == 1 && context.Timeouts[0] == 0, "startup waited for the shared operation lock");
        Require(result.Operation == ConnectionOperationKind.RecoverStale, "startup result is disguised as Stop");
        Require(output.Count > 0 && output[0].StartsWith("启动检查", StringComparison.Ordinal), "startup progress mislabeled as manual Stop");
        if (result.Success) Require(completedOutsideLock, "success was published while holding the operation lease");
        return result;
    }
    sealed class Fake : RuntimeContext
    {
        internal RuntimeState State;
        internal bool Busy, ReadFails, InspectFails, NullInspection, PortIdle = true, PortFails;
        internal bool ReplaceOnRead, ReplaceOnDelete, KeepOnDelete, BecomeLive, ReleaseFails;
        internal int Depth, Reads, Inspections, Deletes, Releases, ForbiddenCalls, PortQueries;
        internal readonly List<int> Timeouts = new List<int>();
        internal readonly RecordedProcessPresence[] Presence = { RecordedProcessPresence.Absent, RecordedProcessPresence.Absent, RecordedProcessPresence.Absent };
        internal Fake() : base(Path.Combine(fixtures, "s" + Guid.NewGuid().ToString("N").Substring(0, 8))) { State = NewState(Token); }
        RuntimeState NewState(string token)
        {
            return new RuntimeState { PhoneIdentity = "STARTUP_FIXTURE", WirelessSerial = "192.0.2.1:37123",
                ProtectionSessionId = token, PhoneControlPath = "/data/local/tmp/codex-wireless-internet/" + token + "/control.sh",
                RelayProcessId = 11, RelayStartTimeUtcTicks = "1", RelayPath = RelayPath,
                WatchdogProcessId = 12, WatchdogStartTimeUtcTicks = "1", WatchdogPath = WorkerPath,
                RelayHostProcessId = 13, RelayHostStartTimeUtcTicks = "1", RuntimeEngine = "CSharp",
                MonitorHeartbeatQpcTicks = "987654321", RawFields = new Dictionary<string, object> { { "FutureValue", "keep" } } };
        }
        void Locked() { Require(Depth == 1, "session inspection escaped the existing operation lease"); }
        Exception Trap() { ForbiddenCalls++; return new InvalidOperationException("Startup attempted an online/control operation."); }
        public override IDisposable EnterCoreMutex(int timeoutMs = 6000)
        {
            Timeouts.Add(timeoutMs);
            if (timeoutMs != 0) throw Trap();
            if (Busy) throw new TimeoutException("fixture lock busy");
            Require(Depth == 0, "nested operation lease"); Depth++; return new Lease(this);
        }
        sealed class Lease : IDisposable
        {
            Fake owner;
            internal Lease(Fake value) { owner = value; }
            public void Dispose() { if (owner == null) throw new Exception("lease disposed twice"); Fake value = owner; owner = null; value.Depth--; value.Releases++; if (value.ReleaseFails) throw new IOException("fixture release failed"); }
        }
        public override RuntimeState ReadState()
        { Locked(); Reads++; if (ReadFails) throw new IOException("fixture malformed session"); if (ReplaceOnRead && Reads == 2) State = NewState(NextToken); return State; }
        public override StaleSessionInspection InspectStaleSession(RuntimeState state)
        {
            Locked(); Inspections++;
            if (InspectFails) throw new IOException("fixture inspection unavailable");
            if (NullInspection) return null;
            if (BecomeLive && Inspections == 2) Presence[0] = RecordedProcessPresence.OwnedAlive;
            return base.InspectStaleSession(state);
        }
        public override RecordedProcessPresence ProbeRecordedProcess(int pid, string ticks, string path) { Locked(); return Presence[pid - 11]; }
        public override bool IsRelayPortIdleForStaleCleanup() { Locked(); PortQueries++; if (PortFails) throw new IOException("fixture TCP query unavailable"); return PortIdle; }
        public override void DeleteStateIfToken(string token)
        { Locked(); if (ReplaceOnDelete) State = NewState(NextToken); if (!KeepOnDelete && State != null && State.ProtectionSessionId == token) { State = null; Deletes++; } }
        public override Dictionary<string, object> ReadConfig() { throw Trap(); }
        public override void WriteState(RuntimeState state) { throw Trap(); }
        public override RuntimeCommandResult Run(string exe, string[] args, bool allowFailure = false, int timeoutMs = 8000, IDictionary<string, string> environment = null) { throw Trap(); }
        public override RuntimeCommandResult Adb(string[] args, bool allowFailure = false, int timeoutMs = 8000) { throw Trap(); }
        public override RuntimeCommandResult Gnirehtet(string[] args, bool allowFailure = false, int timeoutMs = 8000) { throw Trap(); }
        public override RuntimeCommandResult Guard(RuntimeState state, string command, bool allowFailure = false, int timeoutMs = 8000) { throw Trap(); }
        public override bool PhoneVpnConnected(string serial) { throw Trap(); }
        public override Process GetOwnedRelay(RuntimeState state, bool pinHandle = false) { throw Trap(); }
        public override Process GetOwnedMonitor(RuntimeState state, bool pinHandle = false) { throw Trap(); }
        public override int[] GetTcpPortOwners(int port, bool established = false) { throw Trap(); }
        public override Process StartDetached(string exe, string[] args) { throw Trap(); }
        public override NetworkLink ResolveNetwork(IDictionary<string, object> configuration, RuntimeState expected = null, bool wifiOnly = false) { throw Trap(); }
        public override void ApplyNetwork(RuntimeState state, NetworkLink link) { throw Trap(); }
    }
    static ConnectionOperationResult Preserved(Fake context)
    {
        ConnectionOperationResult result = Recover(context);
        Require(context.State != null && context.Deletes == 0 && !result.Stages.Contains(ConnectionStage.RemoveSession), "uncertain/live session was retired");
        return result;
    }
    static int Main(string[] args)
    {
        try
        {
            fixtures = Path.GetFullPath(args[0]);
            Case("no session performs no work", delegate {
                Fake context = new Fake(); context.State = null;
                Require(Recover(context).Success && context.Reads == 1 && context.Inspections == 0 && context.Deletes == 0 && context.Releases == 1, "no-session startup was not a no-op");
            });
            Case("busy operation is skipped without a read or wait", delegate {
                Fake context = new Fake(); context.Busy = true;
                Require(Preserved(context).Success && context.Reads == 0 && context.Inspections == 0 && context.Releases == 0, "busy startup interfered with an existing operation");
            });
            Case("dead session is checked twice and retired without phone control", delegate {
                Fake context = new Fake(); ConnectionOperationResult result = Recover(context);
                Require(result.Success && context.State == null && context.Deletes == 1 && context.Reads == 3 && context.Inspections == 2 && result.Stages.Contains(ConnectionStage.RemoveSession), "dead startup record was not safely retired");
            });
            Case("every active recorded resource is preserved", delegate {
                for (int i = 0; i < 3; i++) { Fake context = new Fake(); context.Presence[i] = RecordedProcessPresence.OwnedAlive;
                    Require(Preserved(context).Success && context.Inspections == 1 && context.Reads == 1 && context.PortQueries == 0, "active startup fell through to manual Stop"); }
            });
            Case("normal sharing identity and heartbeat remain unchanged", delegate {
                Fake context = new Fake(); context.Presence[0] = RecordedProcessPresence.OwnedAlive;
                RuntimeState original = context.State; JavaScriptSerializer json = new JavaScriptSerializer(); string before = json.Serialize(original);
                Preserved(context);
                Require(Object.ReferenceEquals(original, context.State) && json.Serialize(context.State) == before && context.State.RawFields["FutureValue"].ToString() == "keep", "startup mutated active sharing state");
            });
            Case("unknown ownership on any resource is preserved", delegate {
                for (int i = 0; i < 3; i++) { Fake context = new Fake(); context.Presence[i] = RecordedProcessPresence.Unknown; Preserved(context); }
            });
            Case("occupied or unreadable TCP state is preserved", delegate {
                Fake busy = new Fake(); busy.PortIdle = false; Preserved(busy);
                Fake failed = new Fake(); failed.PortFails = true; Preserved(failed);
            });
            Case("malformed session cannot trigger cleanup", delegate { Fake context = new Fake(); context.ReadFails = true; Require(!Preserved(context).Success && context.Inspections == 0, "malformed state reported completed cleanup"); });
            Case("replacement token before retirement is retained", delegate { Fake context = new Fake(); context.ReplaceOnRead = true; Require(!Preserved(context).Success && context.State.ProtectionSessionId == NextToken, "new token was lost"); });
            Case("newly active resources stop retirement", delegate { Fake context = new Fake(); context.BecomeLive = true; Require(!Preserved(context).Success && context.Inspections == 2, "second ownership check was ignored"); });
            Case("replacement during token deletion cannot report success", delegate {
                Fake context = new Fake(); context.ReplaceOnDelete = true; ConnectionOperationResult result = Recover(context);
                Require(!result.Success && context.State != null && context.State.ProtectionSessionId == NextToken && context.Deletes == 0, "replacement was removed or falsely confirmed");
            });
            Case("uncompleted deletion is not a successful cleanup", delegate { Fake context = new Fake(); context.KeepOnDelete = true; Require(!Recover(context).Success && context.State != null && context.Deletes == 0, "unremoved record was reported cleaned"); });
            Case("lease release failure cannot publish a completed result", delegate {
                Fake context = new Fake(); context.ReleaseFails = true; ConnectionOperationResult result = Recover(context);
                Require(!result.Success && result.Stage == ConnectionStage.ReleaseOperation && !result.Stages.Contains(ConnectionStage.Completed) && context.Releases == 1, "lease failure was hidden or release retried");
            });
            Case("failed or missing inspection cannot authorize retirement", delegate {
                Fake failed = new Fake(); failed.InspectFails = true; Require(!Preserved(failed).Success, "inspection failure was hidden");
                Fake missing = new Fake(); missing.NullInspection = true; Require(!Preserved(missing).Success, "missing inspection authorized cleanup");
            });
            Case("startup action is a distinct bounded wire operation", delegate {
                string id = new string('a', 32);
                ActionControlWire.ValidateOperation(id, "RecoverStale");
                ActionControlFrame frame = new ActionControlFrame { Version = ActionControlWire.Version, OperationId = id, Action = "RecoverStale", Kind = "result", Stage = (int)ConnectionStage.Completed, Success = true, Message = "startup check", Stages = new int[] { (int)ConnectionStage.AcquireOperation, (int)ConnectionStage.Completed } };
                using (MemoryStream stream = new MemoryStream()) { ActionControlWire.Write(stream, frame); stream.Position = 0;
                    Require(ActionControlWire.Read(stream, id, "RecoverStale").Action == "RecoverStale", "startup result was relabeled as Stop"); }
            });
            Console.WriteLine("StartupRecoveryChecks: " + passed + " passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
