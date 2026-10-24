using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WirelessInternet
{
    public sealed class SetupCommandResult
    {
        public int ExitCode;
        public string Output = "", Error = "";
    }

    public interface ISetupCommandRunner
    {
        SetupCommandResult Run(string executable, string arguments, string standardInput, int timeoutMilliseconds);
    }

    public sealed class SetupNetworkInfo
    {
        public string Gateway, InterfaceName;
        public int WifiInterfaceIndex;
    }

    public interface ISetupNetworkProbe
    {
        List<SetupNetworkInfo> GetWirelessGateways();
    }

    public sealed class SetupTarget
    {
        public string Endpoint, Gateway;
        public int WifiInterfaceIndex;
        public bool MultipleWirelessInterfaces;
    }

    public sealed class SetupVerification
    {
        public string PhoneIdentity, ConnectEndpoint;
        public int AndroidSdk;
    }

    public sealed class SetupBackend
    {
        readonly string root, adbPath, configPath, statePath;
        readonly ISetupCommandRunner runner;
        readonly ISetupNetworkProbe networks;
        const string CoreMutex = "Local\\CodexUsbInternetWireless";

        public SetupBackend(string root) : this(root, new SetupProcessRunner(), new SetupNetworkProbe()) { }
        public SetupBackend(string root, ISetupCommandRunner runner) : this(root, runner, new SetupNetworkProbe()) { }
        public SetupBackend(string root, ISetupCommandRunner runner, ISetupNetworkProbe networks)
        {
            this.root = Path.GetFullPath(root);
            this.runner = runner ?? throwArgument();
            if (networks == null) throw new ArgumentNullException("networks");
            this.networks = networks;
            adbPath = Path.Combine(this.root, "platform-tools", "adb.exe");
            configPath = Path.Combine(this.root, "wireless-config.json");
            statePath = Path.Combine(this.root, "state", "wireless-session.json");
        }

        static ISetupCommandRunner throwArgument() { throw new ArgumentNullException("runner"); }

        public static bool IsConfigurationComplete(string root)
        {
            try
            {
                string path = Path.Combine(root, "wireless-config.json");
                if (!File.Exists(path)) return false;
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                return data != null && GetString(data, "Transport") == "NativeTls" &&
                    Regex.IsMatch(GetString(data, "PhoneIdentity"), "^[A-Za-z0-9_-]{1,100}$");
            }
            catch { return false; }
        }

        public bool IsConfigurationComplete() { return IsConfigurationComplete(root); }

        public static bool HasSessionRecord(string root)
        {
            try { File.GetAttributes(Path.Combine(root, "state", "wireless-session.json")); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        public Dictionary<string, object> ReadConfiguration()
        {
            try
            {
                if (!File.Exists(configPath)) return new Dictionary<string, object>();
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configPath, Encoding.UTF8));
                if (data == null) throw new IOException();
                return data;
            }
            catch { throw new IOException("无法读取手机配置。请确认安装目录可读，配置文件为有效 JSON。"); }
        }

        public static string GetString(Dictionary<string, object> data, string key)
        {
            object value;
            return data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : String.Empty;
        }

        public static int GetIndex(Dictionary<string, object> data, string key)
        {
            int value;
            return Int32.TryParse(GetString(data, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0 ? value : 0;
        }

        public void EnsureCanConfigure()
        {
            WithLock(delegate { EnsureNoSession(); });
        }

        void EnsureNoSession()
        {
            if (HasSessionRecord(root)) throw new IOException("当前共享会话尚未结束。请先点击“断开连接”，再修改手机设置。");
        }

        void WithLock(Action action)
        {
            using (Mutex mutex = new Mutex(false, CoreMutex))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(0); }
                    catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("另一个连接或配置操作正在进行，请稍后重试。");
                    action();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        public SetupTarget ResolveTarget(string enteredAddress, int wifiInterfaceIndex)
        {
            string address;
            int port;
            ParseAddress(enteredAddress, out address, out port);
            List<SetupNetworkInfo> candidates = networks.GetWirelessGateways() ?? new List<SetupNetworkInfo>();
            if (wifiInterfaceIndex > 0) candidates = candidates.FindAll(delegate(SetupNetworkInfo item) { return item.WifiInterfaceIndex == wifiInterfaceIndex; });
            if (candidates.Count > 1 && !String.IsNullOrEmpty(address))
            {
                List<SetupNetworkInfo> matched = candidates.FindAll(delegate(SetupNetworkInfo item) { return item.Gateway == address; });
                if (matched.Count == 1) return Target(matched[0], port, true);
            }
            if (candidates.Count == 0) throw new IOException("请先让电脑连接手机热点。未找到具有 IPv4 网关的可用 Wi-Fi 网卡；指定索引时也请确认网卡已连接。");
            if (candidates.Count != 1) throw new IOException("检测到多个 Wi-Fi 网关。请输入当前手机热点的网关 IP:端口，或在高级选项填写 Wi-Fi 网卡索引。");
            return Target(candidates[0], port, false);
        }

        static SetupTarget Target(SetupNetworkInfo network, int port, bool multiple)
        {
            IPAddress gateway;
            if (!IPAddress.TryParse(network.Gateway, out gateway) || gateway.AddressFamily != AddressFamily.InterNetwork || gateway.Equals(IPAddress.Any))
                throw new IOException("手机热点 IPv4 网关无效。");
            return new SetupTarget { Endpoint = gateway + ":" + port.ToString(CultureInfo.InvariantCulture), Gateway = gateway.ToString(),
                WifiInterfaceIndex = network.WifiInterfaceIndex, MultipleWirelessInterfaces = multiple };
        }

        static void ParseAddress(string text, out string address, out int port)
        {
            text = (text ?? String.Empty).Trim();
            Match match = Regex.Match(text, "^(?:(?<ip>[0-9]{1,3}(?:[.][0-9]{1,3}){3}):)?(?<port>[0-9]{1,5})$");
            IPAddress parsed;
            address = match.Groups["ip"].Value;
            port = 0;
            if (!match.Success || !Int32.TryParse(match.Groups["port"].Value, out port) || port < 1 || port > 65535 ||
                (address.Length > 0 && (!IPAddress.TryParse(address, out parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)))
                throw new IOException("请输入端口号（1–65535），或 IPv4地址:端口。例如 37123 或 192.168.43.1:37123。");
        }

        public string Pair(string pairingAddress, string pairingCode, Action<string> progress)
        {
            return Pair(pairingAddress, pairingCode, 0, progress);
        }

        public string Pair(string pairingAddress, string pairingCode, int wifiInterfaceIndex, Action<string> progress)
        {
            if (!Regex.IsMatch(pairingCode ?? String.Empty, "^[0-9]{6}$")) throw new IOException("请输入手机“使用配对码配对设备”页面上的 6 位数字配对码。");
            SetupTarget target = ResolveTarget(pairingAddress, wifiInterfaceIndex);
            string paired = null;
            SetupCommandResult result = null;
            try
            {
                    EnsureCanConfigure();
                    Report(progress, "正在通过手机热点进行配对…");
                    try
                    {
                        // The code never appears in arguments, status text, configuration, or files.
                        result = runner.Run(adbPath, "pair " + target.Endpoint, pairingCode + "\n", 25000);
                        if (result.ExitCode != 0 || (result.Output + " " + result.Error).IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) < 0)
                            throw new IOException();
                    }
                    catch { throw new IOException("配对未完成。请重新打开手机的配对码页面，核对配对端口与新配对码后重试；连接端口不能代替配对端口。"); }
                    EnsureCanConfigure();
                    paired = target.Endpoint;
                    Report(progress, "配对成功。现在填写无线调试主页面的连接端口，检测并保存。");
            }
            finally
            {
                pairingCode = null;
                if (result != null) { result.Output = String.Empty; result.Error = String.Empty; }
            }
            return paired;
        }

        public SetupVerification VerifyAndSave(string connectAddress, Action<string> progress)
        {
            return VerifyAndSave(connectAddress, 0, 0, progress);
        }

        public SetupVerification VerifyAndSave(string connectAddress, int wifiInterfaceIndex, int ethernetInterfaceIndex, Action<string> progress)
        {
            if (wifiInterfaceIndex < 0 || ethernetInterfaceIndex < 0) throw new IOException("网卡索引必须为 0 或正整数。");
            SetupTarget target = ResolveTarget(connectAddress, wifiInterfaceIndex);
            SetupVerification verified = null;
                EnsureCanConfigure();
                Report(progress, "正在连接已配对的手机…");
                SetupCommandResult connect = Run("connect " + target.Endpoint, 12000);
                if (connect.ExitCode != 0 || Regex.IsMatch(connect.Output + " " + connect.Error, "failed|cannot|unable|refused", RegexOptions.IgnoreCase))
                    throw new IOException("无法连接手机。请确认已配对、手机热点和无线调试保持开启，并填写主页面的连接端口。");
                string serial = Phone(target.Endpoint, "getprop ro.serialno");
                if (!Regex.IsMatch(serial, "^[A-Za-z0-9_-]{1,100}$")) throw new IOException("无法读取唯一手机标识，暂不能安全保存此设备。");
                int sdk;
                if (!Int32.TryParse(Phone(target.Endpoint, "getprop ro.build.version.sdk"), out sdk) || sdk < 30)
                    throw new IOException("需要 Android 11 或更高版本，以使用系统原生无线调试。");
                if (Phone(target.Endpoint, "am get-current-user") != "0") throw new IOException("请切换到手机机主用户（user 0），暂不支持访客或工作资料用户。");
                if (Phone(target.Endpoint, "settings get global adb_wifi_enabled") != "1") throw new IOException("手机系统“无线调试”开关未开启，请手动开启后重试。");
                if (Phone(target.Endpoint, "getprop ro.kernel.qemu") == "1" || Phone(target.Endpoint, "getprop ro.boot.qemu") == "1")
                    throw new IOException("检测到安卓模拟器。请选择需要共享网络的真实手机。");
                VerifyUniquePhone(target.Endpoint, serial);
            WithLock(delegate
            {
                // Re-read the gateway before committing: a Wi-Fi switch invalidates the tested endpoint.
                SetupTarget latest = ResolveTarget(connectAddress, wifiInterfaceIndex);
                if (latest.Endpoint != target.Endpoint || latest.WifiInterfaceIndex != target.WifiInterfaceIndex)
                    throw new IOException("电脑的热点连接在检测期间发生变化，请保持连接后重试。");
                EnsureNoSession();
                Dictionary<string, object> data = ReadConfiguration();
                data["Transport"] = "NativeTls";
                data["PhoneIdentity"] = serial;
                data["DisconnectTimeoutSeconds"] = 30;
                data["ConnectEndpoint"] = target.Endpoint;
                data["WifiInterfaceIndex"] = wifiInterfaceIndex > 0 ? wifiInterfaceIndex : target.MultipleWirelessInterfaces ? target.WifiInterfaceIndex : 0;
                data["EthernetInterfaceIndex"] = ethernetInterfaceIndex;
                // The wizard selects by index; discard older alias constraints after reconfiguration.
                data.Remove("WifiInterfaceAlias");
                data.Remove("EthernetInterfaceAlias");
                SaveAtomic(data);
                verified = new SetupVerification { PhoneIdentity = serial, ConnectEndpoint = target.Endpoint, AndroidSdk = sdk };
                Report(progress, "检测通过，手机配置已保存。可返回主界面点击“开始连接”。");
            });
            return verified;
        }

        SetupCommandResult Run(string arguments, int timeout)
        {
            try { return runner.Run(adbPath, arguments, null, timeout); }
            catch { throw new IOException("ADB 检测未完成。请确认工具完整、无线调试授权有效，并保持电脑连接手机热点。"); }
        }

        string Phone(string endpoint, string command)
        {
            SetupCommandResult result = Run("-s " + endpoint + " shell " + command, 7000);
            if (result.ExitCode != 0) throw new IOException("手机未响应检测。请确认无线调试已授权，稍后重试。");
            return (result.Output ?? String.Empty).Trim();
        }

        void VerifyUniquePhone(string endpoint, string identity)
        {
            SetupCommandResult result = Run("devices -l", 7000);
            if (result.ExitCode != 0) throw new IOException("无法确认设备列表，请稍后重试。");
            bool found = false;
            foreach (string line in (result.Output ?? String.Empty).Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)) continue;
                Match device = Regex.Match(line, "^(?<id>[^\\s]+)[ \\t]+(?<state>device|offline|unauthorized|recovery|sideload|no permissions)\\b");
                if (!device.Success) continue;
                string id = device.Groups["id"].Value;
                if (id.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)) continue;
                bool same = id == endpoint || id == identity || id.StartsWith("adb-" + identity + "-", StringComparison.Ordinal);
                if (!same) throw new IOException("ADB 中存在其它真实手机或未知设备连接。请先断开其它手机或过期无线端点，再检测；本程序不会关闭共享 ADB 服务。");
                if (device.Groups["state"].Value == "device") found = true;
            }
            if (!found) throw new IOException("目标手机尚未在 ADB 设备列表中就绪，请重新确认授权与连接端口。");
        }

        void SaveAtomic(Dictionary<string, object> data)
        {
            string temporary = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(data), new UTF8Encoding(false));
                if (File.Exists(configPath)) File.Replace(temporary, configPath, null);
                else File.Move(temporary, configPath);
            }
            catch { throw new IOException("配置无法写入。请确认安装目录可写，建议安装到当前用户的本地目录。"); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        static void Report(Action<string> report, string message) { if (report != null) report(message); }
    }

    public sealed class SetupNetworkProbe : ISetupNetworkProbe
    {
        public List<SetupNetworkInfo> GetWirelessGateways()
        {
            List<SetupNetworkInfo> result = new List<SetupNetworkInfo>();
            foreach (NetworkInterface item in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (item.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 || item.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    IPInterfaceProperties properties = item.GetIPProperties();
                    int index = properties.GetIPv4Properties().Index;
                    foreach (GatewayIPAddressInformation gateway in properties.GatewayAddresses)
                    {
                        if (gateway.Address.AddressFamily != AddressFamily.InterNetwork || gateway.Address.Equals(IPAddress.Any)) continue;
                        if (!result.Exists(delegate(SetupNetworkInfo value) { return value.WifiInterfaceIndex == index && value.Gateway == gateway.Address.ToString(); }))
                            result.Add(new SetupNetworkInfo { Gateway = gateway.Address.ToString(), InterfaceName = item.Name, WifiInterfaceIndex = index });
                    }
                }
                catch (NetworkInformationException) { }
            }
            return result;
        }
    }

    public sealed class SetupProcessRunner : ISetupCommandRunner
    {
        public SetupCommandResult Run(string executable, string arguments, string standardInput, int timeoutMilliseconds)
        {
            if (!File.Exists(executable)) throw new IOException("ADB 工具缺失，请重新安装完整工具包。");
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable, arguments) { WorkingDirectory = Path.GetDirectoryName(executable),
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = standardInput != null,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                process.Start();
                Task<string> stdout = Task.Factory.StartNew(delegate { return ReadCapped(process.StandardOutput); }, TaskCreationOptions.LongRunning);
                Task<string> stderr = Task.Factory.StartNew(delegate { return ReadCapped(process.StandardError); }, TaskCreationOptions.LongRunning);
                if (standardInput != null)
                {
                    try { process.StandardInput.Write(standardInput); process.StandardInput.Flush(); }
                    finally { standardInput = null; process.StandardInput.Close(); }
                }
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try { process.Kill(); process.WaitForExit(1000); } catch { }
                    throw new IOException("ADB 操作超时，仅结束本次检测命令，请重试。");
                }
                if (!Task.WaitAll(new Task[] { stdout, stderr }, 1000)) throw new IOException("ADB 检测输出未完成，请重试。");
                return new SetupCommandResult { ExitCode = process.ExitCode, Output = stdout.Result, Error = stderr.Result };
            }
        }

        static string ReadCapped(StreamReader reader)
        {
            const int limit = 16384;
            StringBuilder text = new StringBuilder(1024);
            char[] block = new char[1024];
            int count;
            while ((count = reader.Read(block, 0, block.Length)) > 0)
            {
                int keep = Math.Min(count, limit - text.Length);
                if (keep > 0) text.Append(block, 0, keep);
            }
            return text.ToString();
        }
    }
}
