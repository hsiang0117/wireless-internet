using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WirelessInternet;

// The former status/sampling/characterization suites share one observation fixture.
// Keep semantic boundary checks; omit duplicate per-tick snapshots and large JSON.
internal static class MonitoringChecks
{
    static int checks;
    static void Check(bool value, string label)
    { if (!value) throw new InvalidOperationException(label); checks++; }
    sealed class Source : ISessionObservationSource
    {
        public SessionObservation Current = Observe();
        public Exception StateError, PhoneError, LogError, ConnectionsError;
        public Action DuringPhone;
        public int Reads, StateCalls, PhoneCalls, LogCalls;
        public bool FailPhone;
        public string VpnKey = "vpn-one";
        public long Now;
        readonly bool automaticCounters;
        public MemoryLogBatch LogBatch = new MemoryLogBatch();
        public PhoneCounters Counters = new PhoneCounters { Key = "vpn-one", Interface = "tun0", Download = 100, Upload = 20, Uptime = 50 };
        public Source(bool automatic = false) { automaticCounters = automatic; Now = automatic ? 0 : Stopwatch.Frequency; }
        public SessionObservation ReadSession() { Reads++; StateCalls++; if (StateError != null) throw StateError; return Current; }
        public List<ConnectionInfo> ReadConnections(SessionRecord state)
        { if (ConnectionsError != null) throw ConnectionsError; return new List<ConnectionInfo>(); }
        public PhoneCounters ReadPhone(SessionRecord state)
        {
            PhoneCalls++;
            if (DuringPhone != null) DuringPhone();
            if (PhoneError != null) throw PhoneError;
            if (FailPhone) throw new IOException("fixture sample failure");
            if (!automaticCounters) return Counters;
            double seconds = Now / (double)Stopwatch.Frequency;
            return new PhoneCounters { Key = VpnKey, Interface = "tun0", Download = (ulong)(1000 + Math.Max(0, seconds) * 300), Upload = (ulong)(100 + Math.Max(0, seconds) * 30), Uptime = 100 + Math.Max(0, seconds) };
        }
        public MemoryLogBatch ReadLogs(string pipe, long after)
        { LogCalls++; if (LogError != null) throw LogError; return LogBatch; }
        public long Timestamp { get { return Now; } }
        public void At(double seconds) { Now = (long)(seconds * Stopwatch.Frequency); }
        public void Next(ulong down, ulong up) { Now += Stopwatch.Frequency; Counters.Download = down; Counters.Upload = up; Counters.Uptime++; }
    }
    static SessionObservation Observe()
    { return new SessionObservation { State = new SessionRecord { ProtectionSessionId = new string('a', 32), PhoneIdentity = "fixture", WirelessSerial = "192.0.2.1:34567", ProtectionTimeoutSeconds = 60, RelayLogPipe = "WirelessInternet.Log." + new string('a', 32) }, RelayOwned = true, MonitorOwned = true, HeartbeatAgeSeconds = 1 }; }
    static Backend BackendFor(Source source) { return new Backend(Path.GetTempPath(), false, source); }
    static void StatusChecks()
    {
        var absent = SessionHealth.Assess(false, true, true, 0, true);
        Check(absent.Connection == ConnectionState.Disconnected && absent.Protection == ProtectionState.Inactive && !absent.Healthy, "no session is inactive");
        Check(SessionHealth.Assess(true, true, true, 12, false).Healthy, "12 second acknowledgement accepted");
        Check(!SessionHealth.Assess(true, true, true, 12.001, false).Healthy, "stale acknowledgement does not imply online");
        foreach (double age in new double[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1 })
        {
            var health = SessionHealth.Assess(true, true, true, age, false);
            Check(health.Protection == ProtectionState.Unknown && !health.Healthy, "invalid acknowledgement age " + age);
        }
        Check(!SessionHealth.Assess(true, false, true, 1, true).Healthy, "phone sample cannot invent missing relay");
        var independent = SessionHealth.Assess(true, true, false, 1, true);
        Check(independent.Connection == ConnectionState.Connected && independent.Protection == ProtectionState.Degraded, "phone evidence does not renew watchdog");
        var source = new Source { PhoneError = new IOException("sample unavailable") };
        var backend = BackendFor(source);
        var s = backend.Poll();
        Check(s.Healthy && s.ConnectionState == ConnectionState.Connected && s.ProtectionState == ProtectionState.Healthy, "failed first sample retains acknowledged connection");
        Check(!s.SamplingOk && s.TelemetryState == TelemetryState.Unavailable && !s.TrafficAvailable, "failed first sample exposes unavailable statistics");
        Check(s.ProtectionTimeoutSeconds == 60, "configured protection timeout reaches snapshot");
        source.PhoneError = null;
        s = backend.Poll();
        Check(s.SamplingOk && s.TelemetryState == TelemetryState.Current && double.IsNaN(s.DownloadBytesPerSecond), "first valid sample establishes baseline");
        source.Next(400, 80); s = backend.Poll();
        Check(s.DownloadBytesPerSecond == 300 && s.UploadBytesPerSecond == 60, "continuous sample computes both rate directions");
        source.PhoneError = new IOException("sample unavailable"); source.Next(600, 100); s = backend.Poll();
        Check(s.Healthy && s.TelemetryState == TelemetryState.Stale && s.TrafficAvailable && s.DownloadBytes == 400 && s.UploadBytes == 80, "sampling failure preserves counters and connection independently");
        Check(double.IsNaN(s.DownloadBytesPerSecond) && double.IsNaN(s.UploadBytesPerSecond), "stale sample never shows obsolete speed");
        source.PhoneError = null; source.Next(800, 200); s = backend.Poll();
        Check(s.TelemetryState == TelemetryState.Current && double.IsNaN(s.DownloadBytesPerSecond), "recovery establishes a fresh rate baseline");
        source.Next(900, 220); s = backend.Poll();
        Check(s.DownloadBytesPerSecond == 100 && s.UploadBytesPerSecond == 20, "next consecutive sample restores rates");
        source.Current.MonitorOwned = false; source.Next(1000, 240); s = backend.Poll();
        Check(s.SamplingOk && s.ConnectionState == ConnectionState.Connected && s.ProtectionState == ProtectionState.Degraded && !s.Healthy, "successful sample cannot mask missing monitor");
        source.Current.MonitorOwned = true; source.Current.HeartbeatAgeSeconds = 20; source.Next(1100, 260); s = backend.Poll();
        Check(s.SamplingOk && s.ConnectionState == ConnectionState.Connected && s.ProtectionState == ProtectionState.Degraded && !s.Healthy, "successful sample cannot mask stale protection");
        source.Current.HeartbeatAgeSeconds = 1; source.Current.RelayOwned = false; s = backend.Poll();
        Check(!s.SamplingOk && s.ConnectionState == ConnectionState.Recovering && s.TelemetryState == TelemetryState.Stale, "missing relay prevents sampling and healthy connection");
        source.StateError = new IOException("partial state write"); s = backend.Poll();
        Check(s.ConnectionState == ConnectionState.Unknown && s.ProtectionState == ProtectionState.Unknown && !s.Healthy, "unreadable session reports unknown");
        source.StateError = null; source.Current = null; s = backend.Poll();
        Check(!s.HasSession && s.ConnectionState == ConnectionState.Disconnected && s.ProtectionState == ProtectionState.Inactive && s.DownloadBytes == 1100, "normal stop retains last counters");
        source.Current = Observe(); source.Current.State.ProtectionSessionId = new string('b', 32); source.PhoneError = new IOException("unavailable"); s = backend.Poll();
        Check(!s.TrafficAvailable && s.TelemetryState == TelemetryState.Unavailable && s.DownloadBytes == 0, "new session never reuses previous counters");
        source = new Source(); backend = BackendFor(source);
        source.DuringPhone = delegate { var replacement = Observe(); replacement.State.ProtectionSessionId = new string('c', 32); source.Current = replacement; };
        s = backend.Poll();
        Check(!s.SamplingOk && !s.TrafficAvailable && s.ConnectionState == ConnectionState.Unknown, "mid sample session change discards counters");
        source = new Source { ConnectionsError = new IOException("tcp failure") }; backend = BackendFor(source); s = backend.Poll();
        Check(s.Healthy && s.SamplingOk, "TCP table failure does not change VPN health");
        source = new Source();
        // No real pipe is ever opened by the fake source.
        source.Current.State.RelayLogPipe = "WirelessInternet.Log." + new string('a',32);
        Check(MemoryLogWire.ValidName(source.Current.State.RelayLogPipe), "log fixture uses a valid protocol name");
        source.LogError = new IOException("log unavailable"); backend = BackendFor(source); s = backend.Poll();
        Check(s.Healthy && s.SamplingOk, "log observation never changes connection or counters");
        source = new Source { StateError = new Exception("preview must not read") };
        s = new Backend(Path.GetTempPath(), true, source).Poll();
        Check(source.Reads == 0 && s.Healthy && s.TelemetryState == TelemetryState.Current, "preview performs no system observation");
    }
    static void SamplingChecks()
    {
        string root = Path.GetTempPath();
            PhoneSamplingPolicy policy=new PhoneSamplingPolicy();
            Check(policy.RateIsFresh(4) && !policy.RateIsFresh(4.001) && !policy.RateIsFresh(double.NaN),"freshness.explicit-four-second-window");
            Source source=new Source(true); Backend backend=new Backend(root,false,source);
            Snapshot s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==1 && s.IsNewTelemetrySample && double.IsNaN(s.DownloadBytesPerSecond),"first-at-zero.clock-establishes-baseline");
            source.At(1);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==1 && !s.IsNewTelemetrySample && s.TelemetryState==TelemetryState.Current && s.SamplingOk,"throttle.normal-not-a-failure");
            source.At(2.99);backend.Poll(PhoneSamplingMode.Foreground);Check(source.PhoneCalls==1,"interval.no-early-query");
            source.At(3);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==2 && s.IsNewTelemetrySample && s.DownloadBytesPerSecond==300 && s.UploadBytesPerSecond==30,"interval.three-seconds-new-average-both-directions");
            source.At(4);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==2 && !s.IsNewTelemetrySample && s.DownloadBytesPerSecond==300 && s.TelemetryAgeSeconds==1,"cache.recent-rate-with-age-no-new-point");
            int logs=source.LogCalls;s=backend.Poll(PhoneSamplingMode.Paused);
            Check(source.PhoneCalls==2 && source.LogCalls==logs+1 && s.Healthy,"pause.no-phone-but-local-status-logs-continue");
            Check(!s.SamplingOk && double.IsNaN(s.DownloadBytesPerSecond) && s.TrafficAvailable && s.TrafficNote.Contains("已暂停"),"pause.retains-total-not-current-speed");
            source.At(30);backend.Poll(PhoneSamplingMode.Paused);Check(source.PhoneCalls==2,"pause.long-hidden-no-query");
            s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==3 && s.IsNewTelemetrySample && double.IsNaN(s.DownloadBytesPerSecond),"resume.forces-query-and-new-baseline");
            Check(!s.TrafficNote.Contains("暂不可用") && !s.Logs.Exists(x=>x.Message.Contains("接口或计数已重建")),"resume.not-misreported-as-failure-or-vpn-rebuild");
            source.At(33);s=backend.Poll(PhoneSamplingMode.Foreground);Check(s.DownloadBytesPerSecond==300,"resume.next-interval-restores-speed");
            source.FailPhone=true;source.At(36);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==5 && s.Healthy && !s.SamplingOk && s.TelemetryState==TelemetryState.Stale,"failure.only-telemetry-degraded");
            source.At(37);s=backend.Poll(PhoneSamplingMode.Foreground);Check(source.PhoneCalls==5 && !s.IsNewTelemetrySample && !s.SamplingOk,"failure.retry-is-throttled");
            source.FailPhone=false;source.At(39);s=backend.Poll(PhoneSamplingMode.Foreground);Check(source.PhoneCalls==6 && double.IsNaN(s.DownloadBytesPerSecond) && !s.TrafficNote.Contains("暂不可用"),"failure.recovery-resets-baseline-and-note");
            source.At(42);s=backend.Poll(PhoneSamplingMode.Foreground);Check(s.DownloadBytesPerSecond==300,"failure.next-good-sample-speed");
            source.Current.State.WirelessSerial="192.0.2.1:37124";source.At(43);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==8 && s.IsNewTelemetrySample && double.IsNaN(s.DownloadBytesPerSecond),"endpoint.change-never-reuses-rate-cache");
            source.Current.State.ProtectionSessionId=new string('b',32);source.FailPhone=true;source.At(44);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(!s.TrafficAvailable && s.DownloadBytes==0 && source.PhoneCalls==9,"session.change-never-reuses-old-totals");
            source.FailPhone=false;source.At(47);backend.Poll(PhoneSamplingMode.Foreground);source.At(50);backend.Poll(PhoneSamplingMode.Foreground);
            source.VpnKey="vpn-two";source.At(53);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(double.IsNaN(s.DownloadBytesPerSecond) && s.Logs.Exists(x=>x.Message.Contains("接口或计数已重建")),"vpn.rebuild-clears-old-rate");
            int before=source.PhoneCalls;backend.Poll();backend.Poll();Check(source.PhoneCalls==before+2,"legacy.no-argument-poll-is-immediate");
            source=new Source(true);backend=new Backend(root,false,source,new PhoneSamplingPolicy(10,10));backend.Poll(PhoneSamplingMode.Foreground);
            source.At(10);backend.Poll(PhoneSamplingMode.Foreground);source.Current.RelayOwned=false;source.At(21);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(!s.SamplingOk && double.IsNaN(s.DownloadBytesPerSecond) && s.TelemetryState==TelemetryState.Stale,"expired-cache.never-current-speed");
            source=new Source(true);backend=new Backend(root,false,source);source.DuringPhone=delegate{source.Current=new SessionObservation{State=new SessionRecord{ProtectionSessionId=new string('c',32),PhoneIdentity="FAKE_PHONE",WirelessSerial="192.0.2.1:37123"}};};
            s=backend.Poll(PhoneSamplingMode.Foreground);Check(!s.TrafficAvailable && !s.IsNewTelemetrySample && s.ConnectionState==ConnectionState.Unknown,"during-query.session-change-discards-sample");
            source=new Source(true);backend=new Backend(root,false,source);source.At(10);backend.Poll(PhoneSamplingMode.Foreground);source.At(9);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==2 && double.IsNaN(s.DownloadBytesPerSecond),"clock.rollback-rebuilds-instead-of-negative-speed");
            source=new Source(true);backend=new Backend(root,false,source);
            for(int i=0;i<30;i++){source.At(i);backend.Poll(PhoneSamplingMode.Foreground);}
            Check(source.PhoneCalls==10 && source.LogCalls==30 && source.StateCalls==40,"request-count.30-local-ticks-only-10-complete-phone-queries");
            for(int i=30;i<60;i++){source.At(i);backend.Poll(PhoneSamplingMode.Paused);}
            Check(source.PhoneCalls==10 && source.LogCalls==60,"request-count.30-paused-ticks-zero-new-phone-queries");
            source.Current.State.WirelessSerial="192.0.2.1:40000";s=backend.Poll(PhoneSamplingMode.Paused);
            Check(source.PhoneCalls==10 && !s.TrafficAvailable && s.DownloadBytes==0,"endpoint.paused-change-invalidates-totals-without-query");
    }
    static void Rejects(Action action, string label)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, label);
    }
    static void CompositionChecks()
    {
        var source = new Source(true);
        source.LogBatch.NextSequence = 300;
        for (int index = 1; index <= 300; index++)
            source.LogBatch.Entries.Add(new MemoryLogRecord { Sequence = index, Source = "relay", Message = "entry-" + index });
        var backend = BackendFor(source);
        Snapshot snapshot = backend.Poll();
        Check(snapshot.Logs.Count == 256, "snapshot enforces 256 log records");
        source.Current.State.RelayLogPipe = "invalid pipe";
        int logs = source.LogCalls; backend.Poll();
        Check(source.LogCalls == logs, "invalid pipe never queried");
        source.Current.State.RelayLogPipe = "WirelessInternet.Log." + new string('b', 32);
        source.LogBatch.NextSequence = 400; source.LogBatch.Entries.Clear(); backend.Poll();
        source.LogBatch.NextSequence = 1; snapshot = backend.Poll();
        Check(snapshot.Healthy && snapshot.SamplingOk && snapshot.Logs.Exists(x => x.Source == "日志" && x.Level == "WARN"), "log sequence rollback degrades observation independently");
        Rejects(delegate { backend.Poll((PhoneSamplingMode)999); }, "invalid sampling mode rejected");
        Rejects(delegate { new PhoneSamplingPolicy(0, 4); }, "zero sampling interval rejected");
        Rejects(delegate { new PhoneSamplingPolicy(3, 2); }, "rate age shorter than interval rejected");
        var previewSource = new Source { StateError = new Exception("preview must not read") };
        var preview = new Backend(Path.GetTempPath(), true, previewSource);
        foreach (PhoneSamplingMode mode in new[] { PhoneSamplingMode.Foreground, PhoneSamplingMode.Paused, PhoneSamplingMode.Refresh, PhoneSamplingMode.Immediate })
            preview.Poll(mode);
        Check(previewSource.Reads == 0 && previewSource.PhoneCalls == 0 && previewSource.LogCalls == 0, "all preview modes perform no observations");
    }
    static int Main()
    {
        try
        {
            StatusChecks(); SamplingChecks(); CompositionChecks();
            Console.WriteLine("PASS " + checks + " monitoring checks");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}