using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WirelessInternet
{
    // Native translation of the release 1.3.2 Start/Stop protocol. The GUI invokes
    // this through a short-lived worker; the relay and protection worker are independent.
    public sealed class ConnectionController
    {
        const string Package = "com.genymobile.gnirehtet";
        readonly RuntimeContext context;

        public ConnectionController(string root) : this(new RuntimeContext(root)) { }
        public ConnectionController(RuntimeContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context;
        }

        public int Start(Action<string> output)
        {
            IDisposable operation = null;
            RuntimeState state = null, newRelay = null;
            bool createdRelay = false, stateSaved = false, newGuard = false;
            string readyPath = null;
            try
            {
                operation = context.EnterCoreMutex(6000);
                Dictionary<string, object> configuration = ReadConfiguration();
                string identity = Get(configuration, "PhoneIdentity");
                int timeout = ProtectionTimeout(configuration);
                RuntimeState oldState = context.ReadState();
                ValidateTransport(oldState);
                if (oldState != null && oldState.RuntimeEngine != "CSharp")
                    throw new IOException("A legacy sharing session is recorded. Disconnect that session before starting the C# runtime; its monitor will not be replaced automatically.");
                if (oldState != null && oldState.PhoneIdentity != identity)
                    throw new IOException("The recorded session belongs to a different phone. Stop that session before changing the binding.");
                NetworkLink network = context.ResolveNetwork(configuration, oldState, false);
                string serial = FindNativeWirelessSerial(network.PhoneGateway, identity, Get(configuration, "ConnectEndpoint"));
                if (context.WifiHasIPv6Default(network.WifiInterfaceIndex))
                    Report(output, "The hotspot supplies a PC IPv6 exit. Other PC applications may use its upstream network; IPv4 Ethernet remains preferred.");
                Report(output, context.Adb(new string[] { "connect", serial }).Text);
                VerifyPhone(serial, identity, output);
                VerifyRestrictionsOff(serial, output);

                int relayId = OwnedRelayId(oldState);
                int[] listeners = Owners(false);
                if (listeners.Length > 0 && (relayId == 0 || listeners.Any(delegate(int pid) { return pid != relayId; })))
                    throw new IOException("Port 31416 is occupied by an untracked relay or application. No unrelated process was stopped.");
                RuntimeState logRecord = oldState;
                if (relayId == 0)
                {
                    Directory.CreateDirectory(Path.Combine(context.Root, "state"));
                    RequireFile(context.RelayHostPath);
                    string relayToken = Guid.NewGuid().ToString("N");
                    readyPath = Path.Combine(context.Root, "state", "relay-ready-" + relayToken + ".json");
                    using (Process starter = Process.GetCurrentProcess())
                    using (Process host = context.StartDetached(context.RelayHostPath,
                        new string[] { relayToken, starter.Id.ToString(CultureInfo.InvariantCulture), starter.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) }))
                    {
                        if (host == null) throw new IOException("The real-time relay host could not be created.");
                    }
                    for (int attempt = 0; attempt < 60 && !File.Exists(readyPath); attempt++) context.Sleep(100);
                    if (!File.Exists(readyPath))
                        throw new IOException("The real-time relay host did not become ready. Its unaccepted child will clean up when the starter exits or its deadline expires.");
                    logRecord = ReadRelayReady(readyPath);
                    using (Process owned = context.GetOwnedRelay(logRecord))
                    {
                        if (owned == null) throw new IOException("The newly started relay exited before ownership could be recorded.");
                        relayId = owned.Id;
                        createdRelay = true;
                        newRelay = logRecord;
                    }
                }

                using (Process owned = context.GetOwnedRelay(logRecord))
                {
                    if (owned == null) throw new IOException("The tracked relay exited during startup.");
                    state = new RuntimeState { Transport = "NativeTls", RuntimeEngine = "CSharp", PhoneIdentity = identity,
                        RelayProcessId = owned.Id, RelayStartTimeUtcTicks = owned.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                        RelayPath = context.RelayPath, WatchdogPath = context.WorkerPath };
                }
                context.ApplyNetwork(state, network);
                state.WirelessSerial = serial;
                state.ConnectEndpoint = serial;
                CopyLogRecord(state, logRecord);

                bool reuse = false;
                using (Process monitor = context.GetOwnedMonitor(oldState))
                {
                    if (context.ProtectionValid(oldState) && monitor != null)
                    {
                        RuntimeCommandResult status = context.Guard(WithSerial(oldState, serial), "status", true);
                        reuse = status.Success && GuardActive(status.Text, oldState.ProtectionSessionId) && context.MonitorHeartbeatFresh(oldState);
                    }
                }
                if (reuse)
                {
                    CopyProtection(state, oldState);
                }
                else
                {
                    RequireFile(context.WorkerPath);
                    StopOwnedMonitor(oldState);
                    string token = Guid.NewGuid().ToString("N");
                    string remoteDirectory = "/data/local/tmp/codex-wireless-internet/" + token;
                    state.ProtectionSessionId = token;
                    state.ProtectionTimeoutSeconds = timeout;
                    state.PhoneControlPath = remoteDirectory + "/control.sh";
                    context.Adb(new string[] { "-s", serial, "shell", "mkdir", "-p", remoteDirectory });
                    using (PhoneGuardLease phoneGuard = context.ExtractPhoneGuard())
                        context.Adb(new string[] { "-s", serial, "push", phoneGuard.Path, state.PhoneControlPath });
                    newGuard = true;
                    RuntimeCommandResult armed = context.Guard(state, "arm");
                    if (armed.Text != "ARMED " + token) throw new IOException("The independent phone disconnect guard did not start.");
                }

                context.WriteState(state);
                stateSaved = true;
                if (readyPath != null)
                {
                    File.WriteAllText(readyPath + ".accepted", relayId.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
                    TryDelete(readyPath);
                }
                bool listening = false;
                for (int attempt = 0; attempt < 15; attempt++)
                {
                    if (Owners(false).Contains(relayId)) { listening = true; break; }
                    context.Sleep(200);
                }
                if (!listening) throw new IOException("The relay did not open its listening port. Retry Start or inspect the live console.");
                context.Gnirehtet(new string[] { "tunnel", serial });
                if (!context.PhoneVpnConnected(serial)) Report(output, context.Gnirehtet(new string[] { "start", serial }).Text);
                bool connected = false;
                for (int attempt = 0; attempt < 15; attempt++)
                {
                    if (Owners(true).Contains(relayId)) { connected = true; break; }
                    context.Sleep(300);
                }
                RuntimeCommandResult reverse = context.Adb(new string[] { "-s", serial, "reverse", "--list" });
                if (!connected || !ReverseReady(reverse.Text) || !context.PhoneVpnConnected(serial))
                    throw new IOException("The wireless tunnel or phone VPN is not ready. Approve the phone VPN request if shown.");
                RuntimeCommandResult beat = context.Guard(state, "beat");
                if (beat.Text != "OK " + state.ProtectionSessionId) throw new IOException("The phone guard refused the startup heartbeat.");
                if (!reuse)
                {
                    using (Process monitor = context.StartDetached(context.WorkerPath,
                        new string[] { "--monitor", state.ProtectionSessionId, "--root", context.Root }))
                    {
                        if (monitor == null) throw new IOException("The protection worker could not be created.");
                        state.WatchdogProcessId = monitor.Id;
                        state.WatchdogStartTimeUtcTicks = monitor.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                        state.WatchdogPath = context.WorkerPath;
                    }
                    context.WriteState(state);
                }
                using (Process monitor = context.GetOwnedMonitor(state))
                    if (monitor == null) throw new IOException("The protection monitor did not remain running.");

                // The monitor must acquire this same mutex before acknowledging a heartbeat.
                operation.Dispose(); operation = null;
                bool acknowledged = false;
                for (int attempt = 0; attempt < 60; attempt++)
                {
                    using (IDisposable read = context.TryEnterCoreMutex())
                    {
                        if (read != null)
                        {
                            RuntimeState latest = context.ReadState();
                            if (latest == null) throw new IOException("The sharing session ended during protection startup.");
                            if (latest.ProtectionSessionId != state.ProtectionSessionId)
                                throw new IOException("Another sharing session replaced this startup.");
                            using (Process monitor = context.GetOwnedMonitor(latest))
                                if (monitor == null) throw new IOException("The monitor exited before acknowledging its first heartbeat.");
                            if (context.MonitorHeartbeatFresh(latest)) { state = latest; acknowledged = true; }
                        }
                    }
                    if (acknowledged) break;
                    context.Sleep(200);
                }
                if (!acknowledged) throw new IOException("The protection monitor did not acknowledge a healthy heartbeat in time.");
                Report(output, "Wireless ADB tunnel and phone VPN are ready. The relay runs independently in the background.");
                Report(output, "Keep phone Wi-Fi, hotspot and system Wireless debugging ON. Mobile data and both VPN restrictions may remain OFF.");
                Report(output, "Click Disconnect before turning OFF Wireless debugging and the hotspot.");
                Report(output, "Automatic disconnect protection is active: about " + state.ProtectionTimeoutSeconds + " seconds without a healthy heartbeat ends this sharing session.");
                return 0;
            }
            catch (Exception error)
            {
                Report(output, "ERROR: " + error.Message);
                if (newGuard && context.ProtectionValid(state))
                    try { context.Guard(state, "stop", true); } catch { }
                if (createdRelay && !stateSaved && newRelay != null)
                {
                    try
                    {
                        using (Process relay = context.GetOwnedRelay(newRelay, true)) KillPinned(relay, 3000);
                        Report(output, "The newly created, unsaved relay was cleaned up.");
                    }
                    catch (Exception cleanup) { Report(output, "Relay cleanup failed: " + cleanup.Message); }
                }
                else if (createdRelay && stateSaved)
                    Report(output, "The recorded relay is retained for recovery. Keep both phone VPN restrictions OFF and use Disconnect.");
                Report(output, "No VPN switches or network routes were changed.");
                return 1;
            }
            finally { if (operation != null) operation.Dispose(); }
        }

        public int Stop(Action<string> output)
        {
            IDisposable operation = null;
            try
            {
                operation = context.EnterCoreMutex(6000);
                Dictionary<string, object> configuration = ReadConfiguration();
                RuntimeState state = context.ReadState();
                ValidateTransport(state);
                string serial = ExistingOrDiscoveredSerial(configuration, state);
                VerifyPhone(serial, Get(configuration, "PhoneIdentity"), output);
                VerifyRestrictionsOff(serial, output);
                VerifyRuntimeRestrictionsOff(serial);
                // Validate ownership before stopping the phone; a reused PID must never be targeted.
                int relayId = OwnedRelayId(state);
                Report(output, context.Gnirehtet(new string[] { "stop", serial }).Text);
                bool stopped = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    if (!context.PhoneVpnConnected(serial)) { stopped = true; break; }
                    context.Sleep(300);
                }
                if (!stopped) throw new IOException("Phone VPN is still connected. Relay left running; retry Disconnect.");
                if (context.ProtectionValid(state)) context.Guard(WithSerial(state, serial), "cancel");
                StopOwnedMonitor(state);
                RuntimeCommandResult reverse = context.Adb(new string[] { "-s", serial, "reverse", "--list" });
                if (ReverseReady(reverse.Text)) context.Adb(new string[] { "-s", serial, "reverse", "--remove", "localabstract:gnirehtet" });
                if (relayId != 0)
                {
                    if (Owners(false).Any(delegate(int pid) { return pid != relayId; }))
                        throw new IOException("Relay port owner changed; no unrelated process was stopped.");
                    using (Process owned = context.GetOwnedRelay(state, true)) KillPinned(owned, 5000);
                }
                if (state != null) context.DeleteStateIfToken(state.ProtectionSessionId);
                RuntimeCommandResult disconnect = context.Adb(new string[] { "disconnect", serial }, true);
                if (!disconnect.Success) Report(output, "ADB disconnect warning: " + disconnect.Text);
                Report(output, "Wireless sharing stopped. You may manually turn OFF phone Wireless debugging, then turn OFF its hotspot.");
                return 0;
            }
            catch (Exception error)
            {
                Report(output, "ERROR: " + error.Message);
                Report(output, "No VPN switches or network routes were changed. Unverified processes were left untouched.");
                return 1;
            }
            finally { if (operation != null) operation.Dispose(); }
        }

        public int Check(Action<string> output)
        {
            try
            {
                using (IDisposable operation = context.EnterCoreMutex(6000))
                {
                    Dictionary<string, object> configuration = ReadConfiguration();
                    RuntimeState state = context.ReadState();
                    ValidateTransport(state);
                    string serial = ExistingOrDiscoveredSerial(configuration, state);
                    VerifyPhone(serial, Get(configuration, "PhoneIdentity"), output);
                    int relayId = OwnedRelayId(state);
                    if (relayId == 0) throw new IOException("No tracked wireless relay is running.");
                    RuntimeCommandResult reverse = context.Adb(new string[] { "-s", serial, "reverse", "--list" });
                    if (!Owners(true).Contains(relayId) || !ReverseReady(reverse.Text) || !context.PhoneVpnConnected(serial))
                        throw new IOException("The wireless ADB tunnel, relay connection or phone VPN is not ready.");
                    using (Process monitor = context.GetOwnedMonitor(state))
                        if (!context.ProtectionValid(state) || monitor == null || !context.MonitorHeartbeatFresh(state))
                            throw new IOException("The disconnect monitor does not have a recent acknowledged heartbeat.");
                    RuntimeCommandResult guard = context.Guard(WithSerial(state, serial), "status");
                    if (!GuardActive(guard.Text, state.ProtectionSessionId)) throw new IOException("The phone disconnect guard is not active.");
                    Report(output, "Tracked relay PID: " + relayId + "; phone VPN connected; protection monitor PID: " + state.WatchdogProcessId + ".");
                    Report(output, "Read-only check completed. No VPN switches, relay processes or routes were changed.");
                    return 0;
                }
            }
            catch (Exception error) { Report(output, "ERROR: " + error.Message); return 1; }
        }

        Dictionary<string, object> ReadConfiguration()
        {
            RequireFile(context.AdbPath); RequireFile(context.RelayPath);
            Dictionary<string, object> configuration = context.ReadConfig();
            if (Get(configuration, "Transport") != "NativeTls") throw new IOException("This runtime requires the NativeTls configuration.");
            if (!Regex.IsMatch(Get(configuration, "PhoneIdentity"), "^[A-Za-z0-9_-]{1,100}$"))
                throw new IOException("Phone setup is incomplete or invalid. Open phone binding and verify your own Android phone first.");
            ProtectionTimeout(configuration);
            return configuration;
        }

        string ExistingOrDiscoveredSerial(Dictionary<string, object> configuration, RuntimeState state)
        {
            if (state != null) return state.WirelessSerial;
            NetworkLink link = context.ResolveNetwork(configuration, null, true);
            return FindNativeWirelessSerial(link.PhoneGateway, Get(configuration, "PhoneIdentity"), Get(configuration, "ConnectEndpoint"));
        }

        string FindNativeWirelessSerial(string gateway, string identity, string fallback)
        {
            string pattern = "^adb-" + Regex.Escape(identity) + @"-\S+[ \t]+_adb-tls-connect\._tcp\.?[ \t]+[0-9.]+:(?<port>\d+)[ \t]*$";
            HashSet<int> ports = new HashSet<int>();
            for (int attempt = 0; attempt < 6; attempt++)
            {
                RuntimeCommandResult result = context.Adb(new string[] { "mdns", "services" }, true);
                if (!result.Success) break;
                ports.Clear();
                foreach (string line in result.Lines ?? new string[0])
                {
                    Match found = Regex.Match(line, pattern);
                    int port;
                    if (found.Success && Int32.TryParse(found.Groups["port"].Value, out port) && port >= 1 && port <= 65535) ports.Add(port);
                }
                if (ports.Count == 1) return gateway + ":" + ports.First().ToString(CultureInfo.InvariantCulture);
                if (attempt < 5) context.Sleep(400);
            }
            if (ports.Count > 1) throw new IOException("Multiple native debugging ports were discovered for this phone. Wait a few seconds and retry.");
            if (!String.IsNullOrEmpty(fallback))
            {
                Match endpoint = Regex.Match(fallback, @"^(?<ip>(?:[0-9]{1,3}\.){3}[0-9]{1,3}):(?<port>[0-9]{1,5})$");
                int port;
                System.Net.IPAddress address;
                if (!endpoint.Success || !System.Net.IPAddress.TryParse(endpoint.Groups["ip"].Value, out address) ||
                    address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || address.ToString() != gateway ||
                    !Int32.TryParse(endpoint.Groups["port"].Value, out port) || port < 1 || port > 65535)
                    throw new IOException("The saved native connection endpoint is invalid or belongs to another hotspot gateway. Update phone binding.");
                return gateway + ":" + port.ToString(CultureInfo.InvariantCulture);
            }
            throw new IOException("Native wireless debugging was not discovered. Keep phone Wi-Fi, hotspot and Wireless debugging ON, or update its main-page connection port in phone binding.");
        }

        void VerifyPhone(string serial, string identity, Action<string> output)
        {
            string actual = context.Adb(new string[] { "-s", serial, "shell", "getprop", "ro.serialno" }).Text;
            if (String.IsNullOrEmpty(actual) || actual != identity) throw new IOException("The wireless endpoint does not match the configured phone.");
            if (context.Adb(new string[] { "-s", serial, "shell", "am", "get-current-user" }).Text != "0")
                throw new IOException("This runtime supports only the phone owner profile (user 0).");
            if (context.Adb(new string[] { "-s", serial, "shell", "settings", "get", "global", "adb_wifi_enabled" }).Text != "1")
                throw new IOException("The phone system Wireless debugging switch is OFF. Turn it ON manually; no settings were changed.");
            Report(output, "Native TLS wireless debugging verified: " + serial + "; configured phone identity verified; system switch ON.");
        }

        void VerifyRestrictionsOff(string serial, Action<string> output)
        {
            string alwaysOn = context.Adb(new string[] { "-s", serial, "shell", "settings", "get", "secure", "always_on_vpn_app" }).Text;
            string lockdown = context.Adb(new string[] { "-s", serial, "shell", "settings", "get", "secure", "always_on_vpn_lockdown" }).Text;
            Report(output, "Always-on VPN: " + alwaysOn + "; block non-VPN connections: " + lockdown);
            if (alwaysOn == Package || (lockdown != "0" && lockdown != "null"))
                throw new IOException("Manually turn OFF both gnirehtet VPN restrictions before Start or Disconnect. The tracked relay was left running.");
        }

        void VerifyRuntimeRestrictionsOff(string serial)
        {
            string dump = context.Adb(new string[] { "-s", serial, "shell", "dumpsys", "vpn_management" }).Text;
            Match owner = Regex.Match(dump, @"(?ms)^  0:[^\r\n]*\r?\n(?<body>.*?)(?=^  \d+:|\z)");
            if (!owner.Success) throw new IOException("Cannot verify owner-profile VPN restrictions. Relay left running.");
            Match latest = null;
            foreach (Match mode in Regex.Matches(owner.Groups["body"].Value, @"(?m)^[ \t]*(\d{4}-\d\d-\d\dT\S+) - \[LockdownAlwaysOn\] Mode changed: lockdown=(true|false) alwaysOn=(true|false)"))
                if (latest == null || String.CompareOrdinal(mode.Groups[1].Value, latest.Groups[1].Value) > 0) latest = mode;
            if (latest == null || latest.Groups[2].Value != "false" || latest.Groups[3].Value != "false")
                throw new IOException("Cannot verify runtime VPN restrictions are OFF. Relay left running; check the phone switches.");
        }

        int OwnedRelayId(RuntimeState state)
        {
            using (Process relay = context.GetOwnedRelay(state)) return relay == null ? 0 : relay.Id;
        }

        void StopOwnedMonitor(RuntimeState state)
        {
            using (Process monitor = context.GetOwnedMonitor(state, true)) KillPinned(monitor, 3000);
        }

        static void KillPinned(Process process, int timeout)
        {
            if (process == null) return;
            if (process.Id == Process.GetCurrentProcess().Id) throw new IOException("Refusing to stop the current controller process.");
            if (!process.HasExited) process.Kill();
            if (!process.WaitForExit(timeout)) throw new IOException("The owned process did not exit in time; its session record was retained.");
        }

        int[] Owners(bool established) { return context.GetTcpPortOwners(31416, established) ?? new int[0]; }
        static bool ReverseReady(string text) { return Regex.IsMatch(text ?? "", @"localabstract:gnirehtet\s+tcp:31416"); }
        static bool GuardActive(string text, string token) { return Regex.IsMatch(text ?? "", "^ACTIVE " + Regex.Escape(token ?? "") + " "); }
        static void ValidateTransport(RuntimeState state)
        {
            if (state != null && state.Transport != "NativeTls") throw new IOException("An older non-native sharing session is recorded. Stop it before switching modes.");
        }
        static int ProtectionTimeout(Dictionary<string, object> configuration)
        {
            int timeout = 30;
            string raw = Get(configuration, "DisconnectTimeoutSeconds");
            if (!String.IsNullOrEmpty(raw) && !Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout))
                throw new IOException("Disconnect timeout is invalid.");
            if (timeout == 0) timeout = 30;
            if (timeout < 15 || timeout > 120) throw new IOException("Disconnect timeout must be between 15 and 120 seconds.");
            return timeout;
        }
        static string Get(IDictionary<string, object> values, string name)
        {
            object value;
            return values != null && values.TryGetValue(name, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
        static void RequireFile(string path) { if (!File.Exists(path)) throw new IOException("Required file missing: " + path); }
        static void TryDelete(string path) { try { File.Delete(path); } catch { } }
        static void Report(Action<string> output, string message) { if (output != null && !String.IsNullOrWhiteSpace(message)) try { output(message); } catch { } }

        static RuntimeState ReadRelayReady(string path)
        {
            RuntimeState value = new JavaScriptSerializer().Deserialize<RuntimeState>(File.ReadAllText(path, Encoding.UTF8));
            if (value == null) throw new IOException("The relay ownership record is invalid.");
            return value;
        }
        static RuntimeState WithSerial(RuntimeState source, string serial)
        {
            if (source == null) return null;
            // The state is a local snapshot under the operation mutex, never a shared monitor object.
            source.WirelessSerial = serial;
            return source;
        }
        static void CopyLogRecord(RuntimeState target, RuntimeState source)
        {
            if (source == null) return;
            target.RelayLogPipe = source.RelayLogPipe; target.LogMode = source.LogMode;
            target.RelayHostProcessId = source.RelayHostProcessId; target.RelayHostStartTimeUtcTicks = source.RelayHostStartTimeUtcTicks;
        }
        static void CopyProtection(RuntimeState target, RuntimeState source)
        {
            target.ProtectionSessionId = source.ProtectionSessionId; target.ProtectionTimeoutSeconds = source.ProtectionTimeoutSeconds;
            target.PhoneControlPath = source.PhoneControlPath; target.WatchdogProcessId = source.WatchdogProcessId;
            target.WatchdogStartTimeUtcTicks = source.WatchdogStartTimeUtcTicks; target.MonitorHeartbeatQpcTicks = source.MonitorHeartbeatQpcTicks;
            target.WatchdogPath = source.WatchdogPath;
        }
    }
}
