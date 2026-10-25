using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using WirelessInternet;

internal static class UiCoordinatorChecks
{
    static readonly List<object> Results = new List<object>();
    static void Check(bool ok, string name)
    {
        Results.Add(new { Name = name, Passed = ok });
        if (!ok) throw new InvalidOperationException(name);
    }
    static int Main(string[] args)
    {
        string failure = null;
        try
        {
            UiOperationCoordinator c = new UiOperationCoordinator();
            UiOperationPresentation view = c.GetPresentation(false, false, false, false, null);
            Check(view.StartEnabled && !view.StopEnabled && view.SettingsEnabled && view.ExitEnabled, "idle.presentation");
            Check(view.TrayState == "正在读取状态", "idle.status-fallback");
            Check(c.GetPresentation(false, false, false, true, "未连接").StartText == "配置手机", "idle.missing-configuration");
            Check(!c.GetPresentation(true, false, false, false, "预览").SettingsEnabled, "preview.disables-configuration");
            Check(!c.CanBeginConfiguration(true), "preview.configuration-admission-refused");
            view = c.GetPresentation(false, true, true, false, "已连接");
            Check(!view.StartEnabled && view.StopEnabled && view.SettingsEnabled, "healthy.buttons-settings-remain-clickable");
            Check(view.StartText == "已连接", "healthy.start-label");
            view = c.GetPresentation(false, true, false, false, "保护异常");
            Check(view.StartEnabled && view.StopEnabled && view.StartText == "重新连接", "degraded.can-repair-protection");

            TaskCompletionSource<bool> sample = new TaskCompletionSource<bool>();
            Check(c.TryBeginPoll(), "poll.admitted"); c.TrackPoll(sample.Task);
            Check(!c.TryBeginPoll(), "poll.no-overlap");
            Check(c.TryBeginAction(UiOperationKind.Start, false), "start.admitted-while-prior-poll-finishes");
            Task wait = c.WaitForPollAsync();
            Check(!wait.IsCompleted, "start.waits-for-pending-sample");
            Check(!c.TryBeginAction(UiOperationKind.Stop, false) && !c.TryBeginConfiguration(false), "start.excludes-other-actions-and-settings");
            Check(!c.TryBeginPoll(), "start.blocks-new-polls");
            view = c.GetPresentation(false, false, false, false, "未连接");
            Check(!view.StartEnabled && !view.StopEnabled && !view.SettingsEnabled && view.ExitEnabled, "start.presentation-exclusive-exit-available");
            sample.SetResult(true); c.CompletePoll();
            Check(wait.Wait(1000) && !c.IsPolling, "start.sample-drained");
            Check(c.TryRequestExit(), "exit.admitted-during-start");
            Check(!c.TryRequestExit(), "exit.idempotent");
            Task action = c.WaitForActionAsync();
            Check(!action.IsCompleted, "exit.waits-for-start-completion");
            Check(!c.TryBeginAction(UiOperationKind.Stop, true), "exit.stop-cannot-overtake-start");
            c.CompleteAction(0);
            Check(action.Wait(1000) && !c.IsActionBusy, "exit.start-completed");
            Check(!c.TryBeginAction(UiOperationKind.Start, false) && !c.TryBeginAction(UiOperationKind.Stop, false), "exit.rejects-ordinary-actions");
            Check(!c.TryBeginConfiguration(false) && !c.TryBeginPoll(), "exit.rejects-configuration-and-poll");
            Check(!c.TryBeginAction(UiOperationKind.Start, true), "exit.never-admits-exiting-start");
            Check(c.TryBeginAction(UiOperationKind.Stop, true), "exit.stop-admitted-after-start");
            c.CompleteAction(1); c.CancelExit();
            Check(!c.IsExitRequested && !c.AllowClose, "failed-stop.cancel-exit-keeps-observer");
            Check(c.TryBeginConfiguration(false), "configuration.admitted-after-exit-recovery");
            Check(!c.TryBeginConfiguration(false) && !c.TryBeginAction(UiOperationKind.Start, false), "configuration.exclusive-admission");
            Check(!c.TryBeginPoll(), "configuration.suspends-new-polls");
            c.CompleteConfiguration();
            Check(c.Operation == UiOperationKind.None, "configuration.completes");
            Check(c.TryRequestExit(), "exit.retry");
            c.PermitClose(); c.BeginClosing(); c.CancelExit();
            Check(c.IsClosing && c.AllowClose && c.IsExitRequested, "closing.terminal-permission-and-exit-state");
            Check(!c.TryBeginPoll() && !c.TryBeginAction(UiOperationKind.Stop, true) && !c.TryBeginConfiguration(false), "closing.rejects-all-work");
            view = c.GetPresentation(false, true, true, false, "已连接");
            Check(!view.StartEnabled && !view.StopEnabled && !view.SettingsEnabled && !view.ExitEnabled, "closing.presentation-terminal");

            c = new UiOperationCoordinator();
            Check(c.TryBeginPoll(), "faulted-poll.admitted");
            TaskCompletionSource<bool> failed = new TaskCompletionSource<bool>();
            c.TrackPoll(failed.Task); Task drain = c.WaitForPollAsync();
            failed.SetException(new IOException("offline fixture")); c.CompletePoll();
            Check(drain.Wait(1000) && !drain.IsFaulted, "faulted-poll.does-not-prevent-safe-stop");
            Check(c.TryBeginAction(UiOperationKind.Stop, false), "faulted-poll.stop-still-admitted"); c.CompleteAction(0);
            UiOperationCoordinator parallel = new UiOperationCoordinator(); int winners = 0;
            Task[] attempts = new Task[40];
            for (int i = 0; i < attempts.Length; i++) attempts[i] = Task.Run(delegate { if (parallel.TryBeginAction(UiOperationKind.Start, false)) Interlocked.Increment(ref winners); });
            Task.WaitAll(attempts);
            Check(winners == 1, "parallel-admission.one-owner"); parallel.CompleteAction(0);
            Check(parallel.Operation == UiOperationKind.None, "parallel-admission.release");
        }
        catch (Exception exception) { failure = exception.ToString(); }
        string report = Path.GetFullPath(args.Length > 0 ? args[0] : "ui-coordinator-results.json");
        File.WriteAllText(report, new JavaScriptSerializer().Serialize(new { Passed = failure == null, Count = Results.Count, Failure = failure, Checks = Results }), new System.Text.UTF8Encoding(false));
        Console.WriteLine((failure == null ? "PASS " : "FAIL ") + Results.Count + " coordinator checks" + (failure == null ? "" : "\n" + failure));
        return failure == null ? 0 : 1;
    }
}
