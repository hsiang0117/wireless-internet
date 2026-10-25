using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace WirelessInternet
{
    // A process exit code alone cannot confirm that an action reached its reported final stage.
    public sealed class WorkerActionResult
    {
        public readonly string Action, Message, ProtocolError;
        public readonly bool Verified, LogOutputIncomplete;
        public readonly int ExitCode, WorkerExitCode;
        public readonly long DroppedLogLines;
        public readonly ConnectionStage? FinalStage;
        public readonly IList<ConnectionStage> Stages;

        internal WorkerActionResult(string action, SharedCommandResult process, ActionControlFrame final, string error)
        {
            Action = action;
            WorkerExitCode = process.ExitCode;
            ProtocolError = error ?? String.Empty;
            Verified = final != null && ProtocolError.Length == 0;
            ExitCode = Verified ? process.ExitCode : (process.ExitCode == 0 ? 1 : process.ExitCode);
            Message = Verified ? final.Message : "连接操作结果未确认：" + ProtocolError;
            FinalStage = Verified ? (ConnectionStage?)final.Stage : null;
            List<ConnectionStage> stages = new List<ConnectionStage>();
            if (Verified) foreach (int stage in final.Stages) stages.Add((ConnectionStage)stage);
            Stages = Array.AsReadOnly(stages.ToArray());
            DroppedLogLines = process.DroppedProgressLines;
            LogOutputIncomplete = process.OutputIncomplete;
        }

        internal static WorkerActionResult Preview(string action)
        {
            return new WorkerActionResult(action, new SharedCommandResult { ExitCode = 1 }, null, "界面预览模式不执行连接操作。");
        }
    }

    internal sealed class WorkerActionClientOptions
    {
        internal int ConnectTimeoutMilliseconds = 5000;
        internal int FrameTimeoutMilliseconds = 120000;
        internal int CompletionDrainMilliseconds = 1000;
        internal IDictionary<string, string> Environment;
    }

    // Control frames use their own pipe. Stdout remains a bounded, lossy display channel.
    internal static class WorkerActionClient
    {
        static void ObserveFailure(Task pending)
        {
            pending.ContinueWith(delegate(Task failed) { GC.KeepAlive(failed.Exception); }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        internal static WorkerActionResult Run(string worker, string root, string action,
            Action<string> output, Action<ConnectionStage> stageChanged)
        {
            return Run(worker, root, action, output, stageChanged, new WorkerActionClientOptions());
        }

        internal static WorkerActionResult Run(string worker, string root, string action,
            Action<string> output, Action<ConnectionStage> stageChanged, WorkerActionClientOptions options)
        {
            if (output == null) throw new ArgumentNullException("output");
            if (options == null) throw new ArgumentNullException("options");
            if (options.ConnectTimeoutMilliseconds < 1 || options.FrameTimeoutMilliseconds < 1 ||
                options.CompletionDrainMilliseconds < 0) throw new ArgumentOutOfRangeException("options");
            string operationId = Guid.NewGuid().ToString("N");
            ActionControlWire.ValidateOperation(operationId, action);
            using (LatestStageDispatcher stages = new LatestStageDispatcher(stageChanged))
            using (ControlReader control = new ControlReader(operationId, action, options, stages))
            {
                // Individual worker commands have their own bounds. In particular, never kill a partial Stop.
                SharedCommandResult process = ProcessExecution.Run(worker,
                    new string[] { "--action", action, "--root", root, "--control", operationId },
                    new SharedCommandOptions {
                        WorkingDirectory = root, TimeoutMilliseconds = Timeout.Infinite,
                        Environment = options.Environment,
                        ProgressLine = delegate(string line, bool error) {
                            if (!String.IsNullOrWhiteSpace(line)) output(line);
                        }
                    });
                return control.Finish(process);
            }
        }

        private sealed class ControlReader : IDisposable
        {
            readonly object gate = new object();
            readonly string operationId, action;
            readonly WorkerActionClientOptions options;
            readonly LatestStageDispatcher stages;
            readonly NamedPipeServerStream pipe;
            readonly Task reader;
            readonly List<int> observedStages = new List<int>();
            ActionControlFrame final;
            string error;
            bool cleanEnd;

            internal ControlReader(string operationId, string action, WorkerActionClientOptions options, LatestStageDispatcher stages)
            {
                this.operationId = operationId; this.action = action; this.options = options; this.stages = stages;
                PipeSecurity security = new PipeSecurity();
                security.SetAccessRuleProtection(true, false);
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
                pipe = new NamedPipeServerStream(ActionControlWire.PipeName(operationId), PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, ActionControlWire.MaximumFrameBytes, 0,
                    security, HandleInheritability.None);
                reader = Task.Factory.StartNew(Read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            void Read()
            {
                try
                {
                    Task connection = Task.Factory.FromAsync(pipe.BeginWaitForConnection, pipe.EndWaitForConnection, null);
                    ObserveFailure(connection);
                    if (!connection.Wait(options.ConnectTimeoutMilliseconds))
                        throw new IOException("连接组件未建立结果通道。");
                    BoundedReadStream input = new BoundedReadStream(pipe, options.FrameTimeoutMilliseconds);
                    int frameCount = 0;
                    while (true)
                    {
                        input.BeginFrame();
                        ActionControlFrame frame = ActionControlWire.Read(input, operationId, action);
                        if (frame == null) { lock (gate) cleanEnd = true; return; }
                        if (++frameCount > 256) throw new IOException("结果通道包含过多控制帧。");
                        lock (gate)
                        {
                            if (final != null) throw new IOException("最终结果后出现了额外控制帧。");
                            if (frame.Kind == "result")
                            {
                                int next = 0;
                                foreach (int stage in observedStages)
                                {
                                    while (next < frame.Stages.Length && frame.Stages[next] != stage) next++;
                                    if (next == frame.Stages.Length) throw new IOException("最终结果与已报告阶段不一致。");
                                    next++;
                                }
                                final = frame;
                            }
                            else observedStages.Add(frame.Stage);
                        }
                        if (frame.Kind == "stage") stages.Publish((ConnectionStage)frame.Stage);
                    }
                }
                catch (Exception exception)
                {
                    lock (gate) { if (error == null) error = "结果通道无效：" + exception.Message; }
                }
                finally { ClosePipe(); }
            }

            internal WorkerActionResult Finish(SharedCommandResult process)
            {
                if (!reader.Wait(options.CompletionDrainMilliseconds))
                {
                    lock (gate) { if (error == null) error = "连接组件退出后，结果通道未及时关闭。"; }
                    ClosePipe();
                    reader.Wait(250);
                }
                lock (gate)
                {
                    if (error == null && (!cleanEnd || final == null)) error = "连接组件未返回完整的最终结果。";
                    if (error == null && final.ExitCode != process.ExitCode) error = "最终结果与连接组件退出码不一致。";
                    return new WorkerActionResult(action, process, final, error);
                }
            }

            void ClosePipe() { try { pipe.Dispose(); } catch (IOException) { } }
            public void Dispose() { ClosePipe(); }
        }

        // The deadline covers a complete frame, so a partial sender cannot extend it byte by byte.
        private sealed class BoundedReadStream : Stream
        {
            readonly Stream stream;
            readonly int timeout;
            Stopwatch frame;
            internal BoundedReadStream(Stream stream, int timeout) { this.stream = stream; this.timeout = timeout; }
            internal void BeginFrame() { frame = Stopwatch.StartNew(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int remaining = timeout - (int)Math.Min(Int32.MaxValue, frame.ElapsedMilliseconds);
                if (remaining <= 0) throw new IOException("读取控制帧超时。");
                Task<int> read = Task<int>.Factory.FromAsync(
                    delegate(AsyncCallback callback, object state) { return stream.BeginRead(buffer, offset, count, callback, state); },
                    stream.EndRead, null);
                ObserveFailure(read);
                if (!read.Wait(remaining)) throw new IOException("读取控制帧超时。");
                return read.Result;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        // Only one callback can run; newly arriving stages replace its pending value.
        private sealed class LatestStageDispatcher : IDisposable
        {
            readonly object gate = new object();
            readonly Action<ConnectionStage> callback;
            ConnectionStage latest;
            bool pending, running, closed;
            internal LatestStageDispatcher(Action<ConnectionStage> callback) { this.callback = callback; }
            internal void Publish(ConnectionStage stage)
            {
                if (callback == null) return;
                lock (gate)
                {
                    if (closed) return;
                    latest = stage; pending = true;
                    if (running) return;
                    running = true;
                    ThreadPool.QueueUserWorkItem(delegate { Pump(); });
                }
            }
            void Pump()
            {
                while (true)
                {
                    ConnectionStage stage;
                    lock (gate)
                    {
                        if (closed || !pending) { running = false; return; }
                        stage = latest; pending = false;
                    }
                    try { callback(stage); } catch { /* A display callback cannot invalidate a worker result. */ }
                }
            }
            public void Dispose() { lock (gate) { closed = true; pending = false; } }
        }
    }
}
