using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WirelessInternet
{
    public sealed class SharedCommandOptions
    {
        public string WorkingDirectory, StandardInput;
        public int TimeoutMilliseconds = 8000;
        public int MaximumOutputCharacters = 65536, MaximumErrorCharacters = 65536;
        public int DrainTimeoutMilliseconds = 1000;
        public IDictionary<string, string> Environment;
        // Delivered on a background dispatcher; true identifies stderr. Slow handlers may
        // lose progress lines, but never block pipe draining or command completion.
        public Action<string, bool> ProgressLine;
    }

    public sealed class SharedCommandResult
    {
        public int ExitCode;
        public string Output = String.Empty, Error = String.Empty;
        public bool TimedOut, OutputIncomplete, InputFailed, OutputTruncated, ErrorTruncated;
        public long DroppedProgressLines;
    }

    // Short-lived command clients only. Long-lived relay hosts and detached monitors
    // deliberately keep their separate ownership and standard-handle isolation.
    public static class ProcessExecution
    {
        private const int MaximumCaptureLimit = 1048576;

        public static string Quote(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value.IndexOf('\0') >= 0) throw new ArgumentException("A command argument contains a null character.");
            bool quoted = value.Length == 0;
            foreach (char character in value) if (Char.IsWhiteSpace(character) || character == '"') { quoted = true; break; }
            if (!quoted) return value;
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }

        public static SharedCommandResult Run(string executable, string[] arguments, SharedCommandOptions options)
        {
            if (arguments == null) throw new ArgumentNullException("arguments");
            StringBuilder command = new StringBuilder();
            foreach (string argument in arguments)
            {
                if (command.Length != 0) command.Append(' ');
                command.Append(Quote(argument));
            }
            return Run(executable, command.ToString(), options);
        }

        public static SharedCommandResult Run(string executable, string rawArguments, SharedCommandOptions options)
        {
            if (String.IsNullOrWhiteSpace(executable)) throw new ArgumentException("A command executable is required.");
            if (options == null) throw new ArgumentNullException("options");
            if (options.TimeoutMilliseconds < Timeout.Infinite)
                throw new ArgumentOutOfRangeException("TimeoutMilliseconds");
            if (options.DrainTimeoutMilliseconds < 0 || options.DrainTimeoutMilliseconds > 10000)
                throw new ArgumentOutOfRangeException("DrainTimeoutMilliseconds");
            ValidateLimit(options.MaximumOutputCharacters, "MaximumOutputCharacters");
            ValidateLimit(options.MaximumErrorCharacters, "MaximumErrorCharacters");
            if (rawArguments != null && rawArguments.IndexOf('\0') >= 0)
                throw new ArgumentException("Command arguments contain a null character.");

            int timeout = options.TimeoutMilliseconds, drainTimeout = options.DrainTimeoutMilliseconds;
            string input = options.StandardInput;
            // Stdin may contain a pairing secret. Never turn echoed input into progress.
            ProgressDispatcher progress = input == null && options.ProgressLine != null ? new ProgressDispatcher(options.ProgressLine) : null;
            Process process = new Process();
            Capture stdout = null, stderr = null;
            Task outputTask = null, errorTask = null, inputTask = null;
            bool inputFailed = false;
            Stream inputStream = null;
            try
            {
                process.StartInfo = new ProcessStartInfo(executable, rawArguments ?? String.Empty) {
                    UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = options.WorkingDirectory ?? String.Empty,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
                };
                if (options.Environment != null)
                    foreach (KeyValuePair<string, string> pair in options.Environment)
                        if (pair.Value == null) process.StartInfo.EnvironmentVariables.Remove(pair.Key);
                        else process.StartInfo.EnvironmentVariables[pair.Key] = pair.Value;
                Stopwatch deadline = Stopwatch.StartNew();
                process.Start();
                // Hold the exact process handle created here before any timeout cleanup.
                IntPtr pinned = process.Handle;
                stdout = new Capture(process.StandardOutput, options.MaximumOutputCharacters, progress, false);
                stderr = new Capture(process.StandardError, options.MaximumErrorCharacters, progress, true);
                outputTask = Task.Factory.StartNew(stdout.Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                errorTask = Task.Factory.StartNew(stderr.Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                inputStream = process.StandardInput.BaseStream;
                if (input == null) inputStream.Close();
                else
                {
                    Stream destination = inputStream;
                    string supplied = input;
                    inputTask = Task.Factory.StartNew(delegate {
                        byte[] bytes = null;
                        try
                        {
                            bytes = new UTF8Encoding(false).GetBytes(supplied);
                            destination.Write(bytes, 0, bytes.Length);
                            destination.Flush();
                        }
                        catch { inputFailed = true; }
                        finally
                        {
                            supplied = null;
                            if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                            try { destination.Close(); } catch { }
                        }
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                }
                input = null;
                bool timedOut = false;
                if (timeout == Timeout.Infinite)
                {
                    // The no-argument overload also waits for redirected EOF and can hang
                    // after a descendant inherits a pipe. Only wait on the owned process.
                    while (!process.WaitForExit(250)) { }
                }
                else
                {
                    int remaining = (int)Math.Max(0, timeout - Math.Min(Int32.MaxValue, deadline.ElapsedMilliseconds));
                    if (!process.WaitForExit(remaining))
                    {
                        timedOut = true;
                        // Never enumerate or kill by PID/name, a process tree, or the shared ADB server.
                        try { process.Kill(); process.WaitForExit(1000); } catch { }
                    }
                }
                List<Task> drains = new List<Task> { outputTask, errorTask };
                if (inputTask != null) drains.Add(inputTask);
                Task.WaitAll(drains.ToArray(), drainTimeout);
                if (progress != null) progress.Complete(250);
                return new SharedCommandResult {
                    ExitCode = timedOut ? 124 : process.ExitCode,
                    TimedOut = timedOut,
                    Output = stdout.Text, Error = stderr.Text,
                    OutputIncomplete = !outputTask.IsCompleted || !errorTask.IsCompleted || stdout.Failed || stderr.Failed,
                    InputFailed = inputFailed || (inputTask != null && !inputTask.IsCompleted),
                    OutputTruncated = stdout.Truncated, ErrorTruncated = stderr.Truncated,
                    DroppedProgressLines = progress == null ? 0 : progress.Dropped
                };
            }
            finally
            {
                input = null;
                if (progress != null) progress.Complete(0);
                try { if (inputStream != null) inputStream.Close(); } catch { }
                if (stdout != null) stdout.Close();
                if (stderr != null) stderr.Close();
                process.Dispose();
            }
        }

        private static void ValidateLimit(int limit, string name)
        {
            if (limit < 0 || limit > MaximumCaptureLimit) throw new ArgumentOutOfRangeException(name);
        }

        private sealed class Capture
        {
            private readonly StreamReader reader;
            private readonly int limit;
            private readonly ProgressDispatcher progress;
            private readonly bool error;
            private readonly object gate = new object();
            private readonly StringBuilder text = new StringBuilder();
            private readonly StringBuilder line = new StringBuilder();
            private bool truncated, lineTruncated;
            private volatile bool failed, closing;
            public Capture(StreamReader reader, int limit, ProgressDispatcher progress, bool error)
            { this.reader = reader; this.limit = limit; this.progress = progress; this.error = error; }
            public string Text { get { lock (gate) return text.ToString(); } }
            public bool Truncated { get { lock (gate) return truncated; } }
            public bool Failed { get { return failed; } }
            public void Pump()
            {
                try
                {
                    char[] block = new char[2048]; int count;
                    while ((count = reader.Read(block, 0, block.Length)) > 0)
                    {
                        lock (gate)
                        {
                            int keep = Math.Min(count, limit - text.Length);
                            if (keep > 0) text.Append(block, 0, keep);
                            if (keep < count) truncated = true;
                        }
                        if (progress != null)
                            for (int index = 0; index < count; index++)
                            {
                                char character = block[index];
                                if (character == '\n') PublishLine();
                                else if (line.Length < 4096) line.Append(character);
                                else lineTruncated = true;
                            }
                    }
                    if (progress != null && (line.Length > 0 || lineTruncated)) PublishLine();
                }
                catch { if (!closing) failed = true; }
            }
            private void PublishLine()
            {
                if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;
                if (lineTruncated)
                {
                    const string marker = " [truncated]";
                    line.Length = Math.Min(line.Length, 4096 - marker.Length);
                    line.Append(marker);
                }
                progress.Add(line.ToString(), error);
                line.Length = 0; lineTruncated = false;
            }
            public void Close()
            {
                closing = true;
                try { reader.Close(); } catch { }
            }
        }

        private sealed class ProgressDispatcher
        {
            private sealed class Item { public string Text; public bool Error; }
            private readonly object gate = new object();
            private readonly Queue<Item> queue = new Queue<Item>();
            private readonly AutoResetEvent ready = new AutoResetEvent(false);
            private readonly Action<string, bool> callback;
            private readonly Task worker;
            private bool complete, suppress;
            private long dropped;
            public ProgressDispatcher(Action<string, bool> callback)
            {
                this.callback = callback;
                worker = Task.Factory.StartNew(Dispatch, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            public long Dropped { get { return Interlocked.Read(ref dropped); } }
            public void Add(string text, bool error)
            {
                lock (gate)
                {
                    if (complete || suppress) { Interlocked.Increment(ref dropped); return; }
                    if (queue.Count == 128) { queue.Dequeue(); Interlocked.Increment(ref dropped); }
                    queue.Enqueue(new Item { Text = text, Error = error });
                    ready.Set();
                }
            }
            private void Dispatch()
            {
                try
                {
                    while (true)
                    {
                        Item item = null;
                        lock (gate)
                        {
                            if (suppress) return;
                            if (queue.Count > 0) item = queue.Dequeue();
                            else if (complete) return;
                        }
                        if (item == null) { ready.WaitOne(); continue; }
                        try { callback(item.Text, item.Error); } catch { }
                    }
                }
                finally { ready.Dispose(); }
            }
            public void Complete(int waitMilliseconds)
            {
                lock (gate)
                {
                    if (suppress) return;
                    complete = true;
                    try { ready.Set(); } catch (ObjectDisposedException) { }
                }
                if (worker.Wait(waitMilliseconds)) return;
                lock (gate)
                {
                    suppress = true;
                    Interlocked.Add(ref dropped, queue.Count);
                    queue.Clear();
                    try { ready.Set(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }
}
