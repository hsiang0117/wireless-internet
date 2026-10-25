using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using WirelessInternet;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

// Independent console component checks. No Backend, SDK, process control,
// application operation coordinator or network access is used.
internal static class ConsoleLogViewChecks
{
    static readonly List<object> Results = new List<object>();
    static readonly DateTime FixedTime = new DateTime(2026, 10, 25, 9, 12, 34, DateTimeKind.Local);
    static void Check(bool value, string name)
    { Results.Add(new { Name = name, Passed = value }); if (!value) throw new Exception(name); }
    static void Pump(int milliseconds)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    static void Drain(ConsoleLogView view)
    {
        int count = 0;
        while (view.PendingCount > 0 && count++ < 100) view.FlushForTest();
        if (view.PendingCount != 0) throw new InvalidOperationException("Fixture queue did not drain within its bounded batches.");
    }
    static string FirstVisibleLine(RichTextBox box)
    {
        int character = box.GetCharIndexFromPosition(new Point(1, 1));
        int line = box.GetLineFromCharIndex(character);
        return box.Lines[Math.Min(line, box.Lines.Length - 1)];
    }
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        Control.CheckForIllegalCrossThreadCalls = true;
        string output = args.Length > 0 ? Path.GetFullPath(args[0]) : AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(output);
        string failure = null;
        try
        {
            bool closing = false, render = true;
            using (Form form = new Form { Text = "ConsoleLogView isolated fixture", Size = new Size(950, 480), StartPosition = FormStartPosition.Manual, Location = new Point(30, 30) })
            using (CheckBox scroll = new CheckBox { Checked = true })
            using (RichTextBox box = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, WordWrap = false,
                DetectUrls = false, HideSelection = false, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9F),
                BackColor = Color.FromArgb(20, 31, 45), ForeColor = Color.FromArgb(184, 204, 221) })
            using (ConsoleLogView view = new ConsoleLogView(box, scroll, delegate { return closing; }, delegate { return render; }))
            {
                Check(!box.IsHandleCreated, "fixture.no-control-handle-before-append");
                Task.Run(delegate { for (int i = 0; i < 80; i++) view.Append(FixedTime, "后台", "BEFORE-HANDLE-" + i, "Info"); }).GetAwaiter().GetResult();
                Check(view.PendingCount == 80 && !box.IsHandleCreated, "background.append-does-not-create-native-handle");
                Check(view.ConsoleCharacterCount == 0 && view.ConsoleLineCount == 0, "background.append-does-not-render");
                form.Controls.Add(box); form.Show(); Pump(70);
                int before = view.PendingCount; view.Flush();
                Check(before - view.PendingCount == 64 && view.PendingCount == 16, "batch.maximum-sixty-four-entries");
                Check(box.TextLength <= 12288 && view.ConsoleCharacterCount == box.TextLength, "batch.character-cap-and-accounting");
                Drain(view);
                Check(box.Text.Contains("BEFORE-HANDLE-79") && box.ReadOnly && !box.CanUndo, "batch.latest-readonly-no-undo");
                view.Clear();
                string unicode = "中文😀 {braces} \\path\\\nsecond\tline";
                view.Append(FixedTime, "源\r\n名", unicode, "Success"); view.Flush();
                Check(box.Text.Contains(unicode), "rtf.unicode-surrogate-braces-slash-multiline-tab");
                Check(box.Text.Contains("09:12:34  [源  名]"), "format.same-timestamp-and-source-normalization");
                Check(box.Rtf.Contains(@"\red48\green216\blue192"), "format.same-success-color");
                view.Clear(); view.Append(FixedTime, "测试", "a\ab\u0001c", "Error"); view.Flush();
                Check(!box.Text.Contains("\a") && !box.Text.Contains("\u0001") && box.Text.Contains("a b c"), "rtf.control-characters-cannot-trigger-bell");
                Check(box.Rtf.Contains(@"\red255\green133\blue135"), "format.same-error-color");
                view.Clear(); view.Append(FixedTime, "长消息", new string('长', 10000), "Warning"); view.Flush();
                Check(box.TextLength <= 4096 && box.Text.Contains("已截断"), "message.entry-cap");
                view.Clear();
                Task[] producers = new Task[4];
                for (int producer = 0; producer < producers.Length; producer++)
                {
                    int identity = producer;
                    producers[producer] = Task.Run(delegate { for (int i = 0; i < 400; i++) view.Append(FixedTime, "线程", "PRODUCER-" + identity + "-" + i + " " + new string('x', 140), "Info"); });
                }
                Task.WaitAll(producers);
                view.Append(FixedTime, "线程", "AFTER-PRODUCERS", "Info");
                Check(view.PendingCount <= 256 && view.PendingCharacters <= 65536, "parallel.pending-entry-and-character-limits");
                Check(view.DroppedCount > 0 && box.TextLength == 0, "parallel.drop-count-without-background-render");
                Drain(view);
                Check(box.Text.Contains("AFTER-PRODUCERS") && box.Text.Contains("已略过"), "parallel.latest-and-drop-notice");
                view.Clear();
                view.AppendUnique("same", FixedTime, "唯一", "UNIQUE", "Info");
                view.AppendUnique("same", FixedTime, "唯一", "DUPLICATE", "Info");
                Check(view.PendingCount == 1 && view.DedupCount == 1, "dedup.one-key-one-entry");
                Drain(view); Check(box.Text.Contains("UNIQUE") && !box.Text.Contains("DUPLICATE"), "dedup.first-record-retained");
                view.Clear();
                for (int i = 0; i < 1600; i++) view.AppendUnique("key-" + i, FixedTime, "唯一", "VALUE-" + i, "Info");
                Check(view.DedupCount == 1500 && view.DedupOrderSnapshot.Count == 1500, "dedup.bounded-history");
                view.Clear();
                render = false; view.Append(FixedTime, "暂停", "PAUSED", "Info"); view.Flush();
                Check(view.PendingCount == 1 && box.TextLength == 0, "pause.only-rendering-stops");
                render = true; view.Flush(); Check(box.Text.Contains("PAUSED"), "pause.resume-keeps-pending");
                view.Clear(); render = false; view.Append(FixedTime, "强制", "FORCE", "Info"); view.FlushForTest();
                Check(box.Text.Contains("FORCE"), "force-test-bypasses-visibility-only");
                render = true; view.Clear(); closing = true;
                Task.Run(delegate { view.Append(FixedTime, "关闭", "REJECTED", "Info"); }).GetAwaiter().GetResult();
                view.FlushForTest(); Check(view.PendingCount == 0 && box.TextLength == 0, "coordinator-closing-prevents-append-and-flush");
                closing = false;
                bool wrongThread = false;
                Task.Run(delegate { try { view.FlushForTest(); } catch (InvalidOperationException) { wrongThread = true; } }).GetAwaiter().GetResult();
                Check(wrongThread, "flush.requires-owning-ui-thread");
                bool uniqueWrongThread = false;
                Task.Run(delegate { try { view.AppendUnique("wrong-thread", FixedTime, "快照", "NO", "Info"); } catch (InvalidOperationException) { uniqueWrongThread = true; } }).GetAwaiter().GetResult();
                Check(uniqueWrongThread && view.DedupCount == 0 && view.PendingCount == 0, "snapshot-dedup.requires-owning-ui-thread-without-partial-enqueue");
                scroll.Checked = false;
                StringBuilder seed = new StringBuilder();
                for (int i = 0; i < 1500; i++) seed.Append("SEED-").Append(i.ToString("D4")).Append(' ').Append(new string('x', 45)).Append('\n');
                box.Text = seed.ToString();
                int selected = box.Text.IndexOf("SEED-1200", StringComparison.Ordinal); box.Select(selected, 9); box.ScrollToCaret();
                string selectedText = box.SelectedText, anchor = FirstVisibleLine(box);
                for (int i = 0; i < 64; i++) view.Append(FixedTime, "滚动", "AFTER-" + i, "Info");
                view.FlushForTest();
                Check(box.SelectedText == selectedText && box.SelectionStart < selected, "rollover.selection-anchor-retained");
                Check(FirstVisibleLine(box) == anchor, "rollover.reading-anchor-retained");
                Check(box.ReadOnly && !box.CanUndo && box.Lines.Length <= 1500, "rollover.readonly-line-cap-and-no-undo");
                scroll.Checked = true; view.Clear();
                for (int i = 0; i < 2100; i++)
                {
                    view.Append(FixedTime, "总限额", "LONG-" + i + " " + new string('x', 120), "Info");
                    if (i % 40 == 39) Drain(view);
                }
                Drain(view);
                Check(box.TextLength <= 200000 && box.Lines.Length <= 1500, "rollover.total-character-and-line-caps");
                Check(box.Text.Contains("LONG-2099") && !box.Text.Contains("LONG-0 "), "rollover.recent-messages-retained");
                view.Clear();
                Check(view.PendingCount == 0 && view.PendingCharacters == 0 && view.DroppedCount == 0 && view.DedupCount == 0,
                    "clear.queue-drop-and-dedup-state");
                Check(box.TextLength == 0 && view.ConsoleCharacterCount == 0 && view.ConsoleLineCount == 0 && box.ReadOnly && !box.CanUndo,
                    "clear.display-and-line-accounting");
                view.Append(FixedTime, "释放", "PENDING", "Info"); view.Dispose(); view.Dispose();
                Task.Run(delegate { view.Append(FixedTime, "释放", "NO-APPEND", "Info"); }).GetAwaiter().GetResult();
                view.FlushForTest(); view.Clear();
                Check(view.PendingCount == 0 && view.PendingCharacters == 0 && box.TextLength == 0, "dispose.idempotent-drops-pending-and-rejects-new-work");
                Check(!box.IsDisposed && !scroll.IsDisposed, "dispose.leaves-form-owned-controls-alive");
                form.Close();
            }
        }
        catch (Exception exception) { failure = exception.ToString(); }
        string report = new JavaScriptSerializer().Serialize(new { Passed = failure == null, Count = Results.Count, Failure = failure, Checks = Results });
        File.WriteAllText(Path.Combine(output, "console-log-view-results.json"), report, new UTF8Encoding(false));
        Console.WriteLine((failure == null ? "PASS " : "FAIL ") + Results.Count + " console log view checks" + (failure == null ? "" : "\n" + failure));
        return failure == null ? 0 : 1;
    }
}
