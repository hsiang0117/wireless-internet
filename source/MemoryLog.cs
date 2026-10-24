using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WirelessInternet
{
    public sealed class MemoryLogRecord
    {
        public long Sequence, UtcTicks;
        public string Source, Message;
    }
    public sealed class MemoryLogBatch
    {
        public long NextSequence, DroppedCount;
        public List<MemoryLogRecord> Entries = new List<MemoryLogRecord>();
    }
    // Both the relay and desktop keep small rolling buffers; neither writes log files.
    public sealed class MemoryLogBuffer
    {
        public const int MaxEntries = 1024, MaxCharacters = 131072, MaxMessage = 2048;
        readonly Queue<MemoryLogRecord> entries = new Queue<MemoryLogRecord>();
        long sequence;
        int characters;
        public void Add(string source, string message)
        {
            if (String.IsNullOrEmpty(message)) return;
            if (message.Length > MaxMessage) message = message.Substring(0, MaxMessage - 4) + " ...";
            lock (entries)
            {
                MemoryLogRecord entry = new MemoryLogRecord { Sequence = ++sequence, UtcTicks = DateTime.UtcNow.Ticks,
                    Source = source, Message = message };
                entries.Enqueue(entry); characters += message.Length;
                while (entries.Count > MaxEntries || characters > MaxCharacters) characters -= entries.Dequeue().Message.Length;
            }
        }
        public MemoryLogBatch Snapshot(long after)
        {
            lock (entries)
            {
                MemoryLogBatch result = new MemoryLogBatch { NextSequence = sequence };
                MemoryLogRecord[] all = entries.ToArray();
                int first = all.Length, size = 0;
                // Catch up to recent events in one bounded response, even after a long absence.
                for (int i = all.Length - 1; i >= 0 && all[i].Sequence > after; --i)
                {
                    if (all.Length - i > 128 || size + all[i].Message.Length > 24576) break;
                    first = i; size += all[i].Message.Length;
                }
                for (int i = first; i < all.Length; ++i) result.Entries.Add(all[i]);
                if (result.Entries.Count > 0) result.DroppedCount = Math.Max(0, result.Entries[0].Sequence - Math.Max(0, after) - 1);
                return result;
            }
        }
        public static void Pump(StreamReader reader, MemoryLogBuffer buffer, string source)
        {
            StringBuilder line = new StringBuilder(MaxMessage);
            bool truncated = false;
            int ch;
            // Read() uses StreamReader's fixed buffer. An unterminated line cannot grow memory.
            while ((ch = reader.Read()) >= 0)
            {
                if (ch == '\n')
                {
                    Publish(buffer, source, line, truncated); line.Length = 0; truncated = false;
                }
                else if (line.Length < MaxMessage) line.Append((char)ch);
                else truncated = true;
            }
            if (line.Length > 0 || truncated) Publish(buffer, source, line, truncated);
        }
        static void Publish(MemoryLogBuffer buffer, string source, StringBuilder line, bool truncated)
        {
            if (truncated) { line.Length = Math.Min(line.Length, MaxMessage - 4); line.Append(" ..."); }
            buffer.Add(source, line.ToString().TrimEnd('\r'));
        }
    }
    public static class MemoryLogWire
    {
        const int MaxPayload = 262144;
        public static bool ValidName(string name) { return Regex.IsMatch(name ?? "", "^WirelessInternet[.]Log[.][0-9a-f]{32}$"); }
        public static byte[] Read(Stream stream, int count, int timeout)
        {
            byte[] bytes = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                Task<int> task = stream.ReadAsync(bytes, offset, count - offset);
                if (!task.Wait(timeout)) throw new IOException("实时日志读取超时。");
                if (task.Result == 0) throw new EndOfStreamException();
                offset += task.Result;
            }
            return bytes;
        }
        public static void Write(Stream stream, byte[] bytes, int timeout)
        {
            Task task = stream.WriteAsync(bytes, 0, bytes.Length);
            if (!task.Wait(timeout)) throw new IOException("实时日志发送超时。");
        }
        public static MemoryLogBatch Fetch(string pipeName, long after)
        {
            if (!ValidName(pipeName)) throw new IOException("实时日志通道无效。");
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(250);
                Write(pipe, BitConverter.GetBytes(after), 500);
                int length = BitConverter.ToInt32(Read(pipe, 4, 500), 0);
                if (length <= 0 || length > MaxPayload) throw new IOException("实时日志响应过长。");
                MemoryLogBatch batch = new JavaScriptSerializer { MaxJsonLength = MaxPayload }.Deserialize<MemoryLogBatch>(Encoding.UTF8.GetString(Read(pipe, length, 500)));
                if (batch == null || batch.Entries == null || batch.Entries.Count > 128) throw new IOException("实时日志响应无效。");
                return batch;
            }
        }
    }
    public sealed class MemoryLogServer : IDisposable
    {
        readonly string name;
        readonly MemoryLogBuffer buffer;
        readonly object gate = new object();
        readonly Thread thread;
        NamedPipeServerStream active;
        bool stopped;
        public MemoryLogServer(string name, MemoryLogBuffer buffer)
        {
            if (!MemoryLogWire.ValidName(name)) throw new ArgumentException("Invalid pipe name.");
            this.name = name; this.buffer = buffer;
            thread = new Thread(Serve) { IsBackground = true, Name = "Memory log snapshots" }; thread.Start();
        }
        NamedPipeServerStream Open()
        {
            PipeSecurity security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        }
        void Serve()
        {
            for (;;)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    lock (gate) { if (stopped) return; active = pipe = Open(); }
                    pipe.WaitForConnection();
                    long after = BitConverter.ToInt64(MemoryLogWire.Read(pipe, 8, 500), 0);
                    if (after < 0) throw new IOException("Invalid cursor.");
                    byte[] payload = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(buffer.Snapshot(after)));
                    MemoryLogWire.Write(pipe, BitConverter.GetBytes(payload.Length), 500);
                    MemoryLogWire.Write(pipe, payload, 500);
                }
                catch { /* A missing or slow observer never interrupts the relay or its pumps. */ }
                finally
                {
                    lock (gate) { if (active == pipe) active = null; }
                    if (pipe != null) pipe.Dispose();
                }
                lock (gate) { if (stopped) return; }
                Thread.Sleep(10);
            }
        }
        public void Dispose()
        {
            lock (gate) { stopped = true; if (active != null) active.Dispose(); }
            thread.Join(1500);
        }
    }
}
