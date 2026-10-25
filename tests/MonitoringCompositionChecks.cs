using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using WirelessInternet;

// A deterministic observation script, compiled unchanged against baseline and
// extracted monitoring sources. It never creates an SDK, process or real pipe.
internal static class MonitoringCompositionChecks
{
    static readonly List<object> Records = new List<object>();
    static int checks;
    static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); checks++; }
    sealed class Source : ISessionObservationSource
    {
        public SessionObservation Current;
        public readonly List<string> Trace = new List<string>();
        public long Now;
        public int PhoneCalls, StateFailures, PhoneFailures, TcpFailures, LogFailures;
        public string PhoneError = "fixture phone unavailable", Key = "vpn-one";
        public ulong Down = 1000, Up = 100;
        public double Uptime = 100;
        public Action DuringPhone;
        public List<ConnectionInfo> Connections = new List<ConnectionInfo>();
        public List<MemoryLogRecord> Logs = new List<MemoryLogRecord>();
        public long LogSequence, Dropped;
        public SessionObservation ReadSession()
        {
            Trace.Add("session:" + (Current == null ? "absent" : Current.State.ProtectionSessionId) + ":" + StateFailures);
            if (StateFailures > 0) { StateFailures--; throw new IOException("fixture state unavailable"); }
            return Current;
        }
        public List<ConnectionInfo> ReadConnections(SessionRecord state)
        {
            Trace.Add("tcp:" + state.ProtectionSessionId + ":" + state.WirelessSerial + ":" + TcpFailures);
            if (TcpFailures > 0) { TcpFailures--; throw new IOException("fixture TCP unavailable"); }
            return new List<ConnectionInfo>(Connections);
        }
        public PhoneCounters ReadPhone(SessionRecord state)
        {
            PhoneCalls++; Trace.Add("phone:" + state.ProtectionSessionId + ":" + state.WirelessSerial + ":" + PhoneFailures);
            if (DuringPhone != null) { Action action = DuringPhone; DuringPhone = null; action(); }
            if (PhoneFailures > 0) { PhoneFailures--; throw new IOException(PhoneError); }
            return new PhoneCounters { Key = Key, Interface = "tun0", Download = Down, Upload = Up, Uptime = Uptime, StartedAt = new DateTime(2001, 1, 2, 3, 4, 5, DateTimeKind.Utc) };
        }
        public MemoryLogBatch ReadLogs(string pipe, long after)
        {
            Trace.Add("logs:" + pipe + ":" + after + ":" + LogFailures);
            if (LogFailures > 0) { LogFailures--; throw new IOException("fixture log unavailable"); }
            return new MemoryLogBatch { NextSequence = LogSequence, DroppedCount = Dropped, Entries = new List<MemoryLogRecord>(Logs) };
        }
        public long Timestamp { get { Trace.Add("clock:" + Now.ToString(CultureInfo.InvariantCulture)); return Now; } }
        public void At(double seconds)
        { Now = (long)(Stopwatch.Frequency * seconds); Uptime = 100 + Math.Max(0, seconds); Down = (ulong)(1000 + Math.Max(0, seconds) * 300); Up = (ulong)(100 + Math.Max(0, seconds) * 30); }
    }
    static SessionObservation Observe(char token)
    {
        return new SessionObservation { State = new SessionRecord { ProtectionSessionId = new string(token, 32), PhoneIdentity = "MONITOR_FIXTURE", WirelessSerial = "192.0.2.1:37123", RuntimeEngine = "CSharp", ProtectionTimeoutSeconds = 30, RelayLogPipe = "WirelessInternet.Log." + new string(token,32) }, RelayOwned = true, MonitorOwned = true, HeartbeatAgeSeconds = 1 };
    }
    static object Canonical(object value)
    {
        if (value == null) return null;
        if (value is string || value is bool || value is int || value is long || value is ulong) return value;
        if (value is double)
        {
            double number = (double)value;
            return Double.IsNaN(number) ? "NaN" : Double.IsPositiveInfinity(number) ? "+Infinity" : Double.IsNegativeInfinity(number) ? "-Infinity" : number.ToString("R", CultureInfo.InvariantCulture);
        }
        if (value is DateTime)
        {
            DateTime date = (DateTime)value;
            return date.Year > 2005 ? "<wall-clock>" : date.ToString("o", CultureInfo.InvariantCulture);
        }
        if (value.GetType().IsEnum) return value.ToString();
        IEnumerable sequence = value as IEnumerable;
        if (sequence != null)
        {
            var all = new List<object>(); foreach (object item in sequence) all.Add(Canonical(item)); return all;
        }
        var fields = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) fields.Add(field.Name, Canonical(field.GetValue(value)));
        return fields;
    }
    static Snapshot Step(Backend backend, Source source, string label, PhoneSamplingMode mode)
    {
        int before = source.Trace.Count;
        Snapshot snapshot = backend.Poll(mode);
        Check(snapshot != null && snapshot.Logs.Count <= 256, label + " bounded snapshot");
        Check(!snapshot.IsNewTelemetrySample || snapshot.SamplingOk, label + " new sample only after success");
        if (mode == PhoneSamplingMode.Paused)
            Check(!source.Trace.Skip(before).Any(x => x.StartsWith("phone:", StringComparison.Ordinal)), label + " pause does not query phone");
        Records.Add(new { Label = label, Snapshot = Canonical(snapshot), Trace = source.Trace.Skip(before).ToArray() });
        return snapshot;
    }
    static void StaticCheck(string label, Func<object> call)
    {
        object outcome;
        try { outcome = new { Value = Canonical(call()), Error = (string)null }; }
        catch (Exception error) { outcome = new { Value = (object)null, Error = error.GetType().FullName + ":" + error.Message }; }
        Records.Add(new { Label = label, Outcome = outcome });
        checks++;
    }
    static int Main(string[] args)
    {
        string report = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "monitor-composition-results.json"));
        try
        {
            var source = new Source();
            var backend = new Backend(Path.GetTempPath(), false, source);
            Step(backend, source, "absent", PhoneSamplingMode.Foreground);
            source.Current = Observe('a');
            source.Connections.Add(new ConnectionInfo { Protocol = "TCP", RemoteAddress = "192.0.2.10", RemotePort = 443, State = "connected" });
            source.LogSequence = 3; source.Dropped = 1;
            source.Logs.Add(new MemoryLogRecord { Sequence = 2, UtcTicks = new DateTime(2001,2,3,0,0,0,DateTimeKind.Utc).Ticks, Source = "中继", Message = "fixture INFO first" });
            source.Logs.Add(new MemoryLogRecord { Sequence = 3, UtcTicks = new DateTime(2001,2,3,0,0,1,DateTimeKind.Utc).Ticks, Source = "中继错误", Message = "fixture WARN second" });
            source.At(0);
            Snapshot current = Step(backend, source, "zero-clock-first", PhoneSamplingMode.Foreground);
            Check(current.Healthy && current.IsNewTelemetrySample, "zero-clock accepts first sample");
            source.Dropped = 0;
            for (int second = 1; second < 30; second++) { source.At(second); Step(backend, source, "foreground-" + second, PhoneSamplingMode.Foreground); }
            Check(source.PhoneCalls == 10, "thirty foreground ticks have ten phone queries");
            int prior = source.PhoneCalls;
            for (int second = 30; second < 60; second++) { source.At(second); Step(backend, source, "paused-" + second, PhoneSamplingMode.Paused); }
            Check(source.PhoneCalls == prior, "thirty paused ticks have no phone query");
            source.At(60); current = Step(backend, source, "restore-refresh", PhoneSamplingMode.Refresh);
            Check(Double.IsNaN(current.DownloadBytesPerSecond) && current.IsNewTelemetrySample, "restore resets baseline");
            source.At(63); current = Step(backend, source, "restore-next-rate", PhoneSamplingMode.Foreground);
            Check(current.DownloadBytesPerSecond == 300 && current.UploadBytesPerSecond == 30, "restored average rate");
            source.PhoneFailures = 3;
            for (int second = 66; second < 75; second++) { source.At(second); Step(backend, source, "sampling-failure-" + second, PhoneSamplingMode.Foreground); }
            source.PhoneError = "fixture different phone error"; source.PhoneFailures = 1; source.At(75); Step(backend, source, "sampling-new-error", PhoneSamplingMode.Foreground);
            source.At(78); Step(backend, source, "sampling-recovery-baseline", PhoneSamplingMode.Foreground);
            source.At(81); Step(backend, source, "sampling-recovery-rate", PhoneSamplingMode.Foreground);
            source.Key = "vpn-two"; source.At(84); Step(backend, source, "vpn-rebuilt", PhoneSamplingMode.Immediate);
            source.At(85); source.Down = 2; source.Up = 1; Step(backend, source, "counter-rollback", PhoneSamplingMode.Immediate);
            source.At(86); source.Uptime = 1; Step(backend, source, "uptime-rollback", PhoneSamplingMode.Immediate);
            source.At(87); source.Current.MonitorOwned = false; Step(backend, source, "monitor-missing-new-sample", PhoneSamplingMode.Foreground);
            source.At(88); Step(backend, source, "monitor-missing-cache", PhoneSamplingMode.Foreground);
            source.At(90); source.Current.MonitorOwned = true; source.Current.HeartbeatAgeSeconds = 20; Step(backend, source, "stale-protection-sample", PhoneSamplingMode.Foreground);
            source.Current.HeartbeatAgeSeconds = Double.NaN; source.At(91); Step(backend, source, "unknown-protection-cache", PhoneSamplingMode.Foreground);
            source.Current.HeartbeatAgeSeconds = 1; source.Current.RelayOwned = false; source.At(95); Step(backend, source, "relay-missing", PhoneSamplingMode.Foreground);
            source.Current.RelayOwned = true; source.At(96); Step(backend, source, "relay-returned", PhoneSamplingMode.Foreground);
            source.TcpFailures = 2; source.LogFailures = 2;
            source.At(97); Step(backend, source, "tcp-log-fail-one", PhoneSamplingMode.Immediate);
            source.At(98); Step(backend, source, "tcp-log-fail-two", PhoneSamplingMode.Immediate);
            source.At(99); Step(backend, source, "tcp-log-recovered", PhoneSamplingMode.Immediate);
            source.Connections.Clear(); source.At(100); Step(backend, source, "tcp-closed", PhoneSamplingMode.Immediate);
            source.StateFailures = 3;
            for (int second = 101; second < 104; second++) { source.At(second); Step(backend, source, "state-unavailable-" + second, PhoneSamplingMode.Foreground); }
            source.At(104); Step(backend, source, "state-recovered", PhoneSamplingMode.Foreground);
            source.Current.State.WirelessSerial = "192.0.2.1:40001"; source.At(105); Step(backend, source, "endpoint-changed-paused", PhoneSamplingMode.Paused);
            source.At(106); Step(backend, source, "endpoint-restored", PhoneSamplingMode.Foreground);
            source.DuringPhone = delegate { source.Current = Observe('b'); }; source.At(109); current = Step(backend, source, "session-switch-inflight", PhoneSamplingMode.Foreground);
            Check(!current.SamplingOk && current.ConnectionState == ConnectionState.Unknown, "inflight switch discards sample");
            source.At(110); Step(backend, source, "new-session", PhoneSamplingMode.Foreground);
            source.Current.State.PhoneIdentity = "SECOND_FIXTURE"; source.At(111); Step(backend, source, "identity-change", PhoneSamplingMode.Foreground);
            source.Current.State.ProtectionTimeoutSeconds = 60; source.At(112); Step(backend, source, "sixty-second-display", PhoneSamplingMode.Foreground);
            source.Current.State.ProtectionTimeoutSeconds = 1; source.At(113); Step(backend, source, "invalid-timeout-display", PhoneSamplingMode.Foreground);
            source.DuringPhone = delegate { source.StateFailures = 1; }; source.At(114); Step(backend, source, "state-unavailable-after-phone", PhoneSamplingMode.Immediate);
            source.Logs.Clear(); source.LogSequence = 1; source.At(115); Step(backend, source, "log-sequence-rollback", PhoneSamplingMode.Immediate);
            source.Current.State.RelayLogPipe = "WirelessInternet.Log." + new string('c',32); source.LogSequence = 300;
            for (int index = 1; index <= 300; index++) source.Logs.Add(new MemoryLogRecord { Sequence = index, UtcTicks = 0, Source = "relay", Message = "entry-" + index });
            source.At(116); current = Step(backend, source, "bounded-log-batch", PhoneSamplingMode.Immediate);
            Check(current.Logs.Count == 256, "snapshot enforces 256 log records");
            source.Current.State.RelayLogPipe = "invalid pipe"; source.At(117); Step(backend, source, "invalid-log-pipe", PhoneSamplingMode.Immediate);
            source.At(50); Step(backend, source, "monotonic-clock-rollback", PhoneSamplingMode.Foreground);
            source.Current = null; source.At(118); current = Step(backend, source, "normal-disconnect", PhoneSamplingMode.Foreground);
            Check(!current.HasSession && current.TrafficAvailable, "disconnect retains totals");
            Step(backend, source, "repeat-disconnect", PhoneSamplingMode.Foreground);
            source.Current = Observe('d'); source.PhoneFailures = 1; source.At(120); current = Step(backend, source, "new-session-first-failure", PhoneSamplingMode.Foreground);
            Check(!current.TrafficAvailable && current.DownloadBytes == 0 && current.Healthy, "new session clears old traffic without invalidating protection");
            source.At(121); Step(backend, source, "failed-read-is-throttled", PhoneSamplingMode.Foreground);
            source.At(123); Step(backend, source, "new-session-recovered", PhoneSamplingMode.Foreground);
            StaticCheck("invalid-mode", delegate { return backend.Poll((PhoneSamplingMode)999); });
            StaticCheck("invalid-policy-interval", delegate { return new PhoneSamplingPolicy(0,4); });
            StaticCheck("invalid-policy-age", delegate { return new PhoneSamplingPolicy(3,2); });
            StaticCheck("invalid-phone-identity", delegate { return Backend.ParsePhone("bad fixture", "MONITOR_FIXTURE"); });
            var previewSource = new Source { StateFailures = 1 };
            var preview = new Backend(Path.GetTempPath(), true, previewSource);
            foreach (PhoneSamplingMode mode in new[] { PhoneSamplingMode.Foreground, PhoneSamplingMode.Paused, PhoneSamplingMode.Refresh, PhoneSamplingMode.Immediate })
                Step(preview, previewSource, "preview-" + mode, mode);
            Check(previewSource.Trace.Count == 0, "preview has no observation calls");
            var data = new { Passed = true, Assertions = checks, SnapshotsAndOutcomes = Records.Count, PhoneQueries = source.PhoneCalls, Records = Records };
            var serializer = new JavaScriptSerializer { MaxJsonLength = 16777216 };
            File.WriteAllText(report, serializer.Serialize(data));
            Console.WriteLine("PASS " + checks + " monitoring composition checks; snapshots/outcomes=" + Records.Count + "; phone queries=" + source.PhoneCalls);
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
