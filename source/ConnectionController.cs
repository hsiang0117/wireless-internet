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
    // Staged orchestration of the existing Start/Stop protocol. The GUI invokes
    // a short-lived worker; the relay and protection worker remain independent.
    public sealed class ConnectionController
    {
        readonly RuntimeContext context;
        readonly AndroidDeviceClient devices;
        readonly Action<ConnectionStage> stageChanged;

        public ConnectionController(string root) : this(new RuntimeContext(root)) { }
        public ConnectionController(RuntimeContext context) : this(context, null) { }
        public ConnectionController(RuntimeContext context, Action<ConnectionStage> stageChanged)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context;
            this.stageChanged = stageChanged;
            devices = new AndroidDeviceClient(context);
        }

        public ConnectionOperationResult LastResult { get; private set; }

        public int Start(Action<string> output) { return StartWithResult(output).ExitCode; }

        public ConnectionOperationResult StartWithResult(Action<string> output)
        {
            IDisposable operation = null;
            ConnectionStartAttempt attempt = new ConnectionStartAttempt();
            ConnectionOperationTrace trace = new ConnectionOperationTrace(ConnectionOperationKind.Start, output, stageChanged);
            try
            {
                trace.Move(ConnectionStage.AcquireOperation);
                operation = context.EnterCoreMutex(6000);
                trace.Move(ConnectionStage.ValidateConfiguration);
                Dictionary<string, object> configuration = ReadConfiguration();
                string identity = Get(configuration, "PhoneIdentity");
                int timeout = ProtectionTimeout(configuration);
                RuntimeState oldState = context.ReadState();
                ValidateTransport(oldState);
                if (oldState != null && oldState.RuntimeEngine != "CSharp")
                    throw new IOException("A legacy sharing session is recorded. Disconnect that session before starting the C# runtime; its monitor will not be replaced automatically.");
                RequirePcOnlySession(oldState);
                if (oldState != null && oldState.PhoneIdentity != identity)
                    throw new IOException("The recorded session belongs to a different phone. Stop that session before changing the binding.");

                trace.Move(ConnectionStage.ResolveDevice);
                NetworkLink network = context.ResolveNetwork(configuration, oldState, false);
                string serial = devices.DiscoverNativeSerial(network.PhoneGateway, identity, Get(configuration, "ConnectEndpoint"));
                if (context.WifiHasIPv6Default(network.WifiInterfaceIndex))
                    Report(output, "The hotspot supplies a PC IPv6 exit. Other PC applications may use its upstream network; IPv4 Ethernet remains preferred.");
                trace.Move(ConnectionStage.VerifyDevice);
                Report(output, context.Adb(new string[] { "connect", serial }).Text);
                devices.VerifyPhone(serial, identity, output);
                devices.VerifyRestrictionsOff(serial, output);

                trace.Move(ConnectionStage.AcquireRelay);
                RuntimeState logRecord = PrepareRelay(oldState, attempt);
                attempt.State = CreateStartupState(logRecord, network, serial, identity);
                trace.Move(ConnectionStage.PrepareProtection);
                PrepareProtection(oldState, timeout, attempt);
                trace.Move(ConnectionStage.CommitSession);
                CommitStartupSession(attempt);
                trace.Move(ConnectionStage.EstablishTunnel);
                EstablishTunnel(attempt, serial, output);
                trace.Move(ConnectionStage.StartMonitor);
                StartProtectionMonitor(attempt);

                // Preserve the existing transaction boundary: the monitor must acquire
                // this same mutex before recording its first healthy PC observation.
                trace.Move(ConnectionStage.ReleaseOperation);
                IDisposable completedOperation = operation; operation = null;
                completedOperation.Dispose();
                trace.Move(ConnectionStage.AwaitProtection);
                AwaitFirstProtection(attempt);
                Report(output, "Wireless ADB tunnel and phone VPN are ready. The relay runs independently in the background.");
                Report(output, "Keep phone Wi-Fi, hotspot and system Wireless debugging ON. Mobile data and both VPN restrictions may remain OFF.");
                Report(output, "Click Disconnect before turning OFF Wireless debugging and the hotspot.");
                Report(output, "PC link monitoring is active: about " + attempt.State.ProtectionTimeoutSeconds + " seconds without a healthy connection ends the PC sharing session. After a disconnection or PC shutdown, manually close the phone VPN if it remains connected.");
                return Finish(trace, 0, "Wireless sharing started.");
            }
            catch (Exception error)
            {
                Report(output, "失败阶段 · " + ConnectionOperationTrace.Describe(trace.Current));
                Report(output, "ERROR: " + error.Message);
                CleanupFailedStart(attempt, output, operation != null);
                Report(output, "No VPN switches or network routes were changed.");
                return Finish(trace, 1, error.Message);
            }
            finally { if (operation != null) operation.Dispose(); }
        }

        RuntimeState PrepareRelay(RuntimeState oldState, ConnectionStartAttempt attempt)
        {
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
                attempt.ReadyPath = Path.Combine(context.Root, "state", "relay-ready-" + relayToken + ".json");
                using (Process starter = Process.GetCurrentProcess())
                using (Process host = context.StartDetached(context.RelayHostPath,
                    new string[] { relayToken, starter.Id.ToString(CultureInfo.InvariantCulture), starter.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) }))
                {
                    if (host == null) throw new IOException("The real-time relay host could not be created.");
                }
                for (int check = 0; check < 60 && !File.Exists(attempt.ReadyPath); check++) context.Sleep(100);
                if (!File.Exists(attempt.ReadyPath))
                    throw new IOException("The real-time relay host did not become ready. Its unaccepted child will clean up when the starter exits or its deadline expires.");
                logRecord = ReadRelayReady(attempt.ReadyPath);
                using (Process owned = context.GetOwnedRelay(logRecord))
                {
                    if (owned == null) throw new IOException("The newly started relay exited before ownership could be recorded.");
                    attempt.CreatedRelay = true;
                    attempt.NewRelay = logRecord;
                }
            }
            return logRecord;
        }

        RuntimeState CreateStartupState(RuntimeState logRecord, NetworkLink network, string serial, string identity)
        {
            RuntimeState state;
            using (Process owned = context.GetOwnedRelay(logRecord))
            {
                if (owned == null) throw new IOException("The tracked relay exited during startup.");
                state = new RuntimeState { Transport = "NativeTls", RuntimeEngine = "CSharp", ProtectionMode = "PcOnly", PhoneIdentity = identity,
                    RelayProcessId = owned.Id, RelayStartTimeUtcTicks = owned.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
                    RelayPath = context.RelayPath, WatchdogPath = context.WorkerPath };
            }
            context.ApplyNetwork(state, network);
            state.WirelessSerial = serial; state.ConnectEndpoint = serial;
            CopyLogRecord(state, logRecord);
            return state;
        }

        void PrepareProtection(RuntimeState oldState, int timeout, ConnectionStartAttempt attempt)
        {
            using (Process monitor = context.GetOwnedMonitor(oldState))
            {
                attempt.ReuseProtection = oldState != null && oldState.ProtectionMode == "PcOnly" &&
                    context.ProtectionValid(oldState) && monitor != null && context.MonitorHeartbeatFresh(oldState);
            }
            if (attempt.ReuseProtection) { CopyProtection(attempt.State, oldState); return; }
            RequireFile(context.WorkerPath);
            StopOwnedMonitor(oldState);
            string token = Guid.NewGuid().ToString("N");
            attempt.State.ProtectionSessionId = token; attempt.State.ProtectionTimeoutSeconds = timeout;
            attempt.State.ProtectionMode = "PcOnly";
            attempt.State.PhoneControlPath = null;
        }

        void CommitStartupSession(ConnectionStartAttempt attempt)
        {
            context.WriteState(attempt.State);
            attempt.StateSaved = true;
            if (attempt.ReadyPath != null)
            {
                File.WriteAllText(attempt.ReadyPath + ".accepted", attempt.State.RelayProcessId.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
                TryDelete(attempt.ReadyPath);
            }
        }

        void EstablishTunnel(ConnectionStartAttempt attempt, string serial, Action<string> output)
        {
            RuntimeState state = attempt.State;
            bool listening = false;
            for (int check = 0; check < 15; check++)
            {
                if (Owners(false).Contains(state.RelayProcessId)) { listening = true; break; }
                context.Sleep(200);
            }
            if (!listening) throw new IOException("The relay did not open its listening port. Retry Start or inspect the live console.");
            context.Gnirehtet(new string[] { "tunnel", serial });
            if (!context.PhoneVpnConnected(serial))
            {
                // A failed command can still have reached Android; scope any rollback
                // to a VPN start attempted by this operation, never a reused VPN.
                attempt.PhoneVpnStartAttempted = true;
                Report(output, context.Gnirehtet(new string[] { "start", serial }).Text);
            }
            bool connected = false;
            for (int check = 0; check < 15; check++)
            {
                if (Owners(true).Contains(state.RelayProcessId)) { connected = true; break; }
                context.Sleep(300);
            }
            RuntimeCommandResult reverse = context.Adb(new string[] { "-s", serial, "reverse", "--list" });
            if (!connected || !AndroidProtocol.ReverseReady(reverse.Text) || !context.PhoneVpnConnected(serial))
                throw new IOException("The wireless tunnel or phone VPN is not ready. Approve the phone VPN request if shown.");
        }

        void StartProtectionMonitor(ConnectionStartAttempt attempt)
        {
            RuntimeState state = attempt.State;
            if (!attempt.ReuseProtection)
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
        }

        void AwaitFirstProtection(ConnectionStartAttempt attempt)
        {
            bool acknowledged = false;
            for (int check = 0; check < 60; check++)
            {
                using (IDisposable read = context.TryEnterCoreMutex())
                {
                    if (read != null)
                    {
                        RuntimeState latest = context.ReadState();
                        if (latest == null) throw new IOException("The sharing session ended during protection startup.");
                        if (latest.ProtectionSessionId != attempt.State.ProtectionSessionId)
                            throw new IOException("Another sharing session replaced this startup.");
                        RequirePcOnlySession(latest);
                        using (Process monitor = context.GetOwnedMonitor(latest))
                            if (monitor == null) throw new IOException("The PC monitor exited before confirming the connection.");
                        if (context.MonitorHeartbeatFresh(latest)) { attempt.State = latest; acknowledged = true; }
                    }
                }
                if (acknowledged) break;
                context.Sleep(200);
            }
            if (!acknowledged) throw new IOException("The PC monitor did not confirm a healthy connection in time.");
        }

        void CleanupFailedStart(ConnectionStartAttempt attempt, Action<string> output, bool operationHeld)
        {
            if (attempt.PhoneVpnStartAttempted && attempt.StateSaved)
            {
                try
                {
                    if (operationHeld) StopAttemptedPhoneVpn(attempt, output);
                    else
                    {
                        // Another Start can reuse the same session token after this
                        // lease ends. An old failure must never stop that newer VPN.
                        Report(output, "The startup operation lock was released; the recorded session and phone VPN are retained. Use Disconnect or close the phone VPN manually if needed.");
                    }
                }
                catch (Exception cleanup)
                { Report(output, "Phone VPN cleanup was not confirmed: " + cleanup.Message + " Close it manually if it remains connected."); }
            }
            if (attempt.CreatedRelay && !attempt.StateSaved && attempt.NewRelay != null)
            {
                try
                {
                    using (Process relay = context.GetOwnedRelay(attempt.NewRelay, true)) OwnedProcessManager.TerminatePinned(relay, 3000);
                    Report(output, "The newly created, unsaved relay was cleaned up.");
                }
                catch (Exception cleanup) { Report(output, "Relay cleanup failed: " + cleanup.Message); }
            }
            else if (attempt.CreatedRelay && attempt.StateSaved)
                Report(output, "The recorded relay is retained for recovery. Keep both phone VPN restrictions OFF and use Disconnect.");
        }

        void StopAttemptedPhoneVpn(ConnectionStartAttempt attempt, Action<string> output)
        {
            RuntimeState latest = context.ReadState();
            if (latest == null || latest.ProtectionSessionId != attempt.State.ProtectionSessionId ||
                latest.ProtectionMode != "PcOnly" || latest.PhoneIdentity != attempt.State.PhoneIdentity ||
                !context.ProtectionValid(latest))
            {
                Report(output, "The sharing session changed; startup cleanup left the phone VPN untouched.");
                return;
            }
            using (Process relay = context.GetOwnedRelay(latest))
                if (relay == null) throw new IOException("The recorded relay ownership could not be confirmed.");
            devices.VerifyPhone(latest.WirelessSerial, latest.PhoneIdentity, output);
            devices.VerifyRestrictionsOff(latest.WirelessSerial, output);
            devices.VerifyRuntimeRestrictionsOff(latest.WirelessSerial);
            StopPhoneVpn(latest.WirelessSerial, output);
        }

        ConnectionOperationResult Finish(ConnectionOperationTrace trace, int exitCode, string message)
        { LastResult = trace.Finish(exitCode, message); return LastResult; }

        // Startup maintenance is deliberately separate from Stop: an active or
        // uncertain sharing session can never enter any phone/process cleanup.
        public int RecoverStale(Action<string> output) { return RecoverStaleWithResult(output).ExitCode; }

        public ConnectionOperationResult RecoverStaleWithResult(Action<string> output)
        {
            IDisposable operation = null;
            ConnectionOperationTrace trace = new ConnectionOperationTrace(ConnectionOperationKind.RecoverStale, output, stageChanged);
            try
            {
                trace.Move(ConnectionStage.AcquireOperation);
                operation = context.TryEnterCoreMutex();
                if (operation == null)
                {
                    const string busy = "连接操作正在执行，已跳过启动清理。";
                    Report(output, busy);
                    return Finish(trace, 0, busy);
                }
                trace.Move(ConnectionStage.ValidateConfiguration);
                RuntimeState state = context.ReadState();
                ValidateTransport(state);
                if (state == null)
                {
                    const string absent = "当前没有记录共享会话，启动检查无需清理。";
                    Report(output, absent);
                    return CompleteStop(trace, ref operation, absent);
                }
                string token = state.ProtectionSessionId;
                trace.Move(ConnectionStage.VerifyOwnership);
                StaleSessionInspection inspection = context.InspectStaleSession(state);
                if (inspection == null) throw new IOException("无法确认旧会话资源，启动检查已保留会话记录。");
                if (!String.IsNullOrEmpty(inspection.BlockReason)) throw new IOException(inspection.BlockReason);
                if (inspection.HasLiveResources && !inspection.CanRetire)
                {
                    const string active = "检测到正在运行的共享，启动检查已保留当前会话。";
                    Report(output, active);
                    return CompleteStop(trace, ref operation, active);
                }
                if (!inspection.CanRetire || inspection.HasLiveResources)
                    throw new IOException("无法确认旧会话已失效，启动检查已保留会话记录。");
                string recovered = RetireStaleRecord(token, trace);
                Report(output, recovered);
                return CompleteStop(trace, ref operation, recovered);
            }
            catch (Exception error)
            {
                Report(output, "启动检查未完成：" + error.Message);
                return Finish(trace, 1, error.Message);
            }
            finally { if (operation != null) operation.Dispose(); }
        }

        public int Stop(Action<string> output) { return StopWithResult(output).ExitCode; }

        public ConnectionOperationResult StopWithResult(Action<string> output)
        {
            IDisposable operation = null;
            ConnectionOperationTrace trace = new ConnectionOperationTrace(ConnectionOperationKind.Stop, output, stageChanged);
            try
            {
                trace.Move(ConnectionStage.AcquireOperation);
                operation = context.EnterCoreMutex(6000);
                trace.Move(ConnectionStage.ValidateConfiguration);
                RuntimeState state = context.ReadState();
                ValidateTransport(state);
                if (state == null)
                {
                    const string message = "当前没有记录共享会话，无需断开。";
                    Report(output, message);
                    return CompleteStop(trace, ref operation, message);
                }
                string token = state.ProtectionSessionId;
                StaleSessionInspection inspection = context.InspectStaleSession(state);
                if (inspection == null)
                {
                    trace.Move(ConnectionStage.VerifyOwnership);
                    throw new IOException("无法确认旧会话资源，已保留会话记录。");
                }
                if (!String.IsNullOrEmpty(inspection.BlockReason))
                {
                    trace.Move(ConnectionStage.VerifyOwnership);
                    throw new IOException(inspection.BlockReason);
                }
                if (inspection.CanRetire)
                {
                    trace.Move(ConnectionStage.VerifyOwnership);
                    if (inspection.HasLiveResources) throw new IOException("共享资源仍在运行，已保留会话记录。");
                    // An offline phone cannot acknowledge Stop. Retire only a
                    // positively dead PC session, under the original operation lock.
                    string recovered = RetireStaleRecord(token, trace);
                    Report(output, recovered);
                    return CompleteStop(trace, ref operation, recovered);
                }
                if (!inspection.HasLiveResources)
                {
                    trace.Move(ConnectionStage.VerifyOwnership);
                    throw new IOException("无法确认旧会话资源，已保留会话记录。");
                }
                RequirePcOnlySession(state);
                Dictionary<string, object> configuration = ReadConfiguration();
                trace.Move(ConnectionStage.ResolveDevice);
                string serial = ExistingOrDiscoveredSerial(configuration, state);
                trace.Move(ConnectionStage.VerifyDevice);
                devices.VerifyPhone(serial, Get(configuration, "PhoneIdentity"), output);
                devices.VerifyRestrictionsOff(serial, output);
                devices.VerifyRuntimeRestrictionsOff(serial);
                // All side effects remain under the same operation lock, in the
                // same order, with ownership verified before stopping the phone.
                trace.Move(ConnectionStage.VerifyOwnership);
                int relayId = OwnedRelayId(state);
                trace.Move(ConnectionStage.StopPhoneVpn);
                StopPhoneVpn(serial, output);
                trace.Move(ConnectionStage.StopMonitor);
                StopOwnedMonitor(state);
                trace.Move(ConnectionStage.RemoveTunnel);
                RuntimeCommandResult reverse = context.Adb(new string[] { "-s", serial, "reverse", "--list" });
                if (AndroidProtocol.ReverseReady(reverse.Text)) context.Adb(new string[] { "-s", serial, "reverse", "--remove", "localabstract:gnirehtet" });
                trace.Move(ConnectionStage.StopRelay);
                if (relayId != 0)
                {
                    if (Owners(false).Any(delegate(int pid) { return pid != relayId; }))
                        throw new IOException("Relay port owner changed; no unrelated process was stopped.");
                    using (Process owned = context.GetOwnedRelay(state, true)) OwnedProcessManager.TerminatePinned(owned, 5000);
                }
                trace.Move(ConnectionStage.RemoveSession);
                if (state != null) context.DeleteStateIfToken(state.ProtectionSessionId);
                trace.Move(ConnectionStage.DisconnectDevice);
                RuntimeCommandResult disconnect = context.Adb(new string[] { "disconnect", serial }, true);
                if (!disconnect.Success) Report(output, "ADB disconnect warning: " + disconnect.Text);
                Report(output, "Wireless sharing stopped. You may manually turn OFF phone Wireless debugging, then turn OFF its hotspot.");
                // Release at the same point as the original successful finally,
                // before publishing a successful diagnostic result.
                return CompleteStop(trace, ref operation, "Wireless sharing stopped.");
            }
            catch (Exception error)
            {
                Report(output, "失败阶段 · " + ConnectionOperationTrace.Describe(trace.Current));
                Report(output, "ERROR: " + error.Message);
                Report(output, "No VPN switches or network routes were changed. Unverified processes were left untouched.");
                return Finish(trace, 1, error.Message);
            }
            finally { if (operation != null) operation.Dispose(); }
        }

        // Both callers hold the existing core lease and have already obtained a
        // positive, local-only retirement assessment. Recheck before deleting.
        string RetireStaleRecord(string token, ConnectionOperationTrace trace)
        {
            RuntimeState latest = context.ReadState();
            if (latest == null) return "当前没有记录共享会话，无需断开。";
            if (!String.Equals(latest.ProtectionSessionId, token, StringComparison.Ordinal))
                throw new IOException("共享会话已发生变化，已保留当前记录，请重试断开。");
            ValidateTransport(latest);
            StaleSessionInspection confirmed = context.InspectStaleSession(latest);
            if (confirmed == null || !confirmed.CanRetire || confirmed.HasLiveResources || !String.IsNullOrEmpty(confirmed.BlockReason))
                throw new IOException(confirmed != null && !String.IsNullOrEmpty(confirmed.BlockReason) ?
                    confirmed.BlockReason : "共享资源已发生变化，已保留会话记录，请重试断开。");
            trace.Move(ConnectionStage.RemoveSession);
            context.DeleteStateIfToken(token);
            if (context.ReadState() != null)
                throw new IOException("共享会话记录未清理或已发生变化，已保留当前记录。");
            return "电脑端共享进程已结束，已清理失效会话记录。手机 VPN 状态未确认；若仍显示连接，请在手机上手动关闭。";
        }

        ConnectionOperationResult CompleteStop(ConnectionOperationTrace trace, ref IDisposable operation, string message)
        {
            trace.Move(ConnectionStage.ReleaseOperation);
            IDisposable completedOperation = operation; operation = null;
            completedOperation.Dispose();
            return Finish(trace, 0, message);
        }

        void StopPhoneVpn(string serial, Action<string> output)
        {
            Report(output, context.Gnirehtet(new string[] { "stop", serial }).Text);
            bool stopped = false;
            for (int check = 0; check < 10; check++)
            {
                if (!context.PhoneVpnConnected(serial)) { stopped = true; break; }
                context.Sleep(300);
            }
            if (!stopped) throw new IOException("Phone VPN is still connected. Relay left running; retry Disconnect.");
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
                    RequirePcOnlySession(state);
                    string serial = ExistingOrDiscoveredSerial(configuration, state);
                    devices.VerifyPhone(serial, Get(configuration, "PhoneIdentity"), output);
                    int relayId = OwnedRelayId(state);
                    if (relayId == 0) throw new IOException("No tracked wireless relay is running.");
                    RuntimeCommandResult reverse = context.Adb(new string[] { "-s", serial, "reverse", "--list" });
                    if (!Owners(true).Contains(relayId) || !AndroidProtocol.ReverseReady(reverse.Text) || !context.PhoneVpnConnected(serial))
                        throw new IOException("The wireless ADB tunnel, relay connection or phone VPN is not ready.");
                    using (Process monitor = context.GetOwnedMonitor(state))
                        if (!context.ProtectionValid(state) || monitor == null || !context.MonitorHeartbeatFresh(state))
                            throw new IOException("The PC monitor does not have a recent healthy connection check.");
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
            return devices.DiscoverNativeSerial(link.PhoneGateway, Get(configuration, "PhoneIdentity"), Get(configuration, "ConnectEndpoint"));
        }

        int OwnedRelayId(RuntimeState state)
        {
            using (Process relay = context.GetOwnedRelay(state)) return relay == null ? 0 : relay.Id;
        }

        void StopOwnedMonitor(RuntimeState state)
        {
            using (Process monitor = context.GetOwnedMonitor(state, true)) OwnedProcessManager.TerminatePinned(monitor, 3000);
        }

        int[] Owners(bool established) { return context.GetTcpPortOwners(31416, established) ?? new int[0]; }
        static void ValidateTransport(RuntimeState state)
        {
            if (state != null && state.Transport != "NativeTls") throw new IOException("An older non-native sharing session is recorded. Stop it before switching modes.");
        }
        static void RequirePcOnlySession(RuntimeState state)
        {
            if (state != null && (state.ProtectionMode != "PcOnly" || !String.IsNullOrEmpty(state.PhoneControlPath)))
                throw new IOException("An older phone-guard session is recorded. Disconnect it with the original program before using this version; its phone guard will not be replaced or cancelled automatically.");
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
        static void CopyLogRecord(RuntimeState target, RuntimeState source)
        {
            if (source == null) return;
            target.RelayLogPipe = source.RelayLogPipe; target.LogMode = source.LogMode;
            target.RelayHostProcessId = source.RelayHostProcessId; target.RelayHostStartTimeUtcTicks = source.RelayHostStartTimeUtcTicks;
        }
        static void CopyProtection(RuntimeState target, RuntimeState source)
        {
            target.ProtectionSessionId = source.ProtectionSessionId; target.ProtectionTimeoutSeconds = source.ProtectionTimeoutSeconds;
            target.ProtectionMode = source.ProtectionMode; target.PhoneControlPath = source.PhoneControlPath;
            target.WatchdogProcessId = source.WatchdogProcessId;
            target.WatchdogStartTimeUtcTicks = source.WatchdogStartTimeUtcTicks; target.MonitorHeartbeatQpcTicks = source.MonitorHeartbeatQpcTicks;
            target.WatchdogPath = source.WatchdogPath;
        }
    }
}
