using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using WirelessInternet;

static class BackendStatusChecks
{
    static int passed;
    static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    sealed class Source : ISessionObservationSource
    {
        public SessionObservation Current = Observe();
        public Exception StateError, PhoneError, LogError, ConnectionsError;
        public Action DuringPhone;
        public int Reads;
        public long Now = Stopwatch.Frequency;
        public PhoneCounters Counters = new PhoneCounters { Key = "vpn-one", Interface = "tun0", Download = 100, Upload = 20, Uptime = 50 };
        public SessionObservation ReadSession() { Reads++; if (StateError != null) throw StateError; return Current; }
        public List<ConnectionInfo> ReadConnections(SessionRecord state)
        { if (ConnectionsError != null) throw ConnectionsError; return new List<ConnectionInfo>(); }
        public PhoneCounters ReadPhone(SessionRecord state)
        { if (DuringPhone != null) DuringPhone(); if (PhoneError != null) throw PhoneError; return Counters; }
        public MemoryLogBatch ReadLogs(string pipe, long after)
        { if (LogError != null) throw LogError; return new MemoryLogBatch(); }
        public long Timestamp { get { return Now; } }
        public void Next(ulong down, ulong up) { Now += Stopwatch.Frequency; Counters.Download = down; Counters.Upload = up; Counters.Uptime++; }
    }
    static SessionObservation Observe()
    { return new SessionObservation { State = new SessionRecord { ProtectionSessionId = new string('a', 32), PhoneIdentity = "fixture", WirelessSerial = "192.0.2.1:34567", ProtectionTimeoutSeconds = 60 }, RelayOwned = true, MonitorOwned = true, HeartbeatAgeSeconds = 1 }; }
    static Backend BackendFor(Source source) { return new Backend(Path.GetTempPath(), false, source); }
    static void Main()
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
        Console.WriteLine("TOTAL " + passed + " PASS");
    }
}
