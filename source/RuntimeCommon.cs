using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
    public class RuntimeState : NetworkLink
    {
        public string Transport = "NativeTls", PhoneIdentity, ConnectEndpoint, RelayPath, RelayStartTimeUtcTicks;
        public string RelayLogPipe, LogMode, RelayHostStartTimeUtcTicks;
        public string ProtectionSessionId, PhoneControlPath, WatchdogStartTimeUtcTicks, MonitorHeartbeatQpcTicks;
        public string WatchdogPath, RuntimeEngine;
        public int RelayProcessId, RelayHostProcessId, ProtectionTimeoutSeconds = 30, WatchdogProcessId;
        [ScriptIgnore] public Dictionary<string, object> RawFields;
    }
    public sealed class PhoneGuardLease : IDisposable
    {
        public readonly string Path;
        readonly string directory;
        internal PhoneGuardLease(string directory, string path) { this.directory = directory; Path = path; }
        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
            try { Directory.Delete(directory, false); } catch { }
        }
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
        public RuntimeContext(string root)
        {
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            AdbPath = Path.Combine(Root, "platform-tools", "adb.exe");
            RelayPath = Path.Combine(Root, "gnirehtet-rust-win64", "gnirehtet.exe");
            WorkerPath = Path.Combine(Root, "support", "ConnectionWorker.exe");
            RelayHostPath = Path.Combine(Root, "support", "RelayLogHost.exe");
            StatePath = Path.Combine(Root, "state", "wireless-session.json");
            ApkPath = Path.Combine(Root, "gnirehtet-rust-win64", "gnirehtet.apk");
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
        {
            object value; return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
        public static int GetInt(IDictionary<string, object> data, string key, int fallback)
        {
            string text = GetString(data, key); int value;
            if (text.Length == 0) return fallback;
            if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) throw new IOException("配置中的 " + key + " 无效。");
            return value;
        }
        public virtual Dictionary<string, object> ReadConfig()
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadShared(Path.Combine(Root, "wireless-config.json")));
            if (data == null || GetString(data, "Transport") != "NativeTls") throw new IOException("请先完成原生无线调试的手机配置。");
            string identity = GetString(data, "PhoneIdentity");
            if (!Regex.IsMatch(identity, "^[A-Za-z0-9_-]{1,100}$")) throw new IOException("请点击“更换/绑定手机”，先配对、检测并保存手机。");
            int timeout = GetInt(data, "DisconnectTimeoutSeconds", 30);
            if (timeout < 15 || timeout > 120) throw new IOException("失联保护时间必须为 15–120 秒。");
            return data;
        }
        public virtual RuntimeState ReadState()
        {
            string text;
            try { text = ReadShared(StatePath); } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
            JavaScriptSerializer json = new JavaScriptSerializer();
            RuntimeState state = json.Deserialize<RuntimeState>(text);
            if (state == null || state.Transport != "NativeTls" || !Regex.IsMatch(state.PhoneIdentity ?? "", "^[A-Za-z0-9_-]{1,100}$") ||
                !ValidSerial(state.WirelessSerial) || !ProtectionValid(state)) throw new IOException("运行会话格式无效，未修改进程或手机状态。");
            state.RawFields = json.Deserialize<Dictionary<string, object>>(text);
            return state;
        }
        public virtual void WriteState(RuntimeState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            JavaScriptSerializer json = new JavaScriptSerializer();
            var data = json.Deserialize<Dictionary<string, object>>(json.Serialize(state));
            if (state.RawFields != null) foreach (var entry in state.RawFields) if (!data.ContainsKey(entry.Key)) data.Add(entry.Key, entry.Value);
            string temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json.Serialize(data), new UTF8Encoding(false));
                if (File.Exists(StatePath)) File.Replace(temporary, StatePath, null); else File.Move(temporary, StatePath);
            }
            finally { try { File.Delete(temporary); } catch { } }
        }
        public virtual void DeleteStateIfToken(string token)
        {
            RuntimeState current = ReadState();
            if (current != null && String.Equals(current.ProtectionSessionId, token, StringComparison.Ordinal)) File.Delete(StatePath);
        }
        static string ReadShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                if (stream.Length > 262144) throw new IOException("运行配置或会话文件过大。");
                return reader.ReadToEnd();
            }
        }
        public static string Quote(string value)
        {
            if (value.Length > 0 && !Regex.IsMatch(value, "[\\s\"]")) return value;
            return "\"" + Regex.Replace(Regex.Replace(value, "(\\\\*)\"", "$1$1\\\""), "(\\\\+)$", "$1$1") + "\"";
        }
        public virtual RuntimeCommandResult Run(string executable, string[] arguments, bool allowFailure = false, int timeoutMs = 8000, IDictionary<string, string> environment = null)
        {
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable, String.Join(" ", arguments.Select(Quote))) { UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = Root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                if (environment != null) foreach (var pair in environment) process.StartInfo.EnvironmentVariables[pair.Key] = pair.Value;
                process.Start(); process.StandardInput.Close();
                Task<string> stdout = Task.Factory.StartNew(delegate { return ReadCapped(process.StandardOutput); }, TaskCreationOptions.LongRunning);
                Task<string> stderr = Task.Factory.StartNew(delegate { return ReadCapped(process.StandardError); }, TaskCreationOptions.LongRunning);
                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); process.WaitForExit(1000); } catch { }
                    if (!allowFailure) throw new IOException("操作超时；仅结束本次命令客户端，请重试。");
                    return new RuntimeCommandResult { ExitCode = 124, Error = "Command timed out." };
                }
                bool drained = Task.WaitAll(new Task[] { stdout, stderr }, 1000);
                if (!drained) throw new IOException("命令输出未及时结束。");
                var result = new RuntimeCommandResult { ExitCode = process.ExitCode, Output = stdout.Result, Error = stderr.Result };
                if (!allowFailure && !result.Success) throw new IOException("操作未完成：" + result.Text);
                return result;
            }
        }
        static string ReadCapped(StreamReader reader)
        {
            StringBuilder text = new StringBuilder(); char[] block = new char[2048]; int count;
            while ((count = reader.Read(block, 0, block.Length)) > 0) { int keep = Math.Min(count, 65536 - text.Length); if (keep > 0) text.Append(block, 0, keep); }
            return text.ToString();
        }
        public virtual RuntimeCommandResult Adb(string[] arguments, bool allowFailure = false, int timeoutMs = 8000) { return Run(AdbPath, arguments, allowFailure, timeoutMs); }
        public virtual RuntimeCommandResult Gnirehtet(string[] arguments, bool allowFailure = false, int timeoutMs = 8000)
        {
            return Run(RelayPath, arguments, allowFailure, timeoutMs, new Dictionary<string, string> { { "ADB", AdbPath }, { "GNIREHTET_APK", ApkPath } });
        }
        public virtual bool ProtectionValid(RuntimeState state)
        {
            return state != null && Regex.IsMatch(state.ProtectionSessionId ?? "", "^[0-9a-f]{32}$") &&
                state.PhoneControlPath == "/data/local/tmp/codex-wireless-internet/" + state.ProtectionSessionId + "/control.sh" &&
                state.ProtectionTimeoutSeconds >= 15 && state.ProtectionTimeoutSeconds <= 120;
        }
        public static bool ValidSerial(string serial)
        {
            if (!Regex.IsMatch(serial ?? "", "^(?:[0-9]{1,3}[.]){3}[0-9]{1,3}:[0-9]{1,5}$")) return false;
            int colon = serial.LastIndexOf(':'), port; System.Net.IPAddress address;
            return Int32.TryParse(serial.Substring(colon + 1), out port) && port > 0 && port <= 65535 &&
                System.Net.IPAddress.TryParse(serial.Substring(0, colon), out address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        }
        public virtual RuntimeCommandResult Guard(RuntimeState state, string command, bool allowFailure = false, int timeoutMs = 8000)
        {
            if (!ProtectionValid(state) || !ValidSerial(state.WirelessSerial) || !Regex.IsMatch(command, "^(?:arm|beat|cancel|stop|status)$")) throw new IOException("手机保护会话无效。");
            return Adb(new string[] { "-s", state.WirelessSerial, "shell", "sh", state.PhoneControlPath, command, state.ProtectionSessionId,
                state.ProtectionTimeoutSeconds.ToString(CultureInfo.InvariantCulture) }, allowFailure, timeoutMs);
        }
        public virtual bool PhoneVpnConnected(string serial)
        {
            RuntimeCommandResult result = Adb(new string[] { "-s", serial, "shell", "dumpsys", "connectivity" });
            return Regex.IsMatch(result.Text, @"(?m)^  NetworkAgentInfo\{network\{\d+\}[^\r\n]*ni\{VPN CONNECTED extra: VPN:com\.genymobile\.gnirehtet\}");
        }
        public virtual bool MonitorHeartbeatFresh(RuntimeState state)
        {
            long stamp; if (state == null || !Int64.TryParse(state.MonitorHeartbeatQpcTicks, out stamp)) return false;
            double age = ElapsedSeconds(stamp); return age >= 0 && age <= 12;
        }
        public static Process GetOwnedProcess(int pid, string ticks, string expectedPath, bool pinHandle, bool rejectSelf)
        {
            if (pid <= 0) return null;
            Process process;
            try { process = Process.GetProcessById(pid); } catch (ArgumentException) { return null; }
            try
            {
                // Pin before checking identity; a recycled PID can never retarget a subsequent Kill.
                if (pinHandle) { IntPtr held = process.Handle; }
                if (process.HasExited) { process.Dispose(); return null; }
                if (rejectSelf && pid == Process.GetCurrentProcess().Id) throw new IOException("拒绝结束当前控制进程。");
                if (process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) != ticks ||
                    !String.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("已记录进程的身份发生变化，未结束其他进程。");
                return process;
            }
            catch { process.Dispose(); throw; }
        }
        public virtual Process GetOwnedRelay(RuntimeState state, bool pinHandle = false)
        {
            if (state == null) return null;
            if (!String.Equals(Path.GetFullPath(state.RelayPath ?? ""), RelayPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("中继路径与当前工具不符。");
            return GetOwnedProcess(state.RelayProcessId, state.RelayStartTimeUtcTicks, RelayPath, pinHandle, true);
        }
        public virtual Process GetOwnedMonitor(RuntimeState state, bool pinHandle = false)
        {
            if (state == null || state.WatchdogProcessId <= 0) return null;
            string expected;
            if (state.RuntimeEngine == "CSharp")
            {
                if (!String.Equals(Path.GetFullPath(state.WatchdogPath ?? ""), WorkerPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("监控路径与当前工具不符。");
                expected = WorkerPath;
            }
            else if (String.IsNullOrEmpty(state.RuntimeEngine) || state.RuntimeEngine == "PowerShell")
            {
                // Compatibility check only: never execute the retired engine.
                expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            }
            else throw new IOException("无法识别运行会话中的监控引擎。");
            return GetOwnedProcess(state.WatchdogProcessId, state.WatchdogStartTimeUtcTicks, expected, pinHandle, true);
        }
        public virtual int[] GetTcpPortOwners(int port, bool established = false) { return RuntimeTcp.GetOwners(port, established); }
        public virtual Process StartDetached(string executable, string[] arguments)
        {
            // ShellExecuteEx starts these trusted background helpers without inheriting the
            // action worker's output pipe. Clearing standard handles alone is insufficient
            // when Windows has also inherited unnamed copies of an output handle.
            return Process.Start(new ProcessStartInfo(executable, String.Join(" ", arguments.Select(Quote))) {
                UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Root });
        }
        public virtual NetworkLink ResolveNetwork(IDictionary<string, object> config, RuntimeState expected = null, bool wifiOnly = false) { return NativeNetwork.Read(config, expected, wifiOnly); }
        public virtual bool WifiHasIPv6Default(int index) { return NativeNetwork.WifiHasIPv6Default(index); }
        public virtual void ApplyNetwork(RuntimeState state, NetworkLink link)
        {
            state.WifiInterfaceIndex = link.WifiInterfaceIndex; state.EthernetInterfaceIndex = link.EthernetInterfaceIndex;
            state.WifiInterfaceGuid = link.WifiInterfaceGuid; state.EthernetInterfaceGuid = link.EthernetInterfaceGuid;
            state.WifiInterfaceAlias = link.WifiInterfaceAlias; state.EthernetInterfaceAlias = link.EthernetInterfaceAlias;
            state.PhoneGateway = link.PhoneGateway; state.WifiRouteCost = link.WifiRouteCost; state.EthernetRouteCost = link.EthernetRouteCost;
        }
        public virtual PhoneGuardLease ExtractPhoneGuard()
        {
            string directory = Path.Combine(Path.GetTempPath(), "WirelessInternet-guard-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "control.sh");
            try { File.WriteAllText(file, PhoneGuardSource.Text.Replace("\r\n", "\n"), new UTF8Encoding(false)); return new PhoneGuardLease(directory, file); }
            catch { try { Directory.Delete(directory, false); } catch { } throw; }
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
