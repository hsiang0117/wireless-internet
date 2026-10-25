using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using WirelessInternet;

internal static class ActionClientFixture
{
    static void Raw(Stream stream, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        byte[] prefix = BitConverter.GetBytes(bytes.Length);
        stream.Write(prefix, 0, prefix.Length); stream.Write(bytes, 0, bytes.Length);
    }
    static void Raw(Stream stream, ActionControlFrame frame) { Raw(stream, new JavaScriptSerializer().Serialize(frame)); }
    static int Main(string[] args)
    {
        string action = null, operation = null, root = null;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--action") action = args[i + 1];
            if (args[i] == "--control") operation = args[i + 1];
            if (args[i] == "--root") root = args[i + 1];
        }
        string mode = Environment.GetEnvironmentVariable("WIRELESS_ACTION_FIXTURE") ?? "success";
        if (mode == "no-channel") return 0;
        using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", ActionControlWire.PipeName(operation), PipeDirection.Out))
        {
            pipe.Connect(2000);
            ConnectionStage first = action == "Stop" ? ConnectionStage.StopPhoneVpn : ConnectionStage.AcquireOperation;
            ActionControlFrame stage = new ActionControlFrame {
                Version = ActionControlWire.Version, OperationId = operation, Action = action,
                Kind = "stage", Stage = (int)first
            };
            ActionControlFrame final = new ActionControlFrame {
                Version = ActionControlWire.Version, OperationId = operation, Action = action,
                Kind = "result", Stage = (int)ConnectionStage.Completed, ExitCode = 0,
                Message = "fixture complete", Success = true, Stages = new int[] { (int)first, (int)ConnectionStage.Completed }
            };
            if (mode == "wrong-operation") { final.OperationId = new string('0', 32); Raw(pipe, final); return 0; }
            if (mode == "wrong-action") { final.Action = action == "Start" ? "Stop" : "Start"; Raw(pipe, final); return 0; }
            if (mode == "wrong-version") { final.Version++; Raw(pipe, final); return 0; }
            if (mode == "malformed") { Raw(pipe, "{"); return 0; }
            if (mode == "oversized") { byte[] size = BitConverter.GetBytes(ActionControlWire.MaximumFrameBytes + 1); pipe.Write(size, 0, 4); return 0; }
            if (mode == "truncated") { byte[] size = BitConverter.GetBytes(100); pipe.Write(size, 0, 4); pipe.WriteByte(123); return 0; }
            ActionControlWire.Write(pipe, stage);
            if (mode == "no-final") return 0;
            if (mode == "timeout-stop")
            {
                pipe.WriteByte(100); pipe.Flush();
                Thread.Sleep(400);
                File.WriteAllText(Path.Combine(root, "stop-completed.marker"), "finished");
                return 0;
            }
            if (mode == "failure")
            {
                final.Stage = (int)first; final.Stages = new int[] { (int)first };
                final.ExitCode = 7; final.Success = false; final.Message = "fixture failed at stage";
            }
            if (mode == "flood")
                for (int i = 0; i < 10000; i++) Console.WriteLine("display-only " + i + " " + new string('x', 180));
            if (mode == "slow-callbacks")
            {
                Console.WriteLine("display callback");
                Thread.Sleep(75);
            }
            ActionControlWire.Write(pipe, final);
            if (mode == "duplicate") ActionControlWire.Write(pipe, final);
            if (mode == "exit-mismatch") return 9;
            return final.ExitCode;
        }
    }
}
