using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using WirelessInternet;

internal static class ActionClientChecks
{
    static string worker, root;
    static int passed;
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Case(string name, Action check)
    {
        check(); passed++; Console.WriteLine("PASS " + name);
    }
    static WorkerActionResult Run(string mode, string action, Action<string> log, Action<ConnectionStage> stage, int frameTimeout)
    {
        return WorkerActionClient.Run(worker, root, action, log ?? delegate { }, stage,
            new WorkerActionClientOptions {
                ConnectTimeoutMilliseconds = 500, FrameTimeoutMilliseconds = frameTimeout,
                CompletionDrainMilliseconds = 150,
                Environment = new Dictionary<string, string> { { "WIRELESS_ACTION_FIXTURE", mode } }
            });
    }
    static WorkerActionResult Run(string mode) { return Run(mode, "Start", null, null, 2000); }
    static void Unconfirmed(string mode)
    {
        WorkerActionResult result = Run(mode);
        Require(!result.Verified && result.ExitCode != 0 && result.ProtocolError.Length > 0 && result.FinalStage == null,
            mode + " falsely confirmed an operation");
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2) throw new ArgumentException("Expected fake-worker executable and isolated test root.");
            root = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.Combine(root, "support"));
            worker = Path.Combine(root, "support", "ConnectionWorker.exe"); File.Copy(Path.GetFullPath(args[0]), worker, true);
            Stopwatch suite = Stopwatch.StartNew();
            Case("verified-success", delegate {
                WorkerActionResult result = Run("success");
                Require(result.Verified && result.ExitCode == 0 && result.WorkerExitCode == 0 && result.Action == "Start" &&
                    result.FinalStage == ConnectionStage.Completed && result.Stages.Count == 2 && result.Message == "fixture complete", "normal result lost fields");
                bool readOnly = false; try { result.Stages[0] = ConnectionStage.Completed; } catch (NotSupportedException) { readOnly = true; }
                Require(readOnly, "stage history is mutable");
            });
            Case("verified-action-failure", delegate {
                WorkerActionResult result = Run("failure", "Stop", null, null, 2000);
                Require(result.Verified && result.ExitCode == 7 && result.FinalStage == ConnectionStage.StopPhoneVpn && result.Action == "Stop", "verified action failure lost stage or code");
            });
            Case("missing-final-and-legacy-exit", delegate {
                Unconfirmed("no-final");
                string old = Environment.GetEnvironmentVariable("WIRELESS_ACTION_FIXTURE");
                try
                {
                    Environment.SetEnvironmentVariable("WIRELESS_ACTION_FIXTURE", "no-final");
                    using (Backend backend = new Backend(root, false)) Require(backend.RunAction("Start", delegate { }) != 0, "legacy wrapper accepted exit zero without final result");
                }
                finally { Environment.SetEnvironmentVariable("WIRELESS_ACTION_FIXTURE", old); }
            });
            Case("missing-control-channel", delegate { Unconfirmed("no-channel"); });
            foreach (string mode in new string[] { "wrong-operation", "wrong-action", "wrong-version", "malformed", "oversized", "truncated", "duplicate", "exit-mismatch" })
            {
                string current = mode;
                Case(current, delegate { Unconfirmed(current); });
            }
            Case("stdout-flood-does-not-lose-final", delegate {
                WorkerActionResult result = Run("flood", "Start", delegate { Thread.Sleep(2); }, null, 2000);
                Require(result.Verified && result.ExitCode == 0 && result.DroppedLogLines > 0, "display flood affected independent final result");
            });
            Case("slow-display-callbacks-do-not-block-result", delegate {
                using (ManualResetEvent release = new ManualResetEvent(false))
                using (ManualResetEvent logEntered = new ManualResetEvent(false))
                using (ManualResetEvent stageEntered = new ManualResetEvent(false))
                using (CountdownEvent ended = new CountdownEvent(2))
                {
                    Stopwatch elapsed = Stopwatch.StartNew();
                    WorkerActionResult result;
                    try
                    {
                        result = Run("slow-callbacks", "Start",
                            delegate { logEntered.Set(); release.WaitOne(2000); ended.Signal(); },
                            delegate { stageEntered.Set(); release.WaitOne(2000); ended.Signal(); }, 2000);
                        Require(result.Verified && elapsed.ElapsedMilliseconds < 1000 && logEntered.WaitOne(100) && stageEntered.WaitOne(100),
                            "a slow display callback delayed the control reader");
                    }
                    finally { release.Set(); ended.Wait(2500); }
                }
            });
            Case("control-timeout-never-kills-partial-stop", delegate {
                string marker = Path.Combine(root, "stop-completed.marker");
                if (File.Exists(marker)) File.Delete(marker);
                Stopwatch elapsed = Stopwatch.StartNew();
                WorkerActionResult result = Run("timeout-stop", "Stop", null, null, 80);
                Require(!result.Verified && result.ExitCode != 0 && result.WorkerExitCode == 0 &&
                    File.Exists(marker) && elapsed.ElapsedMilliseconds >= 350, "control timeout killed or falsely confirmed a partial Stop");
            });
            Console.WriteLine("ActionClientChecks: " + passed + " passed in " + suite.ElapsedMilliseconds + " ms");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
