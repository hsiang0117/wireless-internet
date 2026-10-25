// Offline runner tests. Only launches the companion fixture from this test directory.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using WirelessInternet;

internal static class ProcessExecutionChecks
{
    private static string fixture, root;
    private static int checks;

    private static int Main(string[] args)
    {
        try
        {
            fixture = Path.GetFullPath(args[0]);
            root = Path.Combine(Path.GetDirectoryName(fixture), "runner-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            BasicArgumentsAndEncoding();
            LimitsAndAdapters();
            TimeoutOwnershipAndEof();
            ProgressIsolation();
            Console.WriteLine("PASS " + checks + " shared-execution checks; only isolated fixture processes, no ADB/network/system changes.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL after " + checks + " checks: " + error); return 1; }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++;
    }
    private static void Reject(Action action, string text, string name)
    {
        Exception failure = null;
        try { action(); } catch (Exception error) { failure = error; }
        Check(failure != null && (text == null || failure.Message.Contains(text)), name);
    }
    private static SharedCommandOptions Options(int timeout = 5000)
    {
        return new SharedCommandOptions { WorkingDirectory = root, TimeoutMilliseconds = timeout };
    }
    private static SharedCommandResult Run(string[] arguments, SharedCommandOptions options = null)
    {
        return ProcessExecution.Run(fixture, arguments, options ?? Options());
    }
    private static void BasicArgumentsAndEncoding()
    {
        string[] values = new string[] { "", "plain", "two words", "\t", "中文🙂", "a\"b", "a\\\"b", "C:\\two words\\", "\\\\server\\directory\\", "line1\nline2", "{}|&<>^%$`", "end\\\\\"" };
        SharedCommandResult result = Run(new string[] { "args" }.Concat(values).ToArray());
        string[] actual = result.Output.TrimEnd('\r', '\n').Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
        // The first empty argument is an empty base64 line and must remain in the output.
        Check(actual.Length == values.Length, "Windows argument count roundtrip");
        for (int index = 0; index < values.Length; index++)
            Check(Encoding.UTF8.GetString(Convert.FromBase64String(actual[index])) == values[index], "Argument roundtrip " + index);
        SharedCommandResult raw = ProcessExecution.Run(fixture, "args " + ProcessExecution.Quote("raw argument \\\""), Options());
        Check(Encoding.UTF8.GetString(Convert.FromBase64String(raw.Output.Trim())) == "raw argument \\\"", "Raw argument overload preserves caller quoting");
        SharedCommandResult exit = Run(new string[] { "exit", "17" });
        Check(exit.ExitCode == 17 && exit.Output == "stdout 中文" && exit.Error == "stderr 中文", "Exit code and UTF8 streams remain separate");
        Check(!exit.OutputIncomplete && !exit.TimedOut, "Normal completion reports complete capture");
        Check(Run(new string[] { "cwd" }).Output == root, "Child working directory is explicit");
        string variable = "WIRELESS_RUNNER_QA_" + Guid.NewGuid().ToString("N");
        SharedCommandOptions env = Options();
        env.Environment = new Dictionary<string, string> { { variable, "child-only 中文" } };
        Check(Run(new string[] { "env", variable }, env).Output == "child-only 中文", "Per-child environment override");
        Check(Environment.GetEnvironmentVariable(variable) == null, "Environment override never mutates parent");
        env.Environment[variable] = null;
        Check(Run(new string[] { "env", variable }, env).Output == "<absent>", "Null child environment value removes only child entry");
        string supplied = "mock pairing 123456\nUnicode 中文🙂\n";
        SharedCommandOptions stdin = Options();
        stdin.StandardInput = supplied;
        int callbacks = 0;
        stdin.ProgressLine = delegate { Interlocked.Increment(ref callbacks); };
        SharedCommandResult received = Run(new string[] { "stdin" }, stdin);
        Check(received.Output == Convert.ToBase64String(new UTF8Encoding(false).GetBytes(supplied)), "Stdin is exact UTF8 without BOM and reaches EOF");
        Check(!received.InputFailed && callbacks == 0, "Secret stdin disables all progress callbacks");
        Check(Run(new string[] { "stdin" }).Output == String.Empty, "No stdin explicitly supplies EOF");
        Reject(delegate { ProcessExecution.Quote(null); }, null, "Null argument is rejected");
        Reject(delegate { ProcessExecution.Quote("bad\0argument"); }, "null character", "NUL argument is rejected");
        SharedCommandOptions invalid = Options(); invalid.MaximumOutputCharacters = Int32.MaxValue;
        Reject(delegate { Run(new string[] { "exit", "0" }, invalid); }, null, "Capture limit cannot be made unbounded");
    }

    private static void LimitsAndAdapters()
    {
        SharedCommandOptions tiny = Options(); tiny.MaximumOutputCharacters = 257; tiny.MaximumErrorCharacters = 383;
        SharedCommandResult flood = Run(new string[] { "flood", "2097152" }, tiny);
        Check(flood.ExitCode == 0 && !flood.OutputIncomplete, "Concurrent 2MiB streams are fully drained without pipe deadlock");
        Check(flood.Output.Length == 257 && flood.Error.Length == 383 && flood.OutputTruncated && flood.ErrorTruncated,
            "Independent capture limits and truncation flags");
        Check(flood.Output.All(delegate(char c) { return c == 'A'; }) && flood.Error.All(delegate(char c) { return c == 'B'; }),
            "Captured prefixes are not mixed between streams");
        SharedCommandOptions zero = Options(); zero.MaximumOutputCharacters = 0; zero.MaximumErrorCharacters = 0;
        SharedCommandResult discarded = Run(new string[] { "flood", "131072" }, zero);
        Check(discarded.Output == "" && discarded.Error == "" && !discarded.OutputIncomplete && discarded.OutputTruncated,
            "Zero capture still drains both streams");
        RuntimeContext runtime = new RuntimeContext(root);
        RuntimeCommandResult runtimeFlood = runtime.Run(fixture, new string[] { "flood", "131072" });
        Check(runtimeFlood.Output.Length == 65536 && runtimeFlood.Error.Length == 65536, "Runtime retains 64Ki characters per stream");
        SetupProcessRunner setup = new SetupProcessRunner();
        SetupCommandResult setupFlood = setup.Run(fixture, "flood 131072", null, 5000);
        Check(setupFlood.Output.Length == 16384 && setupFlood.Error.Length == 16384, "Setup retains 16Ki characters per stream");
        Check(runtime.Run(fixture, new string[] { "exit", "19" }, true).ExitCode == 19, "Runtime allowFailure preserves nonzero exit");
        Reject(delegate { runtime.Run(fixture, new string[] { "exit", "19" }); }, "操作未完成", "Runtime disallowed failure retains its exception semantics");
        Check(setup.Run(fixture, "exit 19", null, 5000).ExitCode == 19, "Setup keeps nonzero exit for existing caller checks");
        string input = "fake-code-648219\n中文";
        Check(setup.Run(fixture, "stdin", input, 5000).Output == Convert.ToBase64String(Encoding.UTF8.GetBytes(input)), "Setup stdin adapter preserves UTF8 input");
        Check(RuntimeContext.Quote("trailing space\\") == ProcessExecution.Quote("trailing space\\"), "Runtime Quote delegates to common quoting");
    }

    private static void TimeoutOwnershipAndEof()
    {
        string childRecord = Path.Combine(root, "owned-client.pid");
        using (Process unrelated = Process.Start(new ProcessStartInfo(fixture, "stall 12000") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root }))
        {
            IntPtr held = unrelated.Handle;
            try
            {
                Stopwatch timer = Stopwatch.StartNew();
                SharedCommandResult timed = Run(new string[] { "record-stall", childRecord, "12000" }, Options(500));
                Check(timed.TimedOut && timed.ExitCode == 124 && timer.ElapsedMilliseconds < 3000, "Timeout returns 124 within bounded cleanup budget");
                Check(File.Exists(childRecord) && !RecordAlive(childRecord), "Timeout stops only its exact owned fixture client");
                Check(!unrelated.HasExited, "Timeout leaves another process with the same executable name alive");
            }
            finally { if (!unrelated.HasExited) { unrelated.Kill(); unrelated.WaitForExit(1000); } }
        }
        RuntimeContext runtime = new RuntimeContext(root);
        Check(runtime.Run(fixture, new string[] { "stall", "5000" }, true, 100).ExitCode == 124, "Runtime allowFailure timeout remains 124");
        Reject(delegate { runtime.Run(fixture, new string[] { "stall", "5000" }, false, 100); }, "操作超时", "Runtime required timeout retains old message");
        Reject(delegate { new SetupProcessRunner().Run(fixture, "stall 5000", null, 100); }, "ADB 操作超时", "Setup timeout retains old message");
        SharedCommandOptions blockedInput = Options(200); blockedInput.StandardInput = new string('Z', 262144);
        Stopwatch inputTime = Stopwatch.StartNew();
        Check(Run(new string[] { "stall", "5000" }, blockedInput).TimedOut && inputTime.ElapsedMilliseconds < 3000,
            "A child that never reads stdin cannot block the timeout");
        SharedCommandOptions infinite = Options(Timeout.Infinite);
        Check(!Run(new string[] { "stall", "200" }, infinite).TimedOut, "Infinite action deadline remains supported without forced termination");

        string inherited = Path.Combine(root, "inherited-output.pid");
        try
        {
            SharedCommandOptions options = Options(); options.DrainTimeoutMilliseconds = 150;
            Stopwatch timer = Stopwatch.StartNew();
            SharedCommandResult result = Run(new string[] { "fork-exit", inherited }, options);
            Check(result.ExitCode == 0 && result.OutputIncomplete && timer.ElapsedMilliseconds < 3000,
                "Inherited descendant output is an incomplete bounded drain, not an infinite EOF wait");
            Check(RecordAlive(inherited), "Incomplete EOF cleanup does not kill a descendant");
        }
        finally { KillFixture(inherited); }
        string descendant = Path.Combine(root, "timeout-descendant.pid");
        try
        {
            SharedCommandOptions options = Options(700); options.DrainTimeoutMilliseconds = 100;
            SharedCommandResult result = Run(new string[] { "fork-stall", descendant }, options);
            Check(result.TimedOut && RecordAlive(descendant), "Timeout never kills a process tree");
        }
        finally { KillFixture(descendant); }
    }

