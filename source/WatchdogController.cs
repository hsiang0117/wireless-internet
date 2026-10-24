using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WirelessInternet
{
    // Runs only inside the detached ConnectionWorker process. It never changes phone
    // switches, starts a phone VPN, restarts adbd, or stops the shared ADB server.
    public sealed class WatchdogController
    {
        const int HealthySweepMilliseconds = 5000;
        const int AdbTimeoutMilliseconds = 5000;
        const string ReverseName = "localabstract:gnirehtet";
        const string ReverseDestination = "tcp:31416";
        readonly RuntimeContext context;
        string sessionToken, controlPath, selfStartTicks;
        int selfId, timeoutSeconds = 30;
        long lastHealthyAt;

        sealed class SessionChangedException : Exception { }
        sealed class SessionBusyException : Exception { }

        public WatchdogController(string root) : this(new RuntimeContext(root)) { }

        public WatchdogController(RuntimeContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context;
        }

        public int Run(string token)
        {
            if (!Regex.IsMatch(token ?? String.Empty, "^[0-9a-f]{32}$")) return 2;
            sessionToken = token;
            controlPath = "/data/local/tmp/codex-wireless-internet/" + token + "/control.sh";
            using (Process self = Process.GetCurrentProcess())
            {
                selfId = self.Id;
                selfStartTicks = self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
            }
            timeoutSeconds = 30;
            lastHealthyAt = context.Timestamp;
            bool terminal = false;
            try
            {
                while (true)
                {
                    if (terminal || DeadlineReached())
                    {
                        terminal = true;
                        if (StopExpiredSession()) return 0;
                        context.Sleep(500);
                        continue;
                    }
                    long cycleStarted = context.Timestamp;
                    try
                    {
                        RuntimeState state = InSession(delegate(RuntimeState current) { return current; });
                        RequireOwnedRelay(state);
                        RequireSoleListener(state);
                        string gateway = GetPhoneGateway(state);
                        string serial = GetVerifiedSerial(state, gateway);
                        RequireReverse(serial);

                        RuntimeCommandResult vpn = Adb(new string[] { "-s", serial, "shell", "dumpsys", "connectivity" });
                        if (!vpn.Success || !PhoneVpnIsConnected(vpn.Text))
                            throw new IOException("The phone VPN is not connected; the monitor will not restart it.");
                        RequireOwnedRelay(state);
                        int[] connections = context.GetTcpPortOwners(31416, true);
                        if (!ContainsOwner(connections, state.RelayProcessId))
                            throw new IOException("The tracked relay has no established phone connection.");
                        if (DeadlineReached())
                        {
                            terminal = true;
                            throw new IOException("Protected heartbeat deadline reached during recovery.");
                        }

                        RuntimeCommandResult beat = SessionAdb(new string[] {
                            "-s", serial, "shell", "sh", controlPath, "beat", sessionToken });
                        if (beat.Success && String.Equals(beat.Text, "OK " + sessionToken, StringComparison.Ordinal))
                        {
                            // An answer that arrives after our own deadline cannot revive this monitor.
                            if (DeadlineReached())
                            {
                                terminal = true;
                                throw new IOException("Protected heartbeat acknowledgement arrived after expiry.");
                            }
                            InSession(delegate(RuntimeState latest) {
                                latest.MonitorHeartbeatQpcTicks = context.Timestamp.ToString(CultureInfo.InvariantCulture);
                                context.WriteState(latest);
                                return true;
                            });
                            lastHealthyAt = context.Timestamp;
                        }
                        else if (Regex.IsMatch(beat.Text ?? String.Empty,
                            "(?m)^REFUSED[ \\t]+" + Regex.Escape(sessionToken) + "\\b") ||
                            (beat.Success && !String.IsNullOrEmpty(beat.Text)))
                        {
                            // EXPIRED, CANCELLED, SUPERSEDED and any unexpected successful
                            // acknowledgement are terminal. Never arm or restart a phone guard here.
                            terminal = true;
                            continue;
                        }
                        else throw new IOException("The phone guard did not acknowledge the protected heartbeat.");
                    }
                    catch (SessionChangedException) { return 0; }
                    catch (SessionBusyException) { context.Sleep(500); continue; }
                    catch (Exception)
                    {
                        // No default log files or console output. Both expiry mechanisms remain
                        // independent of the GUI and a failed observation cannot renew either one.
                    }
                    if (terminal) continue;
                    double remaining = HealthySweepMilliseconds - context.ElapsedSeconds(cycleStarted) * 1000.0;
                    if (remaining > 0) context.Sleep((int)Math.Min(HealthySweepMilliseconds, Math.Ceiling(remaining)));
                }
            }
            catch (Exception)
            {
                // If the monitor itself fails, the already armed phone-local guard still expires.
                return 1;
            }
        }

        bool DeadlineReached()
        {
            return context.ElapsedSeconds(lastHealthyAt) >= timeoutSeconds;
        }

        T InSession<T>(Func<RuntimeState, T> action)
        {
            using (IDisposable held = context.TryEnterCoreMutex())
            {
                if (held == null) throw new SessionBusyException();
                return action(ReadCurrentState());
            }
        }

        RuntimeState ReadCurrentState()
        {
            RuntimeState state = context.ReadState();
            if (state == null || !String.Equals(state.ProtectionSessionId, sessionToken, StringComparison.Ordinal) ||
                state.WatchdogProcessId != selfId ||
                !String.Equals(state.WatchdogStartTimeUtcTicks, selfStartTicks, StringComparison.Ordinal) ||
                !String.Equals(state.RuntimeEngine, "CSharp", StringComparison.Ordinal) ||
                !SamePath(state.WatchdogPath, context.WorkerPath))
                throw new SessionChangedException();
            if (state.Transport != "NativeTls" || state.ProtectionTimeoutSeconds < 15 || state.ProtectionTimeoutSeconds > 120)
                throw new IOException("Invalid protection-session configuration.");
            if (!String.Equals(state.PhoneControlPath, controlPath, StringComparison.Ordinal) ||
                !SamePath(state.RelayPath, context.RelayPath))
                throw new IOException("Unexpected protection-session path.");
            long relayTicks;
            if (state.RelayProcessId <= 0 || !Int64.TryParse(state.RelayStartTimeUtcTicks,
                NumberStyles.None, CultureInfo.InvariantCulture, out relayTicks) || relayTicks <= 0 ||
                !Regex.IsMatch(state.PhoneIdentity ?? String.Empty, "^[A-Za-z0-9_-]{1,100}$"))
                throw new IOException("Invalid tracked relay or phone identity.");
            timeoutSeconds = state.ProtectionTimeoutSeconds;
            return state;
        }

        static bool SamePath(string first, string second)
        {
            if (String.IsNullOrWhiteSpace(first) || String.IsNullOrWhiteSpace(second)) return false;
            return String.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }

        void RequireOwnedRelay(RuntimeState state)
        {
            using (Process relay = context.GetOwnedRelay(state))
            {
                if (relay == null) throw new IOException("The tracked relay is not running.");
            }
        }

        void RequireSoleListener(RuntimeState state)
        {
            int[] owners = context.GetTcpPortOwners(31416, false);
            if (!ContainsOwner(owners, state.RelayProcessId)) throw new IOException("The tracked relay has no listener.");
            foreach (int owner in owners)
                if (owner != state.RelayProcessId) throw new IOException("Another process owns a relay-port listener.");
        }

        static bool ContainsOwner(int[] owners, int expected)
        {
            if (owners != null) foreach (int owner in owners) if (owner == expected) return true;
            return false;
        }

        string GetPhoneGateway(RuntimeState state)
        {
            Dictionary<string, object> selection = new Dictionary<string, object>();
            selection["WifiInterfaceIndex"] = state.WifiInterfaceIndex;
            selection["EthernetInterfaceIndex"] = state.EthernetInterfaceIndex;
            NetworkLink link = context.ResolveNetwork(selection, state, false);
            return link.PhoneGateway;
        }

        RuntimeCommandResult Adb(string[] arguments, int timeout = AdbTimeoutMilliseconds)
        {
            return context.Adb(arguments, true, timeout);
        }

        RuntimeCommandResult SessionAdb(string[] arguments, int timeout = AdbTimeoutMilliseconds)
        {
            return InSession(delegate(RuntimeState current) { return Adb(arguments, timeout); });
        }

        static bool ValidSerial(string serial, string gateway)
        {
            Match match = Regex.Match(serial ?? String.Empty, "^(?<ip>[0-9]{1,3}(?:[.][0-9]{1,3}){3}):(?<port>[0-9]{1,5})$");
            IPAddress address;
            int port;
            if (!match.Success || !IPAddress.TryParse(match.Groups["ip"].Value, out address) ||
                address.AddressFamily != AddressFamily.InterNetwork ||
                !Int32.TryParse(match.Groups["port"].Value, out port) || port < 1 || port > 65535) return false;
            byte first = address.GetAddressBytes()[0];
            if (first == 0 || first == 127 || first >= 224) return false;
            return String.IsNullOrEmpty(gateway) || String.Equals(address.ToString(), gateway, StringComparison.Ordinal);
        }

        string GetVerifiedSerial(RuntimeState state, string gateway)
        {
            string serial = state.WirelessSerial;
            if (ValidSerial(serial, gateway))
            {
                RuntimeCommandResult probe = Adb(new string[] { "-s", serial, "shell", "getprop", "ro.serialno" });
                if (probe.Success && String.Equals(probe.Text, state.PhoneIdentity, StringComparison.Ordinal)) return serial;
                if (probe.Success && !String.IsNullOrEmpty(probe.Text))
                    throw new IOException("The wireless endpoint belongs to a different phone.");
            }
            serial = FindNativeSerial(gateway, state.PhoneIdentity, serial);
            RuntimeCommandResult connected = SessionAdb(new string[] { "connect", serial });
            if (!connected.Success) throw new IOException("Native wireless-debugging reconnection failed.");
            RuntimeCommandResult identity = Adb(new string[] { "-s", serial, "shell", "getprop", "ro.serialno" });
            if (!identity.Success || !String.Equals(identity.Text, state.PhoneIdentity, StringComparison.Ordinal))
                throw new IOException("Could not verify the configured phone after reconnecting.");
            InSession(delegate(RuntimeState latest) {
                if (!String.Equals(latest.WirelessSerial, serial, StringComparison.Ordinal))
                {
                    latest.WirelessSerial = serial;
                    latest.ConnectEndpoint = serial;
                    context.WriteState(latest);
                }
                return true;
            });
            return serial;
        }

        string FindNativeSerial(string gateway, string identity, string fallback)
        {
            RuntimeCommandResult discovered = Adb(new string[] { "mdns", "services" });
            string pattern = "^adb-" + Regex.Escape(identity) + "-\\S+[ \\t]+_adb-tls-connect[.]_tcp[.]?[ \\t]+[0-9.]+:(?<port>[0-9]+)[ \\t]*\\r?$";
            HashSet<int> ports = new HashSet<int>();
            foreach (string line in discovered.Lines ?? new string[0])
            {
                Match service = Regex.Match(line, pattern);
                int port;
                if (service.Success && Int32.TryParse(service.Groups["port"].Value, out port) && port >= 1 && port <= 65535)
                    ports.Add(port);
            }
            if (discovered.Success && ports.Count == 1)
                foreach (int port in ports) return gateway + ":" + port.ToString(CultureInfo.InvariantCulture);
            if (ports.Count > 1) throw new IOException("Multiple native debugging ports were discovered for the tracked phone.");
            if (ValidSerial(fallback, gateway)) return fallback;
            throw new IOException("A unique native wireless-debugging endpoint could not be found.");
        }

        void RequireReverse(string serial)
        {
            RuntimeCommandResult reverse = Adb(new string[] { "-s", serial, "reverse", "--list" });
            if (!reverse.Success) throw new IOException("Could not read the protected reverse tunnel.");
            if (HasOwnReverse(reverse.Text)) return;
            RuntimeCommandResult restored = SessionAdb(new string[] {
                "-s", serial, "reverse", "--no-rebind", ReverseName, ReverseDestination });
            if (!restored.Success) throw new IOException("Could not restore this session's reverse tunnel without rebinding another mapping.");
            reverse = Adb(new string[] { "-s", serial, "reverse", "--list" });
            if (!reverse.Success || !HasOwnReverse(reverse.Text)) throw new IOException("The protected reverse tunnel was not restored.");
        }

        static bool HasOwnReverse(string text)
        {
            bool found = false;
            foreach (string line in Regex.Split(text ?? String.Empty, "\\r?\\n"))
            {
                Match mapping = Regex.Match(line, "^\\S+[ \\t]+localabstract:gnirehtet[ \\t]+(?<destination>\\S+)[ \\t]*$");
                if (!mapping.Success) continue;
                if (mapping.Groups["destination"].Value != ReverseDestination)
                    throw new IOException("Another destination already owns the phone's gnirehtet reverse name.");
                found = true;
            }
            return found;
        }

        static bool PhoneVpnIsConnected(string text)
        {
            return Regex.IsMatch(text ?? String.Empty,
                "(?m)^  NetworkAgentInfo\\{network\\{\\d+\\}[^\\r\\n]*ni\\{VPN CONNECTED extra: VPN:com[.]genymobile[.]gnirehtet\\}");
        }

        bool StopExpiredSession()
        {
            RuntimeState snapshot;
            try { snapshot = InSession(delegate(RuntimeState current) { return current; }); }
            catch (SessionBusyException) { return false; }
            catch (SessionChangedException) { return true; }
            catch (Exception) { return true; }

            // Best effort only: the independent phone guard also handles a powered-off PC.
            // Read-only identity checks may run outside the mutex; every mutation rechecks it.
            string serial = snapshot.WirelessSerial;
            if (ValidSerial(serial, null))
            {
                try
                {
                    RuntimeCommandResult identity = Adb(new string[] { "-s", serial, "shell", "getprop", "ro.serialno" }, 1500);
                    if (identity.Success && String.Equals(identity.Text, snapshot.PhoneIdentity, StringComparison.Ordinal))
                        SessionAdb(new string[] { "-s", serial, "shell", "sh", controlPath, "stop", sessionToken }, 3000);
                }
                catch (SessionBusyException) { return false; }
                catch (SessionChangedException) { return true; }
                catch (Exception) { }
            }
            IDisposable held = context.TryEnterCoreMutex();
            if (held == null) return false;
            using (held)
            {
                try
                {
                    RuntimeState latest = ReadCurrentState();
                    bool relayOwned = false;
                    try
                    {
                        using (Process relay = context.GetOwnedRelay(latest)) { relayOwned = relay != null; }
                    }
                    catch (Exception) { }
                    if (relayOwned)
                    {
                        latest = ReadCurrentState();
                        using (Process relay = context.GetOwnedRelay(latest, true))
                        {
                            if (relay != null)
                            {
                                if (!relay.HasExited) relay.Kill();
                                relay.WaitForExit(1000);
                            }
                        }
                    }
                    ReadCurrentState();
                    context.DeleteStateIfToken(sessionToken);
                    return true;
                }
                catch (SessionChangedException) { return true; }
                catch (Exception)
                {
                    // Retain an unreadable or uncleanable state for manual recovery. Never
                    // broaden cleanup to another process, another token, or the shared server.
                    return true;
                }
            }
        }
    }
}
