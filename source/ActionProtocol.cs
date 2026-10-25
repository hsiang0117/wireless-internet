using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

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

    // This bounded control channel carries state, independently of lossy console progress.
    internal sealed class ActionControlFrame
    {
        public int Version;
        public string OperationId, Action, Kind;
        public int Stage, ExitCode;
        public string Message;
        public int[] Stages;
        public bool Success;
    }

    internal static class ActionControlWire
    {
        public const int Version = 1;
        public const int MaximumFrameBytes = 16384;
        public const int MaximumMessageChars = 2048;
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        static readonly string[] Fields = { "Version", "OperationId", "Action", "Kind", "Stage", "ExitCode", "Message", "Stages", "Success" };

        public static string PipeName(string operationId)
        {
            ValidateOperation(operationId, "Start");
            return "WirelessInternet.Control." + operationId;
        }

        public static void ValidateOperation(string operationId, string action)
        {
            if (operationId == null || operationId.Length != 32) throw new IOException("Invalid control operation identifier.");
            foreach (char ch in operationId)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) throw new IOException("Invalid control operation identifier.");
            if (action != "Start" && action != "Stop") throw new IOException("Invalid control action.");
        }

        static void Validate(ActionControlFrame frame, string operationId, string action)
        {
            ValidateOperation(operationId, action);
            if (frame == null || frame.Version != Version || frame.OperationId != operationId || frame.Action != action)
                throw new IOException("Control frame does not match this operation.");
            if (!Enum.IsDefined(typeof(ConnectionStage), frame.Stage)) throw new IOException("Invalid control stage.");
            if (frame.Kind == "stage")
            {
                if (frame.ExitCode != 0 || frame.Success || !String.IsNullOrEmpty(frame.Message) || frame.Stages != null)
                    throw new IOException("Unexpected result fields in a stage frame.");
                return;
            }
            if (frame.Kind != "result" || frame.ExitCode < 0 || frame.Success != (frame.ExitCode == 0) ||
                frame.Message == null || frame.Message.Length > MaximumMessageChars || frame.Stages == null ||
                frame.Stages.Length == 0 || frame.Stages.Length > 64 || frame.Stages[frame.Stages.Length - 1] != frame.Stage ||
                frame.Success != (frame.Stage == (int)ConnectionStage.Completed))
                throw new IOException("Invalid control result.");
            foreach (int stage in frame.Stages)
                if (!Enum.IsDefined(typeof(ConnectionStage), stage)) throw new IOException("Invalid result stage history.");
        }

        public static byte[] Encode(ActionControlFrame frame)
        {
            if (frame == null) throw new IOException("Missing control frame.");
            Validate(frame, frame.OperationId, frame.Action);
            byte[] payload = Utf8.GetBytes(new JavaScriptSerializer().Serialize(frame));
            if (payload.Length == 0 || payload.Length > MaximumFrameBytes) throw new IOException("Control frame exceeds the size limit.");
            byte[] encoded = new byte[payload.Length + 4];
            int size = payload.Length;
            for (int i = 0; i < 4; i++) encoded[i] = (byte)(size >> (i * 8));
            Buffer.BlockCopy(payload, 0, encoded, 4, payload.Length);
            return encoded;
        }

        public static void Write(Stream stream, ActionControlFrame frame)
        {
            byte[] encoded = Encode(frame);
            stream.Write(encoded, 0, encoded.Length);
        }

        static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                int read = stream.Read(buffer, offset, count);
                if (read <= 0) throw new IOException("Truncated control frame.");
                offset += read; count -= read;
            }
        }

        public static ActionControlFrame Read(Stream stream, string operationId, string action)
        {
            ValidateOperation(operationId, action);
            byte[] prefix = new byte[4];
            int first = stream.ReadByte();
            if (first < 0) return null;
            prefix[0] = (byte)first;
            ReadExact(stream, prefix, 1, 3);
            int length = prefix[0] | prefix[1] << 8 | prefix[2] << 16 | prefix[3] << 24;
            if (length <= 0 || length > MaximumFrameBytes) throw new IOException("Invalid control frame length.");
            byte[] payload = new byte[length];
            ReadExact(stream, payload, 0, length);
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = MaximumFrameBytes;
                serializer.RecursionLimit = 8;
                string json = Utf8.GetString(payload);
                Dictionary<string, object> values = serializer.DeserializeObject(json) as Dictionary<string, object>;
                if (values == null || values.Count != Fields.Length) throw new IOException("Invalid control frame fields.");
                foreach (string field in Fields) if (!values.ContainsKey(field)) throw new IOException("Missing control frame field.");
                // Reject coercions such as a numeric string being accepted as an enum value.
                foreach (string field in new string[] { "Version", "Stage", "ExitCode" })
                    if (!(values[field] is int)) throw new IOException("Invalid control numeric field.");
                foreach (string field in new string[] { "OperationId", "Action", "Kind" })
                    if (!(values[field] is string)) throw new IOException("Invalid control text field.");
                if (!(values["Success"] is bool) || (values["Message"] != null && !(values["Message"] is string)))
                    throw new IOException("Invalid control result field.");
                object[] stages = values["Stages"] as object[];
                if (values["Stages"] != null && stages == null) throw new IOException("Invalid control history field.");
                if (stages != null) foreach (object stage in stages) if (!(stage is int)) throw new IOException("Invalid control history value.");
                ActionControlFrame frame = serializer.Deserialize<ActionControlFrame>(json);
                Validate(frame, operationId, action);
                return frame;
            }
            catch (IOException) { throw; }
            catch (Exception error) { throw new IOException("Invalid control frame encoding.", error); }
        }
    }

    public static class ConnectionStageText
    {
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
}
