using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WirelessInternet
{
    internal static class WorkerHost
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int id);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetStdHandle(int id, IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
        static void DetachStarterPipes()
        {
            HashSet<IntPtr> handles = new HashSet<IntPtr>();
            foreach (int id in new int[] { -10, -11, -12 })
            {
                IntPtr handle = GetStdHandle(id); SetStdHandle(id, IntPtr.Zero);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1)) handles.Add(handle);
            }
            Console.SetIn(TextReader.Null); Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null);
            foreach (IntPtr handle in handles) CloseHandle(handle);
        }
        [STAThread] static int Main(string[] args)
        {
            string root = null, action = null, token = null, operationId = null;
            bool monitor = false;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--root" && i + 1 < args.Length && root == null) root = args[++i];
                    else if (args[i] == "--action" && i + 1 < args.Length && action == null) action = args[++i];
                    else if (args[i] == "--monitor" && i + 1 < args.Length && token == null) { token = args[++i]; monitor = true; }
                    else if (args[i] == "--control" && i + 1 < args.Length && operationId == null) operationId = args[++i];
                    else throw new ArgumentException("Invalid desktop worker arguments.");
                }
                if (root == null || (monitor ? action != null : action == null)) throw new ArgumentException("A worker mode and root are required.");
                if (operationId != null)
                {
                    if (monitor) throw new ArgumentException("Monitor mode does not accept a control channel.");
                    ActionControlWire.ValidateOperation(operationId, action);
                }
                if (monitor) DetachStarterPipes(); else Console.OutputEncoding = new UTF8Encoding(false);
                RuntimeContext context = new RuntimeContext(root);
                string actual = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ConnectionWorker.exe"));
                if (!String.Equals(actual, context.WorkerPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Worker executable and runtime directory do not match.");
                if (monitor)
                {
                    // Parent cannot know the child's PID until Start returns. Do not race its state commit.
                    using (Process self = Process.GetCurrentProcess())
                    {
                        string ticks = self.StartTime.ToUniversalTime().Ticks.ToString();
                        long started = context.Timestamp;
                        bool assigned = false;
                        while (context.ElapsedSeconds(started) < 5)
                        {
                            using (IDisposable lease = context.TryEnterCoreMutex())
                            {
                                if (lease != null)
                                {
                                    RuntimeState state = context.ReadState();
                                    if (state == null || state.ProtectionSessionId != token) return 0;
                                    if (state.WatchdogProcessId == self.Id && state.WatchdogStartTimeUtcTicks == ticks && state.RuntimeEngine == "CSharp" &&
                                        String.Equals(state.WatchdogPath, context.WorkerPath, StringComparison.OrdinalIgnoreCase)) { assigned = true; break; }
                                }
                            }
                            context.Sleep(50);
                        }
                        if (!assigned) return 1;
                    }
                    return new WatchdogController(context).Run(token);
                }
                if (operationId != null)
                {
                    // Establish control before acquiring a lock or touching a device. Once
                    // started, disappearance of the GUI must not interrupt a partial Stop.
                    using (WorkerActionWriter writer = WorkerActionWriter.Connect(operationId, action, 2000))
                    {
                        ConnectionController controlled = new ConnectionController(context, writer.PublishStage);
                        ConnectionOperationResult result;
                        if (action == "Start") result = controlled.StartWithResult(Console.WriteLine);
                        else if (action == "Stop") result = controlled.StopWithResult(Console.WriteLine);
                        else if (action == "RecoverStale") result = controlled.RecoverStaleWithResult(Console.WriteLine);
                        else throw new ArgumentException("Unknown controlled worker action.");
                        if (!writer.Complete(result, 2000))
                            try { Console.Error.WriteLine("Control result was not delivered; the connection action has finished."); } catch { }
                        return result.ExitCode;
                    }
                }
                ConnectionController controller = new ConnectionController(context);
                if (action == "Start") return controller.Start(Console.WriteLine);
                if (action == "Stop") return controller.Stop(Console.WriteLine);
                if (action == "RecoverStale") return controller.RecoverStale(Console.WriteLine);
                if (action == "Check") return controller.Check(Console.WriteLine);
                throw new ArgumentException("Unknown desktop worker action.");
            }
            catch (Exception error)
            {
                if (!monitor) { try { Console.OutputEncoding = new UTF8Encoding(false); Console.Error.WriteLine("ERROR: " + error.Message); } catch { } }
                return 1;
            }
        }
    }
    // Only the worker compiles this writer. PublishStage never performs pipe I/O.
    internal sealed class WorkerActionWriter : IDisposable
    {
        readonly object gate = new object();
        readonly string operationId, action;
        readonly NamedPipeClientStream pipe;
        readonly AutoResetEvent changed = new AutoResetEvent(false);
        readonly ManualResetEvent finalWritten = new ManualResetEvent(false);
        readonly Thread thread;
        ActionControlFrame latestStage, finalResult;
        bool finishing, closed, delivered;

        WorkerActionWriter(string operationId, string action, NamedPipeClientStream pipe)
        {
            this.operationId = operationId; this.action = action; this.pipe = pipe;
            thread = new Thread(SendLoop); thread.IsBackground = true; thread.Name = "Connection control writer"; thread.Start();
        }

        public static WorkerActionWriter Connect(string operationId, string action, int timeoutMilliseconds)
        {
            ActionControlWire.ValidateOperation(operationId, action);
            NamedPipeClientStream pipe = new NamedPipeClientStream(".", ActionControlWire.PipeName(operationId), PipeDirection.Out, PipeOptions.Asynchronous);
            try { pipe.Connect(timeoutMilliseconds); return new WorkerActionWriter(operationId, action, pipe); }
            catch { pipe.Dispose(); throw; }
        }

        ActionControlFrame Frame(string kind, ConnectionStage stage)
        {
            return new ActionControlFrame { Version = ActionControlWire.Version, OperationId = operationId, Action = action, Kind = kind, Stage = (int)stage };
        }

        public void PublishStage(ConnectionStage stage)
        {
            lock (gate)
            {
                if (closed || finishing) return;
                latestStage = Frame("stage", stage);
                changed.Set();
            }
        }

        public bool Complete(ConnectionOperationResult result, int timeoutMilliseconds)
        {
            try
            {
                if (result == null || result.Operation.ToString() != action) { ClosePipe(); return false; }
                ActionControlFrame frame = Frame("result", result.Stage);
                frame.ExitCode = result.ExitCode; frame.Success = result.Success;
                string message = result.Message ?? "";
                if (message.Length > ActionControlWire.MaximumMessageChars)
                {
                    int length = ActionControlWire.MaximumMessageChars;
                    if (Char.IsHighSurrogate(message[length - 1])) length--;
                    message = message.Substring(0, length);
                }
                frame.Message = message;
                frame.Stages = new int[result.Stages.Count];
                for (int i = 0; i < frame.Stages.Length; i++) frame.Stages[i] = (int)result.Stages[i];
                lock (gate)
                {
                    if (closed || finishing) return false;
                    finishing = true; latestStage = null; finalResult = frame; changed.Set();
                }
                if (!finalWritten.WaitOne(timeoutMilliseconds)) { ClosePipe(); return false; }
                lock (gate) return delivered;
            }
            catch { ClosePipe(); return false; }
        }

        void SendLoop()
        {
            try
            {
                while (true)
                {
                    changed.WaitOne();
                    ActionControlFrame frame;
                    lock (gate)
                    {
                        if (closed) return;
                        frame = finalResult ?? latestStage;
                        finalResult = null; latestStage = null;
                    }
                    if (frame == null) continue;
                    byte[] bytes = ActionControlWire.Encode(frame);
                    Task write = Task.Factory.FromAsync(pipe.BeginWrite, pipe.EndWrite, bytes, 0, bytes.Length, null);
                    // FromAsync owns EndWrite, including after timeout-triggered disposal.
                    // Observing late failure avoids abandoning an outstanding APM operation.
                    write.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    if (!write.Wait(1000)) throw new IOException("Control write timed out.");
                    if (frame.Kind == "result")
                    {
                        lock (gate) delivered = true;
                        return;
                    }
                }
            }
            catch { }
            finally { ClosePipe(); finalWritten.Set(); }
        }

        void ClosePipe()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
            }
            try { pipe.Dispose(); } catch { }
            changed.Set();
        }

        public void Dispose()
        {
            ClosePipe();
            // A non-reading GUI cannot leave an operation worker waiting indefinitely.
            if (Thread.CurrentThread != thread) thread.Join(1500);
        }
    }
}