    private static void ProgressIsolation()
    {
        Stopwatch timer = Stopwatch.StartNew();
        long firstAt = -1;
        List<string> lines = new List<string>();
        List<bool> sources = new List<bool>();
        SharedCommandOptions phases = Options(Timeout.Infinite);
        phases.ProgressLine = delegate(string line, bool error) {
            lock (lines) { if (firstAt < 0) firstAt = timer.ElapsedMilliseconds; lines.Add(line); sources.Add(error); }
        };
        SharedCommandResult completed = Run(new string[] { "phases" }, phases);
        Check(completed.ExitCode == 0 && firstAt >= 0 && firstAt < 600, "Progress arrives while the command is still running");
        Check(lines.SequenceEqual(new string[] { "phase-one", "phase-two" }) && !sources[0] && sources[1], "Progress preserves stderr identity");
        int callbacks = 0, lateCallbacks = 0, returned = 0;
        using (ManualResetEvent release = new ManualResetEvent(false))
        {
            SharedCommandOptions slow = Options();
            slow.ProgressLine = delegate {
                if (Interlocked.CompareExchange(ref returned, 0, 0) != 0) Interlocked.Increment(ref lateCallbacks);
                Interlocked.Increment(ref callbacks);
                release.WaitOne(5000);
            };
            timer.Restart();
            SharedCommandResult result = Run(new string[] { "progress", "10000", "200" }, slow);
            Interlocked.Exchange(ref returned, 1); release.Set();
            Thread.Sleep(100);
            Check(result.ExitCode == 0 && !result.OutputIncomplete && timer.ElapsedMilliseconds < 3000,
                "Blocked callback never blocks output drain or successful command completion");
            Check(result.DroppedProgressLines > 0 && callbacks == 1 && lateCallbacks == 0,
                "Progress queue is bounded and unissued callbacks are discarded before return");
        }
        SharedCommandOptions throwing = Options(); throwing.ProgressLine = delegate { throw new Exception("Synthetic UI callback failure"); };
        Check(Run(new string[] { "progress", "50", "10" }, throwing).ExitCode == 0, "Callback exceptions do not fail a command");
        int maximum = 0; bool truncationMarker = false;
        SharedCommandOptions longLine = Options();
        longLine.ProgressLine = delegate(string line, bool error) { maximum = Math.Max(maximum, line.Length); truncationMarker |= line.EndsWith(" [truncated]"); };
        SharedCommandResult longResult = Run(new string[] { "progress", "1", "100000" }, longLine);
        Check(maximum <= 4096 && truncationMarker && !longResult.OutputIncomplete, "Unterminated long progress lines remain bounded and fully drained");
    }

    private static bool RecordAlive(string record)
    {
        if (!File.Exists(record)) return false;
        string[] fields = File.ReadAllText(record).Split('|');
        int id = Int32.Parse(fields[0], CultureInfo.InvariantCulture); long ticks = Int64.Parse(fields[1], CultureInfo.InvariantCulture);
        try
        {
            using (Process process = Process.GetProcessById(id))
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks &&
                    String.Equals(Path.GetFullPath(process.MainModule.FileName), fixture, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }
    private static void KillFixture(string record)
    {
        if (!File.Exists(record)) return;
        string[] fields = File.ReadAllText(record).Split('|');
        int id = Int32.Parse(fields[0], CultureInfo.InvariantCulture); long ticks = Int64.Parse(fields[1], CultureInfo.InvariantCulture);
        try
        {
            using (Process process = Process.GetProcessById(id))
            {
                IntPtr pinned = process.Handle;
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == ticks &&
                    String.Equals(Path.GetFullPath(process.MainModule.FileName), fixture, StringComparison.OrdinalIgnoreCase))
                { process.Kill(); process.WaitForExit(1000); }
            }
        }
        catch (ArgumentException) { }
    }
}
