using System;
using System.Collections.Generic;

namespace WirelessInternet
{
    public sealed class LogEntry
    {
        public DateTime Time;
        public long Sequence;
        public string Source, Message, Level;
    }
    public sealed class ConnectionInfo
    {
        public string Protocol, RemoteAddress, State;
        public int RemotePort;
    }
    public sealed class Snapshot
    {
        public string SessionId = "", Phone = "", Endpoint = "", StatusText = "未连接", Detail = "", TrafficNote = "";
        public bool HasSession, Healthy, SamplingOk, TrafficAvailable;
        public bool IsNewTelemetrySample;
        public double TelemetryAgeSeconds = double.NaN;
        public ConnectionState ConnectionState = ConnectionState.Disconnected;
        public ProtectionState ProtectionState = ProtectionState.Inactive;
        public TelemetryState TelemetryState = TelemetryState.Unavailable;
        public int ProtectionTimeoutSeconds = 30;
        public double HeartbeatAgeSeconds = double.NaN;
        public ulong DownloadBytes, UploadBytes;
        public double DownloadBytesPerSecond = double.NaN, UploadBytesPerSecond = double.NaN;
        public DateTime? StartedAt;
        public List<ConnectionInfo> Connections = new List<ConnectionInfo>();
        public List<LogEntry> Logs = new List<LogEntry>();
    }
    public sealed class SessionRecord
    {
        public string ProtectionSessionId, PhoneIdentity, WirelessSerial, RelayStartTimeUtcTicks, RelayPath;
        public string WatchdogStartTimeUtcTicks, MonitorHeartbeatQpcTicks, WatchdogPath, RuntimeEngine;
        public int RelayProcessId, WatchdogProcessId, ProtectionTimeoutSeconds;
        public string RelayLogPipe, LogMode;
    }
    public sealed class PhoneCounters
    {
        public string Key, Interface;
        public ulong Download, Upload;
        public double Uptime;
        public DateTime? StartedAt;
    }
    public sealed class CommandResult
    {
        public int ExitCode;
        public string Output, Error;
    }
    public sealed class SessionObservation
    {
        public SessionRecord State;
        public bool RelayOwned, MonitorOwned;
        public double HeartbeatAgeSeconds = double.NaN;
    }
    // Read-only observation boundary; production and deterministic tests use the
    // same polling/state mapping without granting this interface control actions.
    public interface ISessionObservationSource
    {
        SessionObservation ReadSession();
        List<ConnectionInfo> ReadConnections(SessionRecord state);
        PhoneCounters ReadPhone(SessionRecord state);
        MemoryLogBatch ReadLogs(string pipeName, long after);
        long Timestamp { get; }
    }
    public enum PhoneSamplingMode { Immediate, Foreground, Paused, Refresh }

    // The injected source supplies the monotonic clock. A normal three-second
    // GUI interval may reuse a recent average rate for at most four seconds;
    // an older value, a failed read or a pause is never presented as current speed.
    public sealed class PhoneSamplingPolicy
    {
        public readonly double IntervalSeconds, MaximumRateAgeSeconds;
        public PhoneSamplingPolicy() : this(3, 4) { }
        public PhoneSamplingPolicy(double intervalSeconds, double maximumRateAgeSeconds)
        {
            if (Double.IsNaN(intervalSeconds) || Double.IsInfinity(intervalSeconds) || intervalSeconds <= 0)
                throw new ArgumentOutOfRangeException("intervalSeconds");
            if (Double.IsNaN(maximumRateAgeSeconds) || Double.IsInfinity(maximumRateAgeSeconds) || maximumRateAgeSeconds < intervalSeconds)
                throw new ArgumentOutOfRangeException("maximumRateAgeSeconds");
            IntervalSeconds = intervalSeconds; MaximumRateAgeSeconds = maximumRateAgeSeconds;
        }
        public bool ShouldQuery(PhoneSamplingMode mode, bool attempted, double secondsSinceAttempt)
        {
            return mode != PhoneSamplingMode.Paused && (mode == PhoneSamplingMode.Immediate || mode == PhoneSamplingMode.Refresh ||
                !attempted || secondsSinceAttempt < 0 || secondsSinceAttempt >= IntervalSeconds);
        }
        public bool RateIsFresh(double age) { return age >= 0 && age <= MaximumRateAgeSeconds; }
    }
}
