using System;
using System.Collections.Generic;

namespace WirelessInternet
{
    // Describes this completed attempt, not the mutable live sharing state.
    public sealed class ConnectionOperationResult
    {
        public readonly ConnectionOperationKind Operation;
        public readonly ConnectionStage Stage;
        public readonly int ExitCode;
        public readonly string Message;
        public readonly IList<ConnectionStage> Stages;
        public bool Success { get { return ExitCode == 0; } }
        internal ConnectionOperationResult(ConnectionOperationKind operation, ConnectionStage stage,
            int exitCode, string message, List<ConnectionStage> stages)
        {
            Operation = operation; Stage = stage; ExitCode = exitCode; Message = message ?? "";
            Stages = Array.AsReadOnly(stages.ToArray());
        }
    }

    internal sealed class ConnectionOperationTrace
    {
        readonly ConnectionOperationKind operation;
        readonly Action<string> output;
        readonly Action<ConnectionStage> stageChanged;
        readonly List<ConnectionStage> stages = new List<ConnectionStage>();
        public ConnectionStage Current { get; private set; }
        public ConnectionOperationTrace(ConnectionOperationKind operation, Action<string> output)
            : this(operation, output, null) { }
        public ConnectionOperationTrace(ConnectionOperationKind operation, Action<string> output, Action<ConnectionStage> stageChanged)
        { this.operation = operation; this.output = output; this.stageChanged = stageChanged; }
        public void Move(ConnectionStage stage)
        {
            Current = stage; stages.Add(stage);
            if (stageChanged != null) try { stageChanged(stage); } catch { }
            if (output != null) try { output((operation == ConnectionOperationKind.Start ? "连接阶段 · " :
                operation == ConnectionOperationKind.RecoverStale ? "启动检查 · " : "断开阶段 · ") + Describe(stage)); } catch { }
        }
        public ConnectionOperationResult Finish(int exitCode, string message)
        {
            if (exitCode == 0) Move(ConnectionStage.Completed);
            return new ConnectionOperationResult(operation, Current, exitCode, message, stages);
        }
        public static string Describe(ConnectionStage stage) { return ConnectionStageText.Describe(stage); }
    }

    // Only resources created/committed by the current Start may enter its cleanup.
    // A committed relay deliberately remains recorded for a normal Stop recovery.
    internal sealed class ConnectionStartAttempt
    {
        public RuntimeState State, NewRelay;
        public bool CreatedRelay, StateSaved, NewGuard, ReuseProtection;
        public string ReadyPath;
    }
}
