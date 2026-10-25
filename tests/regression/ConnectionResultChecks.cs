using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using WirelessInternet;
using FakeContext = ConnectionLifecycleChecks.FakeContext;

// Uses the same owned fake contexts as the baseline characterization, but tests
// only the new structured result API. No real SDK, network or phone is accessed.
internal static class ConnectionResultChecks
{
    static readonly List<object> Results = new List<object>();
    static int assertions, failures;
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fake-lifecycle")
        { Console.WriteLine("READY " + args[1]); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); return 0; }
        string report = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetDirectoryName(typeof(ConnectionResultChecks).Assembly.Location), "connection-result-results.json");
        ConnectionLifecycleChecks.InitializeFixtures(ConnectionLifecycleChecks.CreateFixtureDirectory(Path.GetDirectoryName(report), "rc-"));
        Case("completed Start and Stop have exact ordered immutable histories", delegate(FakeContext c) {
            var controller = new ConnectionController(c);
            Check(controller.LastResult == null, "no fabricated result before action");
            c.BeginAction(); var start = controller.StartWithResult(c.Output.Add);
            Check(start.Success && start.ExitCode == 0 && start.Stage == ConnectionStage.Completed && start.Operation == ConnectionOperationKind.Start, "start result identity");
            Check(Object.ReferenceEquals(start, controller.LastResult), "LastResult references completed start result");
            var expectedStart = new[] { ConnectionStage.AcquireOperation, ConnectionStage.ValidateConfiguration, ConnectionStage.ResolveDevice, ConnectionStage.VerifyDevice,
                ConnectionStage.AcquireRelay, ConnectionStage.PrepareProtection, ConnectionStage.CommitSession, ConnectionStage.EstablishTunnel, ConnectionStage.StartMonitor,
                ConnectionStage.ReleaseOperation, ConnectionStage.AwaitProtection, ConnectionStage.Completed };
            Check(start.Stages.SequenceEqual(expectedStart), "ordered start history");
            Check(!String.IsNullOrWhiteSpace(start.Message), "success message present");
            Check(start.Stages.IsReadOnly, "history declares read only");
            Reject(delegate { start.Stages.Add(ConnectionStage.StopRelay); }, "history append rejected");
            Reject(delegate { start.Stages[0] = ConnectionStage.Completed; }, "history element assignment rejected");
            Reject(delegate { start.Stages.Clear(); }, "history clear rejected");
            c.BeginAction(); var stop = controller.StopWithResult(c.Output.Add);
            Check(stop.Success && stop.ExitCode == 0 && stop.Stage == ConnectionStage.Completed && stop.Operation == ConnectionOperationKind.Stop, "stop result identity");
            var expectedStop = new[] { ConnectionStage.AcquireOperation, ConnectionStage.ValidateConfiguration, ConnectionStage.ResolveDevice, ConnectionStage.VerifyDevice,
                ConnectionStage.VerifyOwnership, ConnectionStage.StopPhoneVpn, ConnectionStage.CancelPhoneGuard, ConnectionStage.StopMonitor, ConnectionStage.RemoveTunnel,
                ConnectionStage.StopRelay, ConnectionStage.RemoveSession, ConnectionStage.DisconnectDevice, ConnectionStage.ReleaseOperation, ConnectionStage.Completed };
            Check(stop.Stages.SequenceEqual(expectedStop), "ordered stop history");
            Check(Object.ReferenceEquals(stop, controller.LastResult), "LastResult advances to stop");
            Check(start.Stages.SequenceEqual(expectedStart), "later action does not mutate old result history");
            Check(start.Success && start.Operation == ConnectionOperationKind.Start, "old start result retains identity");
        });
        Failure("busy core lock", "lock-acquire", ConnectionStage.AcquireOperation);
        Failure("invalid configuration", "config", ConnectionStage.ValidateConfiguration);
        Failure("ambiguous native endpoint", "discovery", ConnectionStage.ResolveDevice);
        Failure("wrong phone identity", "identity", ConnectionStage.VerifyDevice);
        Failure("relay creation", "host-create", ConnectionStage.AcquireRelay);
        Failure("guard arming", "guard-arm", ConnectionStage.PrepareProtection);
        Failure("first session commit", "state-first", ConnectionStage.CommitSession);
        Failure("phone disconnect after commit", "tunnel-offline", ConnectionStage.EstablishTunnel);
        Failure("VPN not ready", "vpn-denied", ConnectionStage.EstablishTunnel);
        Failure("refused startup heartbeat", "beat-refused", ConnectionStage.EstablishTunnel);
        Failure("monitor creation", "monitor-create", ConnectionStage.StartMonitor);
        Failure("first heartbeat acknowledgement", "ack", ConnectionStage.AwaitProtection);
        StopFailure("wrong relay owner", "relay", ConnectionStage.VerifyOwnership);
        StopFailure("runtime restriction", "restriction", ConnectionStage.VerifyDevice);
        StopFailure("unconfirmed VPN stop", "vpn-stop", ConnectionStage.StopPhoneVpn);
        StopFailure("wrong monitor identity", "monitor", ConnectionStage.StopMonitor);
        StopFailure("new port listener", "listener", ConnectionStage.StopRelay);
        Case("Start lease release exception is a failed ReleaseOperation result without retrying Dispose", delegate(FakeContext c) {
            var controller = new ConnectionController(c); c.BeginAction(); c.ThrowOnRelease = true;
            var result = controller.StartWithResult(c.Output.Add);
            Check(!result.Success && result.ExitCode == 1 && result.Stage == ConnectionStage.ReleaseOperation, "release failure has exact failed start stage");
            Check(!result.Stages.Contains(ConnectionStage.Completed) && !result.Stages.Contains(ConnectionStage.AwaitProtection), "start never reports completion or waits for ack after release failure");
            Check(c.PrimaryReleaseInvocations == 1 && c.LockDepth == 0, "primary lease disposal attempted exactly once");
            Check(c.GuardStopAttempts == 1 && c.GuardStops == 1 && c.LocklessGuardStops == 1, "new guard cleanup remains token protected after PC lease release");
            Check(c.State != null && c.Relay.IsAlive && !c.Vpn, "committed relay retained for normal recovery");
            Check(Object.ReferenceEquals(result, controller.LastResult) && result.Message.Contains("lease release"), "release failure reason published");
        });
        Case("Stop lease release exception does not publish successful completion", delegate(FakeContext c) {
            var controller = new ConnectionController(c); c.BeginAction(); Check(controller.StartWithResult(null).Success, "setup started");
            c.BeginAction(); c.ThrowOnRelease = true; var result = controller.StopWithResult(c.Output.Add);
            Check(!result.Success && result.ExitCode == 1 && result.Stage == ConnectionStage.ReleaseOperation, "release failure has exact failed stop stage");
            Check(!result.Stages.Contains(ConnectionStage.Completed), "stop result cannot claim completion");
            Check(c.PrimaryReleaseInvocations == 1 && c.LockDepth == 0, "stop primary lease disposal attempted exactly once");
            Check(c.State == null && !c.Relay.IsAlive && !c.Monitor.IsAlive && !c.Vpn, "already completed owned cleanup is preserved");
            Check(Object.ReferenceEquals(result, controller.LastResult) && result.Message.Contains("lease release"), "stop release failure reason retained");
        });
        Case("legacy integer API forwards results and progress exceptions do not alter transaction", delegate(FakeContext c) {
            var controller = new ConnectionController(c);
            c.BeginAction(); int start = controller.Start(delegate { throw new InvalidOperationException("fixture UI progress sink"); });
            Check(start == 0 && controller.LastResult.Success && controller.LastResult.Stage == ConnectionStage.Completed, "legacy start exit code and result");
            c.BeginAction(); int stop = controller.Stop(delegate { throw new InvalidOperationException("fixture UI progress sink"); });
            Check(stop == 0 && controller.LastResult.Success && controller.LastResult.Operation == ConnectionOperationKind.Stop, "legacy stop exit code and result");
            c.ReturnedIdentity = "OTHER_PHONE"; c.BeginAction(); int failed = controller.Start(null);
            Check(failed == 1 && !controller.LastResult.Success && controller.LastResult.Stage == ConnectionStage.VerifyDevice, "legacy failure result and null sink");
            Check(typeof(ConnectionController).GetProperty("LastResult").GetSetMethod() == null, "LastResult has no public setter");
            foreach (string field in new[] { "Operation", "Stage", "ExitCode", "Message", "Stages" })
                Check(typeof(ConnectionOperationResult).GetField(field).IsInitOnly, "result field immutable: " + field);
        });
        string json = new JavaScriptSerializer().Serialize(new { suite = "ConnectionResultChecks", cases = Results.Count, assertions = assertions, failures = failures, realAdbCalls = 0, realNetworkCalls = 0, results = Results });
        File.WriteAllText(report, json); Console.WriteLine("RESULT " + json);
        Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + assertions + " structured connection result assertions in " + Results.Count + " cases");
        return failures == 0 ? 0 : 1;
    }
    static void Failure(string name, string failure, ConnectionStage expected)
    {
        Case("Start reports " + name, delegate(FakeContext c) {
            c.Fail = failure;
            if (failure == "config") c.Config["DisconnectTimeoutSeconds"] = 121;
            if (failure == "identity") c.ReturnedIdentity = "OTHER_PHONE";
            if (failure == "discovery") c.Discovery += "\nadb-LIFECYCLE_PHONE-second _adb-tls-connect._tcp. 203.0.113.9:39999";
            var controller = new ConnectionController(c); c.BeginAction(); var result = controller.StartWithResult(c.Output.Add);
            Check(!result.Success && result.ExitCode == 1 && result.Operation == ConnectionOperationKind.Start, "failure status identity");
            Check(result.Stage == expected && result.Stages.Last() == expected, "exact failed start stage");
            Check(!result.Stages.Contains(ConnectionStage.Completed), "failed action never completes");
            Check(!String.IsNullOrWhiteSpace(result.Message), "original failure reason retained");
            Check(Object.ReferenceEquals(result, controller.LastResult), "failure published in LastResult");
            Check(c.Output.Any(x => x.StartsWith("失败阶段 · ", StringComparison.Ordinal)), "failed stage sent to existing progress channel");
        });
    }
    static void StopFailure(string name, string failure, ConnectionStage expected)
    {
        Case("Stop reports " + name, delegate(FakeContext c) {
            var controller = new ConnectionController(c); c.BeginAction(); Check(controller.StartWithResult(null).Success, "setup started");
            if (failure == "relay") c.State.RelayStartTimeUtcTicks = "1";
            else if (failure == "restriction") c.RuntimeRestrictions = true;
            else if (failure == "monitor") c.State.WatchdogStartTimeUtcTicks = "1";
            else if (failure == "listener") c.Extra = c.AddChild(c.RelayHostPath);
            else c.Fail = failure;
            c.BeginAction(); var result = controller.StopWithResult(c.Output.Add);
            Check(!result.Success && result.ExitCode == 1 && result.Operation == ConnectionOperationKind.Stop, "stop failure identity");
            Check(result.Stage == expected && result.Stages.Last() == expected, "exact failed stop stage");
            Check(!result.Stages.Contains(ConnectionStage.Completed), "failed stop cannot claim completion");
            Check(!String.IsNullOrWhiteSpace(result.Message) && Object.ReferenceEquals(result, controller.LastResult), "reason and latest result retained");
        });
    }
    static void Reject(Action action, string label)
    {
        bool rejected = false; try { action(); } catch (NotSupportedException) { rejected = true; }
        Check(rejected, label);
    }
    static void Check(bool condition, string label) { assertions++; if (!condition) throw new InvalidOperationException(label); }
    static void Case(string name, Action<FakeContext> test)
    {
        int start = assertions; string error = null; FakeContext context = null;
        try {
            using (context = new FakeContext()) {
                test(context); Check(context.LockDepth == 0, "all leases released"); Check(context.LockViolations == 0 && context.Illegal == 0, "no new unsafe or real I/O");
            }
        }
        catch (Exception exception) { failures++; error = exception.ToString(); }
        Results.Add(new { name = name, passed = error == null, assertions = assertions - start, error = error,
            output = error != null && context != null ? context.Output.ToArray() : null });
        Console.WriteLine((error == null ? "PASS " : "FAIL ") + name);
    }
}
