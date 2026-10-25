using System;
using System.Collections.Generic;
using System.IO;

namespace WirelessInternet
{
    // Sole owner of monitoring composition: read order, session health, TCP/log
    // observations and emitted snapshots. It never admits or executes UI actions.
    internal sealed class MonitorSnapshotAssembler
    {
        readonly bool preview;
        readonly ISessionObservationSource observations;
        readonly PhoneTrafficTracker traffic;
        string logPipe = "";
        long logCursor, nextLogSequence;
        bool logUnavailable;
        HashSet<string> lastConnections = new HashSet<string>();
        string session = "", lastStatus = "";
        int stateFailures;
        Snapshot lastSnapshot;
        int previewTick;
        public MonitorSnapshotAssembler(bool preview, ISessionObservationSource observations, PhoneSamplingPolicy samplingPolicy)
        {
            this.preview = preview; this.observations = observations;
            traffic = new PhoneTrafficTracker(samplingPolicy);
        }
        void Add(Snapshot s, string source, string message, string level)
        {
            if (s.Logs.Count >= 256) return;
            if (message.Length > 4096) message = message.Substring(0, 4092) + " ...";
            s.Logs.Add(new LogEntry { Time = DateTime.Now, Sequence = ++nextLogSequence, Source = source, Message = message, Level = level });
        }
        void ReadMemoryLogs(Snapshot snapshot, SessionRecord state)
        {
            if (logPipe != state.RelayLogPipe) { logPipe = state.RelayLogPipe; logCursor = 0; logUnavailable = false; }
            if (!MemoryLogWire.ValidName(logPipe)) return;
            try
            {
                MemoryLogBatch batch = observations.ReadLogs(logPipe, logCursor);
                if (batch.NextSequence < logCursor) throw new IOException("实时日志序号已改变。");
                if (batch.DroppedCount > 0) Add(snapshot, "日志", "仅显示最近事件，已略过 " + batch.DroppedCount + " 条较早记录。日志不保存到文件。", "INFO");
                foreach (MemoryLogRecord item in batch.Entries)
                {
                    if (item.Sequence <= logCursor || item.Sequence > batch.NextSequence || item.Message == null || item.Message.Length > MemoryLogBuffer.MaxMessage) continue;
                    string level = item.Message.Contains(" ERROR ") ? "ERROR" : item.Message.Contains(" WARN ") ? "WARN" : "INFO";
                    int priorCount = snapshot.Logs.Count;
                    Add(snapshot, item.Source == "中继错误" ? "中继错误" : "中继", item.Message, level);
                    if (snapshot.Logs.Count > priorCount && item.UtcTicks >= DateTime.MinValue.Ticks && item.UtcTicks <= DateTime.MaxValue.Ticks)
                        snapshot.Logs[snapshot.Logs.Count - 1].Time = new DateTime(item.UtcTicks, DateTimeKind.Utc).ToLocalTime();
                }
                logCursor = batch.NextSequence;
                logUnavailable = false;
            }
            catch (Exception)
            {
                if (!logUnavailable) Add(snapshot, "日志", "实时日志暂不可用，网络共享与失联保护独立运行。", "WARN");
                logUnavailable = true;
            }
        }
        public Snapshot Poll(PhoneSamplingMode mode)
        {
            if (!Enum.IsDefined(typeof(PhoneSamplingMode), mode)) throw new ArgumentOutOfRangeException("mode");
            if (preview)
            {
                Snapshot demo = Preview();
                demo.IsNewTelemetrySample = mode != PhoneSamplingMode.Paused;
                if (mode == PhoneSamplingMode.Paused) { demo.SamplingOk = false; demo.TelemetryState = TelemetryState.Stale; demo.DownloadBytesPerSecond = demo.UploadBytesPerSecond = double.NaN; }
                return demo;
            }
            Snapshot snap = traffic.CreateSnapshot();
            Action<string, string, string> add = delegate(string source, string message, string level) { Add(snap, source, message, level); };
            SessionObservation observation;
            try { observation = observations.ReadSession(); stateFailures = 0; }
            catch (Exception e)
            {
                stateFailures++;
                snap.HasSession = true; snap.StatusText = "读取状态中";
                snap.ConnectionState = ConnectionState.Unknown; snap.ProtectionState = ProtectionState.Unknown;
                snap.Detail = "共享状态暂不可读；后台保护独立运行。";
                if (stateFailures == 3) Add(snap, "状态", e.Message, "WARN");
                return snap;
            }
            if (observation == null)
            {
                snap.StatusText = "未连接";
                snap.Detail = "手机保持 Wi-Fi、热点及无线调试开启，电脑连接手机热点后，点击开始连接。";
                if (lastSnapshot != null && lastSnapshot.HasSession)
                {
                    snap.StatusText = "已断开";
                    snap.Detail = "本次共享已结束。流量保留至下一次连接，关闭界面不会影响后台共享。";
                    Add(snap, "状态", "共享会话已结束，已停止网速采样。", "INFO");
                }
                logPipe = ""; logCursor = 0;
                traffic.Reset(false); traffic.ForgetEndpoint();
                lastStatus = snap.StatusText;
                lastSnapshot = snap;
                return snap;
            }
            SessionRecord state = observation.State;
            if (session != state.ProtectionSessionId)
            {
                session = state.ProtectionSessionId;
                traffic.Reset(true);
                snap.TrafficAvailable = false;
                snap.TelemetryState = TelemetryState.Unavailable;
                snap.DownloadBytes = snap.UploadBytes = 0; snap.StartedAt = null;
                lastConnections.Clear(); logPipe = ""; logCursor = 0;
                Add(snap, "会话", "已接管受保护的无线共享会话 " + session.Substring(0, 8) + "；关闭本窗口后仍可继续共享。", "INFO");
            }
            traffic.BindEndpoint(state, snap);
            snap.HasSession = true; snap.SessionId = session; snap.Phone = state.PhoneIdentity; snap.Endpoint = state.WirelessSerial;
            snap.ProtectionTimeoutSeconds = state.ProtectionTimeoutSeconds >= 15 && state.ProtectionTimeoutSeconds <= 120 ? state.ProtectionTimeoutSeconds : 30;
            ApplyHealth(snap, observation, false);
            bool relayOk = observation.RelayOwned;
            if (relayOk)
            {
                try
                {
                    snap.Connections = observations.ReadConnections(state);
                    HashSet<string> current = new HashSet<string>();
                    foreach (ConnectionInfo c in snap.Connections)
                    {
                        string key = c.Protocol + " " + c.RemoteAddress + ":" + c.RemotePort;
                        current.Add(key);
                        if (!lastConnections.Contains(key)) Add(snap, "TCP 采样", "连接出现 " + key + " · " + c.State, "INFO");
                    }
                    foreach (string key in lastConnections) if (!current.Contains(key)) Add(snap, "TCP 采样", "连接结束 " + key, "INFO");
                    lastConnections = current;
                }
                catch (Exception e) { Add(snap, "TCP 采样", "暂时无法读取连接表：" + e.Message, "WARN"); }
            }
            long sampleClock = observations.Timestamp;
            bool queryPhone = traffic.PrepareSampling(snap, mode, sampleClock, relayOk);
            if (queryPhone)
            {
                traffic.BeginAttempt(snap, sampleClock);
                try
                {
                    PhoneCounters c = observations.ReadPhone(state);
                    SessionObservation after = observations.ReadSession();
                    if (after == null || after.State.ProtectionSessionId != session || after.State.WirelessSerial != state.WirelessSerial || after.State.PhoneIdentity != state.PhoneIdentity)
                    {
                        snap.ConnectionState = ConnectionState.Unknown; snap.ProtectionState = ProtectionState.Unknown; snap.Healthy = false;
                        throw new IOException("会话已切换，丢弃旧样本。");
                    }
                    ApplyHealth(snap, after, true);
                    long now = observations.Timestamp;
                    traffic.AcceptSample(c, now, snap, add);
                }
                catch (Exception e)
                {
                    traffic.FailSample(e, snap, add);
                }
            }
            snap.StatusText = snap.ConnectionState == ConnectionState.Connected ?
                (snap.ProtectionState == ProtectionState.Healthy ? "已连接" : "已连接 · 保护异常") :
                snap.ConnectionState == ConnectionState.Unknown ? "连接状态待确认" : "连接恢复中";
            snap.Detail = snap.Healthy ? "手机 VPN 在线 · 失联保护约 " + snap.ProtectionTimeoutSeconds + " 秒 · 最近心跳 " +
                snap.HeartbeatAgeSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " 秒前" :
                snap.ConnectionState == ConnectionState.Connected ? "共享链路可用，失联保护状态异常，请查看保护状态并重新连接。" :
                "正在核实手机与保护心跳；持续失联约 " + snap.ProtectionTimeoutSeconds + " 秒后结束本次共享。";
            if (lastStatus != snap.StatusText)
            {
                Add(snap, "状态", snap.StatusText + " · " + snap.Detail, snap.Healthy ? "INFO" : "WARN"); lastStatus = snap.StatusText;
            }
            ReadMemoryLogs(snap, state); lastSnapshot = snap;
            return snap;
        }
        static void ApplyHealth(Snapshot snapshot, SessionObservation observation, bool phoneVpnConfirmed)
        {
            SessionHealth health = SessionHealth.Assess(true, observation.RelayOwned, observation.MonitorOwned,
                observation.HeartbeatAgeSeconds, phoneVpnConfirmed);
            snapshot.ConnectionState = health.Connection; snapshot.ProtectionState = health.Protection;
            snapshot.HeartbeatAgeSeconds = observation.HeartbeatAgeSeconds; snapshot.Healthy = health.Healthy;
        }
        Snapshot Preview()
        {
            previewTick++;
            Snapshot s = new Snapshot { HasSession = true, Healthy = true, SamplingOk = true, TrafficAvailable = true, IsNewTelemetrySample = true, TelemetryAgeSeconds = 0, SessionId = "preview", Phone = "Android · 界面预览",
                ConnectionState = ConnectionState.Connected, ProtectionState = ProtectionState.Healthy, TelemetryState = TelemetryState.Current,
                Endpoint = "192.0.2.1:37123", StatusText = "预览 · 已连接", Detail = "界面预览 · 所有数据为模拟数据 · 不执行任何手机或网络操作",
                TrafficNote = "预览数据。实际模式统计手机 VPN 接口累计，含协议开销，不代表运营商计费。", HeartbeatAgeSeconds = 1.2,
                DownloadBytes = (ulong)(157286400 + previewTick * 1048576), UploadBytes = 7340032, StartedAt = DateTime.Now.AddMinutes(-18),
                DownloadBytesPerSecond = 1048576 * (1.8 + Math.Sin(previewTick * 0.7)), UploadBytesPerSecond = 32768 * (1.2 + Math.Cos(previewTick)) };
            s.Connections.Add(new ConnectionInfo { Protocol = "TCP", RemoteAddress = "192.0.2.11", RemotePort = 443, State = "已连接" });
            s.Connections.Add(new ConnectionInfo { Protocol = "TCP", RemoteAddress = "198.51.100.12", RemotePort = 14000, State = "已连接" });
            Add(s, "预览", "TCP 10.0.0.2:43686 → 192.0.2.11:443 Open", "INFO");
            return s;
        }
    }
}
