using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WirelessInternet
{
    // Owns bounded console rendering only. Application operation state stays in
    // UiOperationCoordinator; controls and the 120 ms timer stay owned by the Form.
    internal sealed class ConsoleLogView : IDisposable
    {
        internal const int MaximumConsoleLines = 1500;
        internal const int MaximumConsoleCharacters = 200000;
        internal const int MaximumConsoleEntryCharacters = 4096;
        internal const int MaximumPendingLogEntries = 256;
        internal const int MaximumPendingLogCharacters = 65536;
        internal const int MaximumFlushEntries = 64;
        internal const int MaximumFlushCharacters = 12288;
        readonly RichTextBox console;
        readonly CheckBox autoScroll;
        readonly Func<bool> isClosing, canRender;
        readonly int uiThreadId;
        readonly object pendingGate = new object();
        readonly Queue<PendingLog> pendingLogs = new Queue<PendingLog>();
        readonly Queue<int> consoleLineLengths = new Queue<int>();
        readonly HashSet<string> seenLogs = new HashSet<string>(StringComparer.Ordinal);
        readonly Queue<string> seenLogOrder = new Queue<string>();
        int pendingCharacters, droppedCount, pendingDroppedToReport;
        int consoleLineCount, consoleCharacterCount;
        bool flushingLogs;
        volatile bool disposed;

        sealed class PendingLog
        {
            public string TimePrefix, SourcePrefix, Body;
            public int ColorIndex, Characters;
            public List<int> LineLengths;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct NativePoint { public int X, Y; }
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendWindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")]
        static extern IntPtr SendPointMessage(IntPtr window, int message, IntPtr wParam, ref NativePoint point);

        public ConsoleLogView(RichTextBox console, CheckBox autoScroll, Func<bool> isClosing, Func<bool> canRender)
        {
            if (console == null) throw new ArgumentNullException("console");
            if (autoScroll == null) throw new ArgumentNullException("autoScroll");
            if (isClosing == null) throw new ArgumentNullException("isClosing");
            if (canRender == null) throw new ArgumentNullException("canRender");
            this.console = console; this.autoScroll = autoScroll;
            this.isClosing = isClosing; this.canRender = canRender;
            uiThreadId = Thread.CurrentThread.ManagedThreadId;
        }
        internal int PendingCount { get { lock (pendingGate) return pendingLogs.Count; } }
        internal int PendingCharacters { get { lock (pendingGate) return pendingCharacters; } }
        internal int DroppedCount { get { lock (pendingGate) return droppedCount; } }
        internal int ConsoleLineCount { get { return consoleLineCount; } }
        internal int ConsoleCharacterCount { get { return consoleCharacterCount; } }
        internal int DedupCount { get { lock (pendingGate) return seenLogs.Count; } }
        internal ICollection DedupOrderSnapshot { get { lock (pendingGate) return seenLogOrder.ToArray(); } }

        internal void AppendUnique(string key, DateTime time, string source, string message, string level)
        {
            // Snapshot ingestion is UI-thread owned, just like Clear/Flush.
            // Arbitrary output producers use the separate managed-only Append.
            if (disposed) return;
            RequireUiThread();
            lock (pendingGate)
            {
                if (disposed || !seenLogs.Add(key)) return;
                seenLogOrder.Enqueue(key);
                while (seenLogOrder.Count > MaximumConsoleLines) seenLogs.Remove(seenLogOrder.Dequeue());
            }
            Append(time, source, message, level);
        }
        internal void Append(DateTime time, string source, string message, string level)
        {
            // Called from output workers: only managed data and the coordinator's
            // closing delegate are read here; no Control or native UI access.
            if (String.IsNullOrWhiteSpace(message) || disposed || isClosing()) return;
            PendingLog entry = CreatePendingLog(time, source, message, level);
            lock (pendingGate)
            {
                if (disposed || isClosing()) return;
                pendingLogs.Enqueue(entry);
                pendingCharacters += entry.Characters;
                while (pendingLogs.Count > MaximumPendingLogEntries || pendingCharacters > MaximumPendingLogCharacters)
                {
                    pendingCharacters -= pendingLogs.Dequeue().Characters;
                    if (droppedCount < Int32.MaxValue) droppedCount++;
                    if (pendingDroppedToReport < Int32.MaxValue) pendingDroppedToReport++;
                }
            }
        }
        static PendingLog CreatePendingLog(DateTime time, string source, string message, string level)
        {
            string sourceName = String.IsNullOrWhiteSpace(source) ? "日志" : source;
            if (sourceName.Length > 40) sourceName = sourceName.Substring(0, 40);
            sourceName = sourceName.Replace('\r', ' ').Replace('\n', ' ');
            string timePrefix = time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  ";
            string sourcePrefix = "[" + sourceName + "]  ";
            string body = LimitConsoleMessage(message, MaximumConsoleEntryCharacters - timePrefix.Length - sourcePrefix.Length - 1);
            string rendered = timePrefix + sourcePrefix + body + "\n";
            string normalized = level ?? String.Empty;
            if (normalized.Length > 64) normalized = normalized.Substring(0, 64);
            normalized = normalized.ToLowerInvariant();
            int color = normalized.Contains("error") || normalized.Contains("fatal") ? 4 : normalized.Contains("warn") ? 5 : normalized.Contains("success") ? 6 : 3;
            return new PendingLog { TimePrefix = timePrefix, SourcePrefix = sourcePrefix, Body = body,
                Characters = rendered.Length, LineLengths = GetConsoleLineLengths(rendered), ColorIndex = color };
        }
        internal void Flush() { FlushCore(false); }
        internal void FlushForTest() { FlushCore(true); }
        void RequireUiThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != uiThreadId)
                throw new InvalidOperationException("Console view changes must run on the UI thread.");
        }
        void FlushCore(bool force)
        {
            if (disposed || isClosing() || flushingLogs) return;
            RequireUiThread();
            if (console.IsDisposed || (!force && !canRender())) return;
            List<PendingLog> batch = new List<PendingLog>(MaximumFlushEntries);
            int batchCharacters = 0, batchLines = 0;
            lock (pendingGate)
            {
                if (pendingDroppedToReport > 0)
                {
                    PendingLog notice = CreatePendingLog(DateTime.Now, "显示", "刷新较快，已略过 " + pendingDroppedToReport.ToString(CultureInfo.InvariantCulture) + " 条较早消息，仅显示最近内容。", "Warning");
                    batch.Add(notice); batchCharacters = notice.Characters; batchLines = notice.LineLengths.Count;
                    pendingDroppedToReport = 0;
                }
                while (pendingLogs.Count > 0 && batch.Count < MaximumFlushEntries)
                {
                    PendingLog next = pendingLogs.Peek();
                    if (batchCharacters + next.Characters > MaximumFlushCharacters || batchLines + next.LineLengths.Count + 1 > MaximumConsoleLines) break;
                    pendingLogs.Dequeue(); pendingCharacters -= next.Characters;
                    batch.Add(next); batchCharacters += next.Characters; batchLines += next.LineLengths.Count;
                }
            }
            if (batch.Count == 0) return;
            flushingLogs = true;
            try { RenderLogBatch(batch, batchCharacters, batchLines); }
            finally { flushingLogs = false; }
        }
        void RenderLogBatch(List<PendingLog> batch, int batchCharacters, int batchLines)
        {
            if (consoleCharacterCount != console.TextLength) RebuildConsoleAccounting();
            int selectionStart = console.SelectionStart;
            int selectionLength = console.SelectionLength;
            bool scroll = autoScroll.Checked;
            NativePoint oldScroll = new NativePoint();
            int anchorCharacter = 0;
            Point anchorPosition = Point.Empty;
            if (!scroll)
            {
                SendPointMessage(console.Handle, 0x04DD, IntPtr.Zero, ref oldScroll);
                anchorCharacter = console.GetCharIndexFromPosition(new Point(1, 1));
                anchorPosition = console.GetPositionFromCharIndex(anchorCharacter);
            }
            int removedCharacters = 0;
            int remainingCharacters = consoleCharacterCount;
            while (consoleLineLengths.Count > 0 &&
                (remainingCharacters + batchCharacters > MaximumConsoleCharacters ||
                 consoleLineLengths.Count + batchLines + 1 > MaximumConsoleLines))
            {
                int oldLineLength = consoleLineLengths.Dequeue();
                remainingCharacters -= oldLineLength;
                removedCharacters += oldLineLength;
            }
            bool wasReadOnly = console.ReadOnly;
            bool redrawSuppressed = console.Visible && console.IsHandleCreated;
            if (redrawSuppressed) SendWindowMessage(console.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero);
            try
            {
                if (removedCharacters > 0)
                {
                    console.ReadOnly = false;
                    console.Select(0, removedCharacters);
                    console.SelectedText = String.Empty;
                    console.ReadOnly = wasReadOnly;
                    int selectionEnd = Math.Max(0, selectionStart + selectionLength - removedCharacters);
                    selectionStart = Math.Max(0, selectionStart - removedCharacters);
                    selectionLength = Math.Max(0, selectionEnd - selectionStart);
                }
                console.Select(console.TextLength, 0);
                console.SelectedRtf = BuildBatchRtf(batch);
                foreach (PendingLog entry in batch)
                    foreach (int length in entry.LineLengths) consoleLineLengths.Enqueue(length);
                consoleCharacterCount = console.TextLength;
                consoleLineCount = consoleLineLengths.Count == 0 ? 0 : consoleLineLengths.Count + 1;
                if (scroll) { console.Select(console.TextLength, 0); console.ScrollToCaret(); }
                else
                {
                    selectionStart = Math.Min(selectionStart, console.TextLength);
                    console.Select(selectionStart, Math.Min(selectionLength, console.TextLength - selectionStart));
                    NativePoint restore = oldScroll;
                    if (removedCharacters > 0)
                    {
                        if (anchorCharacter < removedCharacters) restore.Y = 0;
                        else
                        {
                            NativePoint current = new NativePoint();
                            SendPointMessage(console.Handle, 0x04DD, IntPtr.Zero, ref current);
                            Point nextAnchor = console.GetPositionFromCharIndex(Math.Min(console.TextLength, anchorCharacter - removedCharacters));
                            restore.Y = Math.Max(0, current.Y + nextAnchor.Y - anchorPosition.Y);
                        }
                    }
                    SendPointMessage(console.Handle, 0x04DE, IntPtr.Zero, ref restore);
                }
            }
            finally
            {
                console.ReadOnly = wasReadOnly;
                console.ClearUndo();
                if (redrawSuppressed) { SendWindowMessage(console.Handle, 0x000B, new IntPtr(1), IntPtr.Zero); console.Invalidate(); }
            }
        }
        static string BuildBatchRtf(List<PendingLog> batch)
        {
            StringBuilder rtf = new StringBuilder(16384);
            rtf.Append(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0\fmodern Consolas;}}{\colortbl;\red146\green166\blue186;\red113\green161\blue255;\red185\green205\blue221;\red255\green133\blue135;\red255\green200\blue103;\red48\green216\blue192;}\f0\fs18 ");
            foreach (PendingLog entry in batch)
            {
                rtf.Append(@"\cf1 "); AppendRtfText(rtf, entry.TimePrefix);
                rtf.Append(@"\cf2 "); AppendRtfText(rtf, entry.SourcePrefix);
                rtf.Append(@"\cf").Append(entry.ColorIndex).Append(' '); AppendRtfText(rtf, entry.Body);
                rtf.Append(@"\par ");
            }
            return rtf.Append('}').ToString();
        }
        static void AppendRtfText(StringBuilder rtf, string text)
        {
            foreach (char character in text)
            {
                if (character == '\\' || character == '{' || character == '}') rtf.Append('\\').Append(character);
                else if (character == '\n') rtf.Append(@"\par ");
                else if (character == '\t') rtf.Append(@"\tab ");
                else if (character < 32) rtf.Append(' ');
                else if (character > 126) rtf.Append(@"\u").Append(unchecked((short)character)).Append('?');
                else rtf.Append(character);
            }
        }
        static string LimitConsoleMessage(string message, int maximumLength)
        {
            bool truncated = message.Length > maximumLength;
            string text = (truncated ? message.Substring(0, maximumLength) : message).Replace("\r\n", "\n").Replace('\r', '\n');
            int breaks = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' && ++breaks >= MaximumConsoleLines - 1)
                { text = text.Substring(0, i); truncated = true; break; }
            }
            if (!truncated) return text;
            const string marker = " … [已截断]";
            int keep = Math.Min(text.Length, Math.Max(0, maximumLength - marker.Length));
            if (keep > 0 && Char.IsHighSurrogate(text[keep - 1])) keep--;
            return text.Substring(0, keep) + marker;
        }
        static List<int> GetConsoleLineLengths(string text)
        {
            List<int> lengths = new List<int>();
            int lineStart = 0;
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '\n') { lengths.Add(i - lineStart + 1); lineStart = i + 1; }
            if (lineStart < text.Length) lengths.Add(text.Length - lineStart);
            return lengths;
        }
        void RebuildConsoleAccounting()
        {
            consoleLineLengths.Clear();
            foreach (int length in GetConsoleLineLengths(console.Text)) consoleLineLengths.Enqueue(length);
            consoleCharacterCount = console.TextLength;
            consoleLineCount = consoleLineLengths.Count == 0 ? 0 : consoleLineLengths.Count + 1;
        }
        internal void Clear()
        {
            if (disposed) return;
            RequireUiThread();
            // Clear the pending prefix under the producer gate. Output enqueued
            // after this point stays pending for a subsequent UI flush.
            lock (pendingGate)
            {
                pendingLogs.Clear(); pendingCharacters = droppedCount = pendingDroppedToReport = 0;
                seenLogs.Clear(); seenLogOrder.Clear();
            }
            bool wasReadOnly = console.ReadOnly;
            try { console.ReadOnly = false; console.Clear(); console.ClearUndo(); }
            finally { console.ReadOnly = wasReadOnly; }
            consoleLineLengths.Clear(); consoleLineCount = consoleCharacterCount = 0;
        }
        public void Dispose()
        {
            lock (pendingGate)
            {
                if (disposed) return;
                disposed = true;
                pendingLogs.Clear(); pendingCharacters = pendingDroppedToReport = 0;
            }
        }
    }
}
