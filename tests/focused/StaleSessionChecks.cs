using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using WirelessInternet;

// Local in-memory sessions only. No ADB, network queries, process creation or termination.
internal static class StaleSessionChecks
{
    const string FirstToken = "11111111111111111111111111111111";
    const string NextToken = "22222222222222222222222222222222";
    static int passed;
    static string fixtures;
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Case(string name, Action check) { check(); passed++; Console.WriteLine("PASS " + name); }
    static ConnectionOperationResult Stop(Fake context)
    {
        bool completedOutsideLock = false;
        ConnectionOperationResult result = new ConnectionController(context, delegate(ConnectionStage stage) {
            if (stage == ConnectionStage.Completed) completedOutsideLock = context.LockDepth == 0;
        }).StopWithResult(null);
        Require(context.LockDepth == 0, "operation lease leaked");
        if (result.Success) Require(completedOutsideLock, "success published before releasing the operation lease");
        return result;
    }
    sealed class Fake : RuntimeContext
    {
        internal RuntimeState State;
        internal readonly RecordedProcessPresence[] Presence = { RecordedProcessPresence.Absent, RecordedProcessPresence.Absent, RecordedProcessPresence.Absent };
        internal bool PortIdle = true, ThrowPort, ThrowProbe, ReplaceOnRead, ReplaceOnDelete, KeepOnDelete, BecomeLive, ThrowRelease;
        internal int LockDepth, Releases, Reads, Inspections, Deletes, ConfigReads, AdbCalls;
        internal Fake() : base(Path.Combine(fixtures, "f" + Guid.NewGuid().ToString("N").Substring(0, 8)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AdbPath)); File.WriteAllBytes(AdbPath, new byte[0]);
            Directory.CreateDirectory(Path.GetDirectoryName(RelayPath)); File.WriteAllBytes(RelayPath, new byte[0]);
            State = NewState(FirstToken);
        }
        RuntimeState NewState(string token)
        {
            return new RuntimeState { PhoneIdentity = "STALE_FIXTURE", WirelessSerial = "192.0.2.1:37123",
                ProtectionSessionId = token, ProtectionMode = "PcOnly",
                RelayProcessId = 11, RelayStartTimeUtcTicks = "1", RelayPath = RelayPath,
                WatchdogProcessId = 12, WatchdogStartTimeUtcTicks = "1", WatchdogPath = WorkerPath,
                RelayHostProcessId = 13, RelayHostStartTimeUtcTicks = "1", RuntimeEngine = "CSharp" };
        }
        void Locked() { Require(LockDepth == 1, "session work escaped the original operation lease"); }
        public override IDisposable EnterCoreMutex(int timeoutMs = 6000) { Require(LockDepth == 0, "nested operation lease"); LockDepth++; return new Lease(this); }
        sealed class Lease : IDisposable
        {
            Fake owner;
            internal Lease(Fake value) { owner = value; }
            public void Dispose() { if (owner == null) throw new Exception("lease disposed twice"); Fake value = owner; owner = null; value.LockDepth--; value.Releases++; if (value.ThrowRelease) throw new IOException("fixture release failed"); }
        }
        public override RuntimeState ReadState() { Locked(); Reads++; if (ReplaceOnRead && Reads == 2) State = NewState(NextToken); return State; }
        public override void DeleteStateIfToken(string token)
        {
            Locked(); if (ReplaceOnDelete) State = NewState(NextToken);
            if (!KeepOnDelete && State != null && State.ProtectionSessionId == token) { State = null; Deletes++; }
        }
        public override StaleSessionInspection InspectStaleSession(RuntimeState state)
        { Locked(); Inspections++; if (BecomeLive && Inspections == 2) Presence[0] = RecordedProcessPresence.OwnedAlive; return base.InspectStaleSession(state); }
        public override RecordedProcessPresence ProbeRecordedProcess(int pid, string ticks, string path)
        { Locked(); if (ThrowProbe) throw new IOException("fixture process query denied"); return Presence[pid - 11]; }
        public override bool IsRelayPortIdleForStaleCleanup() { Locked(); if (ThrowPort) throw new IOException("fixture TCP query failed"); return PortIdle; }
        public override Dictionary<string, object> ReadConfig()
        { Locked(); ConfigReads++; return new Dictionary<string, object> { { "Transport", "NativeTls" }, { "PhoneIdentity", "STALE_FIXTURE" }, { "DisconnectTimeoutSeconds", 30 } }; }
        public override RuntimeCommandResult Adb(string[] args, bool allowFailure = false, int timeoutMs = 8000)
        { Locked(); AdbCalls++; throw new IOException("fixture phone is offline"); }
        public override RuntimeCommandResult Gnirehtet(string[] args, bool allowFailure = false, int timeoutMs = 8000)
        { throw new Exception("unexpected phone side effect"); }
    }
    static void Retained(Fake context, string message)
    {
        ConnectionOperationResult result = Stop(context);
        Require(!result.Success && context.State != null && context.Deletes == 0, message);
        Require(context.AdbCalls == 0 && context.ConfigReads == 0, "uncertain stale cleanup performed online side effects");
    }
    static int Main(string[] args)
    {
        try
        {
            fixtures = Path.GetFullPath(args[0]);
            Case("absent session is an idempotent no-op", delegate {
                Fake context = new Fake(); context.State = null;
                Require(Stop(context).Success && Stop(context).Success && context.Reads == 2 && context.Releases == 2,
                    "absent session did not complete cleanly");
                Require(context.Inspections == 0 && context.ConfigReads == 0 && context.AdbCalls == 0, "no-op contacted a phone");
            });
            Case("dead offline session retires without configuration or phone", delegate {
                Fake context = new Fake(); ConnectionOperationResult result = Stop(context);
                Require(result.Success && context.State == null && context.Deletes == 1 && context.Inspections == 2, "stale session not rechecked and removed");
                Require(context.ConfigReads == 0 && context.AdbCalls == 0 && result.Stages.Contains(ConnectionStage.RemoveSession), "offline cleanup reached a phone");
                Require(result.Message.Contains("手机"), "local-only result omitted phone uncertainty");
                Fake legacy = new Fake(); legacy.State.ProtectionMode = null;
                legacy.State.PhoneControlPath = "/data/local/tmp/codex-wireless-internet/" + FirstToken + "/control.sh";
                Require(Stop(legacy).Success && legacy.State == null && legacy.Deletes == 1 && legacy.Inspections == 2,
                    "confirmed-dead legacy record could not be retired");
                Require(legacy.ConfigReads == 0 && legacy.AdbCalls == 0, "legacy stale cleanup contacted a phone");
            });
            Case("each live resource retains the normal online path", delegate {
                for (int resource = 0; resource < 3; resource++) {
                    Fake context = new Fake(); context.Presence[resource] = RecordedProcessPresence.OwnedAlive;
                    ConnectionOperationResult result = Stop(context);
                    Require(!result.Success && context.State != null && context.Deletes == 0 && context.ConfigReads == 1 && context.AdbCalls > 0,
                        "a live resource was retired offline");
                }
            });
            Case("unknown ownership on any resource blocks retirement", delegate {
                for (int resource = 0; resource < 3; resource++) { Fake context = new Fake(); context.Presence[resource] = RecordedProcessPresence.Unknown; Retained(context, "unknown resource discarded"); }
            });
            Case("occupied relay port blocks retirement", delegate { Fake context = new Fake(); context.PortIdle = false; Retained(context, "busy port ignored"); });
            Case("TCP query failure is not an empty port table", delegate { Fake context = new Fake(); context.ThrowPort = true; Retained(context, "TCP query failure ignored"); });
            Case("replacement token before deletion is preserved", delegate {
                Fake context = new Fake(); context.ReplaceOnRead = true; Retained(context, "replacement session removed");
                Require(context.State.ProtectionSessionId == NextToken, "new token was overwritten");
            });
            Case("resource reappearing before deletion blocks retirement", delegate { Fake context = new Fake(); context.BecomeLive = true; Retained(context, "second ownership inspection ignored"); });
            Case("replacement during token deletion cannot report success", delegate {
                Fake context = new Fake(); context.ReplaceOnDelete = true; Retained(context, "replacement session falsely confirmed");
                Require(context.State.ProtectionSessionId == NextToken, "new session was deleted");
            });
            Case("uncompleted deletion is not successful cleanup", delegate { Fake context = new Fake(); context.KeepOnDelete = true; Retained(context, "undeleted session falsely confirmed"); });
            Case("release failure cannot publish completed result", delegate {
                Fake context = new Fake(); context.ThrowRelease = true; ConnectionOperationResult result = Stop(context);
                Require(!result.Success && result.Stage == ConnectionStage.ReleaseOperation && !result.Stages.Contains(ConnectionStage.Completed), "release failure reported success");
                Require(context.Releases == 1 && context.State == null, "lease release retried or completed cleanup undone");
            });
            Case("invalid recorded path blocks retirement", delegate {
                Fake context = new Fake(); context.State.RelayPath = Path.Combine(context.Root, "unrelated.exe"); Retained(context, "invalid resource path accepted");
            });
            Case("exact self identity and recycled PID are distinguished without termination", delegate {
                using (Process self = Process.GetCurrentProcess()) {
                    string ticks = self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), path = self.MainModule.FileName;
                    Require(OwnedProcessManager.ProbeRecordedProcess(self.Id, ticks, path) == RecordedProcessPresence.OwnedAlive, "live exact process not recognized");
                    Require(OwnedProcessManager.ProbeRecordedProcess(self.Id, (Int64.Parse(ticks, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture), path) == RecordedProcessPresence.Absent, "recycled PID not treated as original process absence");
                    Require(OwnedProcessManager.ProbeRecordedProcess(self.Id, ticks, Path.Combine(fixtures, "wrong.exe")) == RecordedProcessPresence.Unknown && !self.HasExited, "wrong image identity accepted or a process terminated");
                }
            });
            Case("absent and malformed process records stay distinct", delegate {
                Require(OwnedProcessManager.ProbeRecordedProcess(Int32.MaxValue, "1", Path.Combine(fixtures, "absent.exe")) == RecordedProcessPresence.Absent, "absent PID not recognized");
                Require(OwnedProcessManager.ProbeRecordedProcess(0, null, null) == RecordedProcessPresence.Absent, "unrecorded resource is not absent");
                Require(OwnedProcessManager.ProbeRecordedProcess(-1, null, null) == RecordedProcessPresence.Unknown &&
                    OwnedProcessManager.ProbeRecordedProcess(0, "1", null) == RecordedProcessPresence.Unknown &&
                    OwnedProcessManager.ProbeRecordedProcess(12, "invalid", "bad") == RecordedProcessPresence.Unknown, "malformed metadata assumed absent");
            });
            Case("process query failure preserves the session", delegate { Fake context = new Fake(); context.ThrowProbe = true; Retained(context, "failed ownership query discarded a session"); });
            Console.WriteLine("StaleSessionChecks: " + passed + " passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
