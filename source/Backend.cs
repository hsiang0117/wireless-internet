using System;
using System.IO;
using System.Threading;

namespace WirelessInternet
{
    // Compatibility facade: commands retain their own execution boundary;
    // observations/statistics are composed by the monitor assembler.
    public sealed class Backend : IDisposable
    {
        readonly string root;
        readonly bool preview;
        readonly MonitorSnapshotAssembler monitor;
        long actionGeneration;
        public Backend(string root, bool preview) : this(root, preview, null) { }
        public Backend(string root, bool preview, ISessionObservationSource observations) : this(root, preview, observations, new PhoneSamplingPolicy()) { }
        public Backend(string root, bool preview, ISessionObservationSource observations, PhoneSamplingPolicy samplingPolicy)
        {
            if (samplingPolicy == null) throw new ArgumentNullException("samplingPolicy");
            this.root = Path.GetFullPath(root);
            this.preview = preview;
            monitor = new MonitorSnapshotAssembler(preview, observations ?? new SystemSessionObservationSource(this.root), samplingPolicy);
        }
        public static string Quote(string value) { return ProcessExecution.Quote(value); }
        public static CommandResult RunBounded(string exe, string arguments, string workingDirectory, int timeout)
        { return SystemSessionObservationSource.RunBounded(exe, arguments, workingDirectory, timeout); }
        public static SessionRecord ReadState(string path) { return SystemSessionObservationSource.ReadState(path); }
        public static PhoneCounters ParsePhone(string text, string identity) { return SystemSessionObservationSource.ParsePhone(text, identity); }
        public Snapshot Poll() { return monitor.Poll(PhoneSamplingMode.Immediate); }
        public Snapshot Poll(PhoneSamplingMode mode) { return monitor.Poll(mode); }
        public int RunAction(string action, Action<string> output)
        {
            return RunActionWithResult(action, output, null).ExitCode;
        }
        public WorkerActionResult RunActionWithResult(string action, Action<string> output, Action<ConnectionStage> stageChanged)
        {
            if (output == null) throw new ArgumentNullException("output");
            if (action != "Start" && action != "Stop") throw new ArgumentException("Unknown action.");
            return RunWorker(action, output, stageChanged);
        }
        public WorkerActionResult RecoverStaleSession(Action<string> output)
        {
            if (output == null) throw new ArgumentNullException("output");
            return RunWorker("RecoverStale", output, null);
        }
        WorkerActionResult RunWorker(string action, Action<string> output, Action<ConnectionStage> stageChanged)
        {
            if (preview) { output("界面预览模式不执行连接操作。"); return WorkerActionResult.Preview(action); }
            string worker = Path.Combine(root, "support", "ConnectionWorker.exe");
            if (!File.Exists(worker)) throw new IOException("桌面连接组件缺失，请重新安装完整工具包。");
            long generation = Interlocked.Increment(ref actionGeneration);
            int active = 1;
            try
            {
                return WorkerActionClient.Run(worker, root, action,
                    delegate(string line) {
                        if (Thread.VolatileRead(ref active) != 0 && Interlocked.Read(ref actionGeneration) == generation) output(line);
                    },
                    delegate(ConnectionStage stage) {
                        if (stageChanged != null && Thread.VolatileRead(ref active) != 0 && Interlocked.Read(ref actionGeneration) == generation) stageChanged(stage);
                    });
            }
            finally { Interlocked.Exchange(ref active, 0); }
        }
        public void Dispose() { /* Closing the GUI never stops relay, monitor, or shared ADB. */ }
    }
}
