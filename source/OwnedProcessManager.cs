using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace WirelessInternet
{
    // An owned process is always identified by PID, start ticks and exact path.
    // Pinning and termination keep the same Process handle; no tree/name cleanup.
    public sealed class OwnedProcessManager
    {
        readonly string root, relayPath, workerPath;
        public OwnedProcessManager(string root, string relayPath, string workerPath)
        { this.root = root; this.relayPath = relayPath; this.workerPath = workerPath; }

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
