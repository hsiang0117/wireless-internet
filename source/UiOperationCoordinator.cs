using System;
using System.Threading.Tasks;

namespace WirelessInternet
{
    public enum UiOperationKind { None, Start, Stop, Configure }

    public sealed class UiOperationPresentation
    {
        public readonly bool StartEnabled, StopEnabled, SettingsEnabled, ExitEnabled;
        public readonly string StartText, StopText, ExitText, TrayState;
        internal UiOperationPresentation(bool start, bool stop, bool settings, bool exit,
            string startText, string stopText, string exitText, string trayState)
        {
            StartEnabled = start; StopEnabled = stop; SettingsEnabled = settings; ExitEnabled = exit;
            StartText = startText; StopText = stopText; ExitText = exitText; TrayState = trayState;
        }
    }

    // Owns operation admission and ordering; contains no Form, process, phone or filesystem I/O.
    // A view may hide at any point without changing this state or cancelling an operation.
    public sealed class UiOperationCoordinator
    {
        readonly object gate = new object();
        UiOperationKind operation;
        bool polling, exitRequested, allowClose;
        volatile bool closing;
        Task pendingPoll;
        TaskCompletionSource<int> actionCompletion;

        public UiOperationKind Operation { get { lock (gate) return operation; } }
        public bool IsPolling { get { lock (gate) return polling; } }
        public bool IsActionBusy { get { lock (gate) return IsAction(operation); } }
        public bool IsConfiguring { get { lock (gate) return operation == UiOperationKind.Configure; } }
        public bool IsExitRequested { get { lock (gate) return exitRequested; } }
        public bool IsClosing { get { return closing; } }
        public bool AllowClose { get { lock (gate) return allowClose; } }
        public Task PendingPoll { get { lock (gate) return pendingPoll; } }

        static bool IsAction(UiOperationKind value) { return value == UiOperationKind.Start || value == UiOperationKind.Stop; }
        bool CanAdmitAction(UiOperationKind value, bool exiting)
        {
            return IsAction(value) && operation == UiOperationKind.None && !closing &&
                (exiting ? exitRequested && value == UiOperationKind.Stop : !exitRequested);
        }
        public bool CanBeginAction(UiOperationKind value, bool exiting)
        { lock (gate) return CanAdmitAction(value, exiting); }
        public bool TryBeginAction(UiOperationKind value, bool exiting)
        {
            lock (gate)
            {
                if (!CanAdmitAction(value, exiting)) return false;
                operation = value;
                actionCompletion = new TaskCompletionSource<int>();
                return true;
            }
        }
        public void CompleteAction(int exitCode)
        {
            TaskCompletionSource<int> completed;
            lock (gate)
            {
                if (!IsAction(operation)) return;
                completed = actionCompletion;
                operation = UiOperationKind.None;
            }
            // Continuations may reenter admission, so never run them while holding gate.
            if (completed != null) completed.TrySetResult(exitCode);
        }
        public bool CanBeginConfiguration(bool preview)
        { lock (gate) return !preview && operation == UiOperationKind.None && !exitRequested && !closing; }
        public bool TryBeginConfiguration(bool preview)
        {
            lock (gate)
            {
                if (preview || operation != UiOperationKind.None || exitRequested || closing) return false;
                operation = UiOperationKind.Configure;
                return true;
            }
        }
        public void CompleteConfiguration()
        { lock (gate) { if (operation == UiOperationKind.Configure) operation = UiOperationKind.None; } }
        public bool TryBeginPoll()
        {
            lock (gate)
            {
                if (polling || operation != UiOperationKind.None || exitRequested || closing) return false;
                polling = true;
                return true;
            }
        }
        public void TrackPoll(Task task)
        {
            if (task == null) throw new ArgumentNullException("task");
            lock (gate)
            {
                if (!polling) throw new InvalidOperationException("A poll must be admitted before tracking its task.");
                pendingPoll = task;
            }
        }
        public void CompletePoll() { lock (gate) polling = false; }
        public Task WaitForPollAsync()
        { Task task; lock (gate) task = pendingPoll; return IgnoreFailure(task); }
        public Task WaitForActionAsync()
        {
            Task task;
            lock (gate) task = IsAction(operation) && actionCompletion != null ? actionCompletion.Task : null;
            return IgnoreFailure(task);
        }
        static async Task IgnoreFailure(Task task)
        { if (task != null) { try { await task.ConfigureAwait(false); } catch { } } }
        public bool TryRequestExit()
        {
            lock (gate)
            {
                if (closing || exitRequested) return false;
                exitRequested = true;
                return true;
            }
        }
        public void CancelExit() { lock (gate) { if (!closing) exitRequested = false; } }
        public void PermitClose() { lock (gate) allowClose = true; }
        public void BeginClosing() { lock (gate) closing = true; }

        public UiOperationPresentation GetPresentation(bool preview, bool hasSession, bool healthy,
            bool configurationMissing, string statusText)
        {
            lock (gate)
            {
                bool actionBusy = IsAction(operation);
                bool available = !closing && !exitRequested && operation == UiOperationKind.None;
                return new UiOperationPresentation(available && !healthy, available && hasSession,
                    available && !preview, !closing && !exitRequested,
                    actionBusy ? "处理中…" : hasSession && healthy ? "已连接" : hasSession ? "重新连接" : configurationMissing ? "配置手机" : "开始连接",
                    actionBusy ? "请稍候" : "断开连接", exitRequested ? "正在断开并退出…" : "退出",
                    exitRequested ? "正在断开并退出" : actionBusy ? "正在处理连接" : statusText ?? "正在读取状态");
            }
        }
    }
}
