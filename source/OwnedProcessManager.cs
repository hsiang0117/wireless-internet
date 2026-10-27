using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WirelessInternet
{
    public enum RecordedProcessPresence { Absent, OwnedAlive, Unknown }

    // Read-only evidence for retiring a record, never authority to stop a process.
    public sealed class StaleSessionInspection
    {
        public readonly bool CanRetire, HasLiveResources;
        public readonly string BlockReason;
        public StaleSessionInspection(bool canRetire, bool hasLiveResources, string blockReason)
        { CanRetire = canRetire; HasLiveResources = hasLiveResources; BlockReason = blockReason ?? String.Empty; }
    }

    // An owned process is always identified by PID, start ticks and exact path.
    // Pinning and termination keep the same Process handle; no tree/name cleanup.
    public sealed class OwnedProcessManager
    {
        readonly string root, relayPath, workerPath;
        public OwnedProcessManager(string root, string relayPath, string workerPath)
        { this.root = root; this.relayPath = relayPath; this.workerPath = workerPath; }

        public static RecordedProcessPresence ProbeRecordedProcess(int pid, string ticks, string expectedPath)
        {
            if (pid == 0 && String.IsNullOrEmpty(ticks)) return RecordedProcessPresence.Absent;
            long recordedTicks;
            if (pid <= 0 || !Int64.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out recordedTicks) ||
                recordedTicks <= 0 || recordedTicks > DateTime.MaxValue.Ticks || String.IsNullOrWhiteSpace(expectedPath))
                return RecordedProcessPresence.Unknown;
            try
            {
                if (!Path.IsPathRooted(expectedPath)) return RecordedProcessPresence.Unknown;
                string expected = Path.GetFullPath(expectedPath);
                Process process;
                try { process = Process.GetProcessById(pid); }
                catch (ArgumentException) { return RecordedProcessPresence.Absent; }
                using (process)
                {
                    // Pin before querying identity so a recycled PID cannot change the observed process.
                    IntPtr held = process.Handle;
                    if (process.HasExited) return RecordedProcessPresence.Absent;
                    long actualTicks = process.StartTime.ToUniversalTime().Ticks;
                    if (actualTicks != recordedTicks) return RecordedProcessPresence.Absent;
                    return String.Equals(Path.GetFullPath(process.MainModule.FileName), expected, StringComparison.OrdinalIgnoreCase) ?
                        RecordedProcessPresence.OwnedAlive : RecordedProcessPresence.Unknown;
                }
            }
            catch { return RecordedProcessPresence.Unknown; }
        }

        public StaleSessionInspection InspectForRetirement(RuntimeState state,
            Func<int, string, string, RecordedProcessPresence> probe, Func<bool> portIdle)
        {
            if (probe == null) throw new ArgumentNullException("probe");
            if (portIdle == null) throw new ArgumentNullException("portIdle");
            if (state == null) return Blocked("没有可核对的共享会话。");
            try
            {
                if (!SamePath(state.RelayPath, relayPath)) return Blocked("旧会话中继路径与当前工具不符，记录已保留。");
                string monitorPath;
                if (state.RuntimeEngine == "CSharp")
                {
                    if (!SamePath(state.WatchdogPath, workerPath)) return Blocked("旧会话保护监控路径与当前工具不符，记录已保留。");
                    monitorPath = workerPath;
                }
                else if (String.IsNullOrEmpty(state.RuntimeEngine) || state.RuntimeEngine == "PowerShell")
                    monitorPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
                else return Blocked("无法识别旧会话的保护监控引擎，记录已保留。");

                RecordedProcessPresence[] presence = {
                    probe(state.RelayProcessId, state.RelayStartTimeUtcTicks, relayPath),
                    probe(state.WatchdogProcessId, state.WatchdogStartTimeUtcTicks, monitorPath),
                    probe(state.RelayHostProcessId, state.RelayHostStartTimeUtcTicks, Path.Combine(root, "support", "RelayLogHost.exe"))
                };
                bool live = presence.Any(delegate(RecordedProcessPresence value) { return value == RecordedProcessPresence.OwnedAlive; });
                if (presence.Any(delegate(RecordedProcessPresence value) {
                    return value != RecordedProcessPresence.Absent && value != RecordedProcessPresence.OwnedAlive;
                })) return new StaleSessionInspection(false, live, "无法确认旧共享进程的归属，记录已保留。");
                if (live) return new StaleSessionInspection(false, true, String.Empty);
                if (!portIdle()) return Blocked("31416 端口仍有监听或已建立连接，旧共享记录已保留。");
                return new StaleSessionInspection(true, false, String.Empty);
            }
            catch { return Blocked("无法完整核对旧共享进程或端口状态，记录已保留。"); }
        }

        static bool SamePath(string recorded, string expected)
        {
            return !String.IsNullOrWhiteSpace(recorded) && Path.IsPathRooted(recorded) &&
                String.Equals(Path.GetFullPath(recorded), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase);
        }
        static StaleSessionInspection Blocked(string reason) { return new StaleSessionInspection(false, false, reason); }

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
        public Process GetOwnedRelay(RuntimeState state, bool pinHandle)
        {
            if (state == null) return null;
            if (!String.Equals(Path.GetFullPath(state.RelayPath ?? ""), relayPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("中继路径与当前工具不符。");
            return GetOwnedProcess(state.RelayProcessId, state.RelayStartTimeUtcTicks, relayPath, pinHandle, true);
        }
        public Process GetOwnedMonitor(RuntimeState state, bool pinHandle)
        {
            if (state == null || state.WatchdogProcessId <= 0) return null;
            string expected;
            if (state.RuntimeEngine == "CSharp")
            {
                if (!String.Equals(Path.GetFullPath(state.WatchdogPath ?? ""), workerPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("监控路径与当前工具不符。");
                expected = workerPath;
            }
            else if (String.IsNullOrEmpty(state.RuntimeEngine) || state.RuntimeEngine == "PowerShell")
            {
                // Compatibility check only: never execute the retired engine.
                expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            }
            else throw new IOException("无法识别运行会话中的监控引擎。");
            return GetOwnedProcess(state.WatchdogProcessId, state.WatchdogStartTimeUtcTicks, expected, pinHandle, true);
        }
        public Process StartDetached(string executable, string[] arguments)
        {
            // ShellExecuteEx starts these trusted background helpers without inheriting the
            // action worker's output pipe. Clearing standard handles alone is insufficient
            // when Windows has also inherited unnamed copies of an output handle.
            return Process.Start(new ProcessStartInfo(executable, String.Join(" ", arguments.Select(ProcessExecution.Quote))) {
                UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root });
        }
        public static void TerminatePinned(Process process, int timeout)
        {
            if (process == null) return;
            if (process.Id == Process.GetCurrentProcess().Id) throw new IOException("Refusing to stop the current controller process.");
            if (!process.HasExited) process.Kill();
            if (!process.WaitForExit(timeout)) throw new IOException("The owned process did not exit in time; its session record was retained.");
        }
    }
}
