using System;
using System.Collections.Generic;

namespace WirelessInternet
{
    public enum ConnectionOperationKind { Start, Stop }
    public enum ConnectionStage
    {
        AcquireOperation, ValidateConfiguration, ResolveDevice, VerifyDevice,
        AcquireRelay, PrepareProtection, CommitSession, EstablishTunnel,
        StartMonitor, AwaitProtection, VerifyOwnership, StopPhoneVpn,
        CancelPhoneGuard, StopMonitor, RemoveTunnel, StopRelay, RemoveSession,
        DisconnectDevice, ReleaseOperation, Completed
    }

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
        readonly List<ConnectionStage> stages = new List<ConnectionStage>();
        public ConnectionStage Current { get; private set; }
        public ConnectionOperationTrace(ConnectionOperationKind operation, Action<string> output)
        { this.operation = operation; this.output = output; }
        public void Move(ConnectionStage stage)
        {
            Current = stage; stages.Add(stage);
            if (output != null) try { output((operation == ConnectionOperationKind.Start ? "连接阶段 · " : "断开阶段 · ") + Describe(stage)); } catch { }
        }
        public ConnectionOperationResult Finish(int exitCode, string message)
        {
            if (exitCode == 0) Move(ConnectionStage.Completed);
            return new ConnectionOperationResult(operation, Current, exitCode, message, stages);
        }
        public static string Describe(ConnectionStage stage)
        {
            switch (stage)
            {
                case ConnectionStage.AcquireOperation: return "等待其他连接操作完成";
                case ConnectionStage.ValidateConfiguration: return "检查配置与会话";
                case ConnectionStage.ResolveDevice: return "发现手机无线端点";
                case ConnectionStage.VerifyDevice: return "连接并核验手机";
                case ConnectionStage.AcquireRelay: return "准备电脑中继";
                case ConnectionStage.PrepareProtection: return "准备手机失联保护";
                case ConnectionStage.CommitSession: return "保存共享状态";
                case ConnectionStage.EstablishTunnel: return "建立隧道与手机 VPN";
                case ConnectionStage.StartMonitor: return "启动电脑保护监控";
                case ConnectionStage.AwaitProtection: return "等待首次保护确认";
                case ConnectionStage.VerifyOwnership: return "检查共享进程";
                case ConnectionStage.StopPhoneVpn: return "停止手机 VPN";
                case ConnectionStage.CancelPhoneGuard: return "结束手机守护";
                case ConnectionStage.StopMonitor: return "停止电脑保护监控";
                case ConnectionStage.RemoveTunnel: return "移除本次隧道";
                case ConnectionStage.StopRelay: return "停止自有中继";
                case ConnectionStage.RemoveSession: return "清理本次会话记录";
                case ConnectionStage.DisconnectDevice: return "断开无线调试通道";
                case ConnectionStage.ReleaseOperation: return "结束本次操作";
                case ConnectionStage.Completed: return "完成";
                default: return stage.ToString();
            }
        }
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
