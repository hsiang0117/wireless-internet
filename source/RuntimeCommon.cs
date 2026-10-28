using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WirelessInternet
{
    public sealed class RuntimeCommandResult
    {
        public int ExitCode;
        public string Output = "", Error = "";
        public string Text { get { return ((Output ?? "") + "\n" + (Error ?? "")).Trim(); } set { Output = value ?? ""; Error = ""; } }
        public string[] Lines { get { return Regex.Split(Text, "\\r?\\n"); } set { Text = String.Join("\n", value ?? new string[0]); } }
        public bool Success { get { return ExitCode == 0; } set { ExitCode = value ? 0 : 1; } }
    }
    public class RuntimeState : SessionRecord
    {
        public string Transport = "NativeTls", ConnectEndpoint, RelayHostStartTimeUtcTicks, PhoneControlPath, ProtectionMode;
        public int RelayHostProcessId;
        [ScriptIgnore] public Dictionary<string, object> RawFields;
        public RuntimeState() { ProtectionTimeoutSeconds = 30; }
    }
    public sealed class RuntimeMutexLease : IDisposable
    {
        Mutex mutex;
        internal RuntimeMutexLease(Mutex mutex) { this.mutex = mutex; }
        public void Dispose()
        {
            Mutex owned = Interlocked.Exchange(ref mutex, null);
            if (owned != null) { try { owned.ReleaseMutex(); } finally { owned.Dispose(); } }
        }
    }
    public class RuntimeContext
    {
        public const string CoreMutexName = "Local\\CodexUsbInternetWireless";
        public readonly string Root, AdbPath, RelayPath, WorkerPath, StatePath, RelayHostPath, ApkPath;
        readonly RuntimeSessionStore sessions;
        readonly OwnedProcessManager processes;
        public RuntimeContext(string root)
        {
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            AdbPath = Path.Combine(Root, "platform-tools", "adb.exe");
            RelayPath = Path.Combine(Root, "gnirehtet-rust-win64", "gnirehtet.exe");
            WorkerPath = Path.Combine(Root, "support", "ConnectionWorker.exe");
            RelayHostPath = Path.Combine(Root, "support", "RelayLogHost.exe");
            StatePath = Path.Combine(Root, "state", "wireless-session.json");
            ApkPath = Path.Combine(Root, "gnirehtet-rust-win64", "gnirehtet.apk");
            sessions = new RuntimeSessionStore(Root, StatePath);
            processes = new OwnedProcessManager(Root, RelayPath, WorkerPath);
        }
        public virtual long Timestamp { get { return Stopwatch.GetTimestamp(); } }
        public virtual double ElapsedSeconds(long since) { return (Timestamp - since) / (double)Stopwatch.Frequency; }
        public virtual void Sleep(int milliseconds) { Thread.Sleep(milliseconds); }
        public virtual IDisposable EnterCoreMutex(int timeoutMs = 6000)
        {
            Mutex mutex = new Mutex(false, CoreMutexName);
            bool acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(timeoutMs); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new TimeoutException("另一个连接或配置操作正在进行，请稍后重试。");
                return new RuntimeMutexLease(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }
        public virtual IDisposable TryEnterCoreMutex() { try { return EnterCoreMutex(0); } catch (TimeoutException) { return null; } }
        public static string GetString(IDictionary<string, object> data, string key)
        { return RuntimeSessionStore.GetString(data, key); }
        public static int GetInt(IDictionary<string, object> data, string key, int fallback)
        { return RuntimeSessionStore.GetInt(data, key, fallback); }
        public virtual Dictionary<string, object> ReadConfig() { return sessions.ReadConfig(); }
        public virtual RuntimeState ReadState() { return sessions.ReadState(ValidSerial, ProtectionValid); }
        public virtual void WriteState(RuntimeState state) { sessions.WriteState(state); }
        public virtual void DeleteStateIfToken(string token) { sessions.DeleteStateIfToken(token, ReadState); }
        public static string Quote(string value)
        {
            return ProcessExecution.Quote(value);
        }
        public virtual RuntimeCommandResult Run(string executable, string[] arguments, bool allowFailure = false, int timeoutMs = 8000, IDictionary<string, string> environment = null)
        {
            SharedCommandResult captured = ProcessExecution.Run(executable, arguments, new SharedCommandOptions {
                WorkingDirectory = Root, TimeoutMilliseconds = timeoutMs,
                MaximumOutputCharacters = 65536, MaximumErrorCharacters = 65536,
                DrainTimeoutMilliseconds = 1000, Environment = environment
            });
            if (captured.TimedOut)
            {
                if (!allowFailure) throw new IOException("操作超时；仅结束本次命令客户端，请重试。");
                return new RuntimeCommandResult { ExitCode = 124, Error = "Command timed out." };
            }
            if (captured.OutputIncomplete) throw new IOException("命令输出未及时结束。");
            if (captured.InputFailed) throw new IOException("命令输入未完成。");
            var result = new RuntimeCommandResult { ExitCode = captured.ExitCode, Output = captured.Output, Error = captured.Error };
            if (!allowFailure && !result.Success) throw new IOException("操作未完成：" + result.Text);
            return result;
        }
        public virtual RuntimeCommandResult Adb(string[] arguments, bool allowFailure = false, int timeoutMs = 8000) { return Run(AdbPath, arguments, allowFailure, timeoutMs); }
        public virtual RuntimeCommandResult Gnirehtet(string[] arguments, bool allowFailure = false, int timeoutMs = 8000)
        {
            return Run(RelayPath, arguments, allowFailure, timeoutMs, new Dictionary<string, string> { { "ADB", AdbPath }, { "GNIREHTET_APK", ApkPath } });
        }
        public virtual bool ProtectionValid(RuntimeState state)
        {
            if (state == null || !Regex.IsMatch(state.ProtectionSessionId ?? "", "^[0-9a-f]{32}$") ||
                state.ProtectionTimeoutSeconds < 15 || state.ProtectionTimeoutSeconds > 120) return false;
            if (state.ProtectionMode == "PcOnly") return String.IsNullOrEmpty(state.PhoneControlPath);
            // Legacy records remain readable for positively-dead startup recovery.
            // Live legacy sessions must be disconnected with their original version.
            return String.IsNullOrEmpty(state.ProtectionMode) &&
                state.PhoneControlPath == "/data/local/tmp/codex-wireless-internet/" + state.ProtectionSessionId + "/control.sh";
        }
        public static bool ValidSerial(string serial)
        {
            if (!Regex.IsMatch(serial ?? "", "^(?:[0-9]{1,3}[.]){3}[0-9]{1,3}:[0-9]{1,5}$")) return false;
            int colon = serial.LastIndexOf(':'), port; System.Net.IPAddress address;
            return Int32.TryParse(serial.Substring(colon + 1), out port) && port > 0 && port <= 65535 &&
                System.Net.IPAddress.TryParse(serial.Substring(0, colon), out address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }
        public virtual bool PhoneVpnConnected(string serial)
        {
            RuntimeCommandResult result = Adb(new string[] { "-s", serial, "shell", "dumpsys", "connectivity" });
            return AndroidProtocol.PhoneVpnConnected(result.Text);
        }
        public virtual bool MonitorHeartbeatFresh(RuntimeState state)
        {
            long stamp; if (state == null || !Int64.TryParse(state.MonitorHeartbeatQpcTicks, out stamp)) return false;
            double age = ElapsedSeconds(stamp); return age >= 0 && age <= 12;
        }
        public static Process GetOwnedProcess(int pid, string ticks, string expectedPath, bool pinHandle, bool rejectSelf)
        { return OwnedProcessManager.GetOwnedProcess(pid, ticks, expectedPath, pinHandle, rejectSelf); }
        public virtual Process GetOwnedRelay(RuntimeState state, bool pinHandle = false)
        { return processes.GetOwnedRelay(state, pinHandle); }
        public virtual Process GetOwnedMonitor(RuntimeState state, bool pinHandle = false)
        { return processes.GetOwnedMonitor(state, pinHandle); }
        public virtual RecordedProcessPresence ProbeRecordedProcess(int pid, string ticks, string expectedPath)
        { return OwnedProcessManager.ProbeRecordedProcess(pid, ticks, expectedPath); }
        public virtual bool IsRelayPortIdleForStaleCleanup()
        {
            // These APIs include IPv4 and IPv6. A query error must propagate as unknown.
            IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();
            return !properties.GetActiveTcpListeners().Any(delegate(System.Net.IPEndPoint endpoint) { return endpoint.Port == 31416; }) &&
                !properties.GetActiveTcpConnections().Any(delegate(TcpConnectionInformation connection) {
                    return connection.LocalEndPoint.Port == 31416 && connection.State == TcpState.Established;
                });
        }
        public virtual StaleSessionInspection InspectStaleSession(RuntimeState state)
        { return processes.InspectForRetirement(state, ProbeRecordedProcess, IsRelayPortIdleForStaleCleanup); }
        public virtual int[] GetTcpPortOwners(int port, bool established = false) { return RuntimeTcp.GetOwners(port, established); }
        public virtual Process StartDetached(string executable, string[] arguments)
        { return processes.StartDetached(executable, arguments); }
        public virtual NetworkLink ResolveNetwork(IDictionary<string, object> config, RuntimeState expected = null, bool wifiOnly = false) { return NativeNetwork.Read(config, expected, wifiOnly); }
        public virtual bool WifiHasIPv6Default(int index) { return NativeNetwork.WifiHasIPv6Default(index); }
        public virtual void ApplyNetwork(RuntimeState state, NetworkLink link)
        {
            state.WifiInterfaceIndex = link.WifiInterfaceIndex; state.EthernetInterfaceIndex = link.EthernetInterfaceIndex;
            state.WifiInterfaceGuid = link.WifiInterfaceGuid; state.EthernetInterfaceGuid = link.EthernetInterfaceGuid;
            state.WifiInterfaceAlias = link.WifiInterfaceAlias; state.EthernetInterfaceAlias = link.EthernetInterfaceAlias;
            state.PhoneGateway = link.PhoneGateway; state.WifiRouteCost = link.WifiRouteCost; state.EthernetRouteCost = link.EthernetRouteCost;
        }
    }
    public static class RuntimeTcp
    {
        [DllImport("iphlpapi.dll", SetLastError = true)] static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted, int family, int tableClass, uint reserved);
        public static int[] GetOwners(int port, bool established)
        {
            int size = 0; uint first = GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 5, 0);
            if (first != 122 && first != 0) throw new IOException("无法读取 TCP 连接归属：" + first);
            if (size <= 0) return new int[0];
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint code = GetExtendedTcpTable(buffer, ref size, true, 2, 5, 0);
                if (code != 0) throw new IOException("TCP 连接归属读取失败：" + code);
                int count = Marshal.ReadInt32(buffer); HashSet<int> result = new HashSet<int>();
                if (count < 0 || count > (size - 4) / 24) throw new IOException("TCP 表长度无效。");
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = IntPtr.Add(buffer, 4 + i * 24);
                    int state = Marshal.ReadInt32(row, 0), local = Marshal.ReadByte(row, 8) * 256 + Marshal.ReadByte(row, 9);
                    if (local == port && state == (established ? 5 : 2)) result.Add(Marshal.ReadInt32(row, 20));
                }
                return result.ToArray();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
