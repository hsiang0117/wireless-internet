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
    // Production read-only observations; this component never starts/stops sharing.
    internal sealed class SystemSessionObservationSource : ISessionObservationSource
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
            CommandResult result = RunBounded(adbPath, "-s " + ProcessExecution.Quote(state.WirelessSerial) + " shell " + ProcessExecution.Quote(query), root, 3500);
            if (result.ExitCode != 0) throw new IOException("手机采样不可用：" + (result.Error + " " + result.Output).Trim());
            return ParsePhone(result.Output, state.PhoneIdentity);
        }
        public MemoryLogBatch ReadLogs(string pipeName, long after) { return MemoryLogWire.Fetch(pipeName, after); }
        public long Timestamp { get { return Stopwatch.GetTimestamp(); } }
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
            SessionRecord s = SessionRecordJson.ReadObservation(ReadShared(path));
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
