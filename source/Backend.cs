using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

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
    public sealed class Backend : IDisposable
    {
        readonly string root;
        readonly bool preview;
        readonly ISessionObservationSource observations;
        string logPipe = "";
        long logCursor, nextLogSequence;
        bool logUnavailable;
        HashSet<string> lastConnections = new HashSet<string>();
        string session = "", counterKey = "", lastStatus = "", lastSampleError = "";
        ulong lastDown, lastUp, savedDown, savedUp;
        double lastPhoneUptime;
        long lastSampleQpc;
        DateTime? savedStart;
        bool savedTrafficAvailable;
        int sampleFailures, stateFailures;
        Snapshot lastSnapshot;
        int previewTick;
        long actionGeneration;

        public Backend(string root, bool preview) : this(root, preview, null) { }
        public Backend(string root, bool preview, ISessionObservationSource observations)
        {
            this.root = Path.GetFullPath(root);
            this.preview = preview;
            this.observations = observations ?? new SystemSessionObservationSource(this.root);
        }
        public static string Quote(string value)
        {
            return ProcessExecution.Quote(value);
        }
        public static CommandResult RunBounded(string exe, string arguments, string workingDirectory, int timeout)
        {
            SharedCommandResult result = ProcessExecution.Run(exe, arguments, new SharedCommandOptions {
                WorkingDirectory = workingDirectory, TimeoutMilliseconds = timeout, DrainTimeoutMilliseconds = 500 });
            if (result.TimedOut) throw new IOException("采样超时；仅结束本次 ADB 查询，后台共享与保护继续运行。");
            if (result.OutputIncomplete) throw new IOException("采样输出暂未就绪。");
            return new CommandResult { ExitCode = result.ExitCode, Output = result.Output, Error = result.Error };
        }
        static string ReadShared(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(file, Encoding.UTF8)) return reader.ReadToEnd();
        }
        public static SessionRecord ReadState(string path)
        {
            SessionRecord s = new JavaScriptSerializer().Deserialize<SessionRecord>(ReadShared(path));
            if (s == null || !Regex.IsMatch(s.ProtectionSessionId ?? "", "^[0-9a-f]{32}$") ||
                !Regex.IsMatch(s.PhoneIdentity ?? "", "^[A-Za-z0-9_-]{1,100}$") ||
                !Regex.IsMatch(s.WirelessSerial ?? "", "^([0-9]{1,3}\\.){3}[0-9]{1,3}:[0-9]{1,5}$"))
                throw new IOException("会话记录格式暂未就绪。");
            return s;
        }
        static bool OwnsProcess(int pid, string ticks, string path)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    return !p.HasExited && p.StartTime.ToUniversalTime().Ticks.ToString() == ticks &&
                        String.Equals(Path.GetFullPath(p.MainModule.FileName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        static bool OwnsMonitor(string root, SessionRecord state)
        {
            string worker = Path.Combine(root, "support", "ConnectionWorker.exe");
            if (state.RuntimeEngine == "CSharp")
                return String.Equals(state.WatchdogPath, worker, StringComparison.OrdinalIgnoreCase) && OwnsProcess(state.WatchdogProcessId, state.WatchdogStartTimeUtcTicks, worker);
            // Accept an existing legacy session only while the user finishes its normal Stop.
            if (!String.IsNullOrEmpty(state.RuntimeEngine) && state.RuntimeEngine != "PowerShell") return false;
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            return OwnsProcess(state.WatchdogProcessId, state.WatchdogStartTimeUtcTicks, legacy);
        }
        static double HeartbeatAge(SessionRecord s)
        {
            long ticks;
            if (!Int64.TryParse(s.MonitorHeartbeatQpcTicks, out ticks)) return double.NaN;
            double age = (Stopwatch.GetTimestamp() - ticks) / (double)Stopwatch.Frequency;
            return age < 0 ? double.NaN : age;
        }
        void Add(Snapshot s, string source, string message, string level)
        {
            if (s.Logs.Count >= 256) return;
            if (message.Length > 4096) message = message.Substring(0, 4092) + " ...";
            s.Logs.Add(new LogEntry { Time = DateTime.Now, Sequence = ++nextLogSequence, Source = source, Message = message, Level = level });
        }
        public static PhoneCounters ParsePhone(string text, string identity)
        {
            Match id = Regex.Match(text, @"(?m)^__IDENTITY__\r?\n([^\r\n]+)");
            if (!id.Success || id.Groups[1].Value.Trim() != identity) throw new IOException("采样端点的手机身份不匹配。");
            Match uptime = Regex.Match(text, @"(?m)^__UPTIME__\r?\n([0-9]+(?:\.[0-9]+)?)");
            double phoneUptime;
            if (!uptime.Success || !Double.TryParse(uptime.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out phoneUptime)) throw new IOException("无法读取手机运行时间。");
            MatchCollection vpns = Regex.Matches(text, @"(?m)^  NetworkAgentInfo\{network\{(?<id>\d+)\}[^\r\n]*ni\{VPN CONNECTED extra: VPN:com\.genymobile\.gnirehtet\}[^\r\n]*?InterfaceName: (?<iface>[A-Za-z0-9_.-]+)[^\r\n]*");
            if (vpns.Count != 2) throw new IOException("手机 gnirehtet VPN 暂未连接，或采样前后的 VPN 状态不完整。");
            Match vpn = vpns[0], afterVpn = vpns[1];
            if (vpn.Groups["id"].Value != afterVpn.Groups["id"].Value || vpn.Groups["iface"].Value != afterVpn.Groups["iface"].Value ||
                Regex.Match(vpn.Value, @"\bcreated=(\S+)").Value != Regex.Match(afterVpn.Value, @"\bcreated=(\S+)").Value)
                throw new IOException("VPN 在采样期间发生切换，丢弃本轮计数。");
            string iface = vpn.Groups["iface"].Value;
            Match dev = Regex.Match(text, @"(?m)^\s*" + Regex.Escape(iface) + @":\s*(?<rx>\d+)(?:\s+\d+){7}\s+(?<tx>\d+)(?:\s+\d+){7}\s*\r?$");
            if (!dev.Success) throw new IOException("VPN 接口 " + iface + " 的字节计数暂不可读。");
            DateTime started;
            Match created = Regex.Match(vpn.Value, @"\bcreated=(\S+)");
            DateTime? start = created.Success && DateTime.TryParse(created.Groups[1].Value, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out started) ? (DateTime?)started.ToLocalTime() : null;
            return new PhoneCounters { Interface = iface, Key = vpn.Groups["id"].Value + "/" + iface + "/" + created.Groups[1].Value,
                Download = UInt64.Parse(dev.Groups["rx"].Value), Upload = UInt64.Parse(dev.Groups["tx"].Value), Uptime = phoneUptime, StartedAt = start };
        }
        sealed class SystemSessionObservationSource : ISessionObservationSource
        {
            readonly string root, statePath, relayPath, adbPath;
            public SystemSessionObservationSource(string root)
            {
                this.root = root; statePath = Path.Combine(root, "state", "wireless-session.json");
                relayPath = Path.Combine(root, "gnirehtet-rust-win64", "gnirehtet.exe"); adbPath = Path.Combine(root, "platform-tools", "adb.exe");
            }
            bool RelayMatches(SessionRecord state)
            { return OwnsProcess(state.RelayProcessId, state.RelayStartTimeUtcTicks, relayPath) && String.Equals(state.RelayPath, relayPath, StringComparison.OrdinalIgnoreCase); }
            public SessionObservation ReadSession()
            {
                if (!File.Exists(statePath)) return null;
                SessionRecord state = ReadState(statePath);
                return new SessionObservation { State = state, RelayOwned = RelayMatches(state), MonitorOwned = OwnsMonitor(root, state), HeartbeatAgeSeconds = HeartbeatAge(state) };
            }
            public List<ConnectionInfo> ReadConnections(SessionRecord state)
            {
                if (!RelayMatches(state)) return new List<ConnectionInfo>();
                List<ConnectionInfo> connections = TcpTable.Get(state.RelayProcessId);
                if (!RelayMatches(state)) connections.Clear();
                return connections;
            }
            public PhoneCounters ReadPhone(SessionRecord state)
            {
                const string query = "printf '__IDENTITY__\\n'; getprop ro.serialno; printf '__UPTIME__\\n'; cat /proc/uptime; printf '__VPN__\\n'; dumpsys connectivity | grep -F 'ni{VPN CONNECTED extra: VPN:com.genymobile.gnirehtet}'; printf '__DEV__\\n'; cat /proc/net/dev; printf '__VPN_AFTER__\\n'; dumpsys connectivity | grep -F 'ni{VPN CONNECTED extra: VPN:com.genymobile.gnirehtet}'";
                CommandResult result = RunBounded(adbPath, "-s " + Quote(state.WirelessSerial) + " shell " + Quote(query), root, 3500);
                if (result.ExitCode != 0) throw new IOException("手机采样不可用：" + (result.Error + " " + result.Output).Trim());
                return ParsePhone(result.Output, state.PhoneIdentity);
            }
            public MemoryLogBatch ReadLogs(string pipeName, long after) { return MemoryLogWire.Fetch(pipeName, after); }
            public long Timestamp { get { return Stopwatch.GetTimestamp(); } }
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
        public Snapshot Poll()
        {
            if (preview) return Preview();
            Snapshot snap = new Snapshot { DownloadBytes = savedDown, UploadBytes = savedUp, StartedAt = savedStart, TrafficAvailable = savedTrafficAvailable,
                TelemetryState = savedTrafficAvailable ? TelemetryState.Stale : TelemetryState.Unavailable,
                TrafficNote = "手机 VPN 接口累计（含协议开销），不代表运营商计费；速度约每秒采样。" };
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
                counterKey = "";
                lastSampleQpc = 0;
                lastStatus = snap.StatusText;
                lastSnapshot = snap;
                return snap;
            }
            SessionRecord state = observation.State;
            if (session != state.ProtectionSessionId)
            {
                session = state.ProtectionSessionId;
                counterKey = ""; lastSampleQpc = 0; sampleFailures = 0;
                savedDown = savedUp = 0; savedStart = null;
                savedTrafficAvailable = snap.TrafficAvailable = false;
                snap.TelemetryState = TelemetryState.Unavailable;
                snap.DownloadBytes = snap.UploadBytes = 0; snap.StartedAt = null;
                lastConnections.Clear(); logPipe = ""; logCursor = 0;
                Add(snap, "会话", "已接管受保护的无线共享会话 " + session.Substring(0, 8) + "；关闭本窗口后仍可继续共享。", "INFO");
            }
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
            if (relayOk)
            {
                try
                {
                    PhoneCounters c = observations.ReadPhone(state);
                    SessionObservation after = observations.ReadSession();
                    if (after == null || after.State.ProtectionSessionId != session || after.State.WirelessSerial != state.WirelessSerial)
                    {
                        snap.ConnectionState = ConnectionState.Unknown; snap.ProtectionState = ProtectionState.Unknown; snap.Healthy = false;
                        throw new IOException("会话已切换，丢弃旧样本。");
                    }
                    ApplyHealth(snap, after, true);
                    long now = observations.Timestamp;
                    bool continuous = counterKey == c.Key && lastSampleQpc > 0 && c.Uptime >= lastPhoneUptime &&
                        c.Download >= lastDown && c.Upload >= lastUp;
                    double seconds = (now - lastSampleQpc) / (double)Stopwatch.Frequency;
                    if (continuous && sampleFailures == 0 && seconds > 0 && seconds < 10)
                    {
                        snap.DownloadBytesPerSecond = (c.Download - lastDown) / seconds;
                        snap.UploadBytesPerSecond = (c.Upload - lastUp) / seconds;
                    }
                    if (counterKey != "" && !continuous) Add(snap, "流量", "VPN 接口或计数已重建，流量与速度基线已重新读取。", "WARN");
                    savedDown = snap.DownloadBytes = lastDown = c.Download;
                    savedUp = snap.UploadBytes = lastUp = c.Upload;
                    savedStart = snap.StartedAt = c.StartedAt;
                    counterKey = c.Key; lastSampleQpc = now; lastPhoneUptime = c.Uptime;
                    snap.SamplingOk = true;
                    snap.TelemetryState = TelemetryState.Current;
                    savedTrafficAvailable = snap.TrafficAvailable = true;
                    if (sampleFailures > 0) Add(snap, "流量", "手机采样已恢复；重新建立速度基线。", "INFO");
                    sampleFailures = 0; lastSampleError = "";
                }
                catch (Exception e)
                {
                    sampleFailures++;
                    snap.TelemetryState = snap.TrafficAvailable ? TelemetryState.Stale : TelemetryState.Unavailable;
                    if (lastSampleError != e.Message) { Add(snap, "手机采样", e.Message, "WARN"); lastSampleError = e.Message; }
                    snap.TrafficNote = "统计采样暂不可用：保留最后流量，速度显示 —。连接与保护状态分别判断。";
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
        public int RunAction(string action, Action<string> output)
        {
            if (output == null) throw new ArgumentNullException("output");
            if (preview) { output("界面预览模式不执行连接操作。"); return 1; }
            if (action != "Start" && action != "Stop") throw new ArgumentException("Unknown action.");
            string worker = Path.Combine(root, "support", "ConnectionWorker.exe");
            if (!File.Exists(worker)) throw new IOException("桌面连接组件缺失，请重新安装完整工具包。");
            long generation = Interlocked.Increment(ref actionGeneration);
            int active = 1;
            try
            {
                // The worker's individual commands are bounded. Never hard-kill a partial Stop.
                SharedCommandResult result = ProcessExecution.Run(worker, new string[] { "--action", action, "--root", root }, new SharedCommandOptions {
                    WorkingDirectory = root, TimeoutMilliseconds = Timeout.Infinite,
                    ProgressLine = delegate(string line, bool error) {
                        if (Thread.VolatileRead(ref active) != 0 && Interlocked.Read(ref actionGeneration) == generation && !String.IsNullOrWhiteSpace(line)) output(line);
                    } });
                if (result.DroppedProgressLines > 0) output("控制进度较多，已略过部分较早消息；操作结果以最终状态为准。");
                if (result.OutputIncomplete) output("控制命令已结束，部分进度输出未及时关闭；请核对最终连接状态。");
                return result.ExitCode;
            }
            finally { Interlocked.Exchange(ref active, 0); }
        }
        Snapshot Preview()
        {
            previewTick++;
            Snapshot s = new Snapshot { HasSession = true, Healthy = true, SamplingOk = true, TrafficAvailable = true, SessionId = "preview", Phone = "Android · 界面预览",
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
        public void Dispose() { /* Closing the GUI never stops relay, monitor, or shared ADB. */ }
    }
    static class TcpTable
    {
        [DllImport("iphlpapi.dll", SetLastError = true)]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);
        static readonly string[] States = { "未知", "关闭", "监听", "SYN 已发送", "SYN 已收到", "已连接", "FIN_WAIT1", "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB" };
        public static List<ConnectionInfo> Get(int owner)
        {
            List<ConnectionInfo> result = new List<ConnectionInfo>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 5, 0);
            if (size <= 0) return result;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint status = GetExtendedTcpTable(buffer, ref size, true, 2, 5, 0);
                if (status != 0) throw new IOException("Windows TCP 表返回 " + status);
                int count = Marshal.ReadInt32(buffer);
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + i * 24);
                    if (Marshal.ReadInt32(row, 20) != owner) continue;
                    int local = Marshal.ReadByte(row, 8) * 256 + Marshal.ReadByte(row, 9);
                    int remote = Marshal.ReadByte(row, 16) * 256 + Marshal.ReadByte(row, 17);
                    if (local == 31416 || remote == 0) continue;
                    uint address = unchecked((uint)Marshal.ReadInt32(row, 12));
                    int state = Marshal.ReadInt32(row, 0);
                    result.Add(new ConnectionInfo { Protocol = "TCP", RemoteAddress = new IPAddress(address).ToString(), RemotePort = remote,
                        State = state >= 0 && state < States.Length ? States[state] : state.ToString() });
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return result.OrderBy(x => x.RemoteAddress, StringComparer.Ordinal).ThenBy(x => x.RemotePort).ToList();
        }
    }
}
