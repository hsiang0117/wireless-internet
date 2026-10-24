using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using WirelessInternet;

// Session-only child host. Closing the desktop window never closes this process.
// The original gnirehtet process remains the port owner tracked by the existing guard.
internal static class RelayLogHost
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetStdHandle(int id, IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    static void DetachStarterPipes()
    {
        // A long-lived host must not retain the short-lived GUI action's inherited output pipes.
        HashSet<IntPtr> closed = new HashSet<IntPtr>();
        foreach (int id in new int[] { -10, -11, -12 })
        {
            IntPtr handle = GetStdHandle(id);
            SetStdHandle(id, IntPtr.Zero);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1) && closed.Add(handle)) CloseHandle(handle);
        }
    }
    [STAThread]
    static int Main(string[] args)
    {
        DetachStarterPipes();
        int starterId; long starterTicks;
        if (args.Length != 3 || !Regex.IsMatch(args[0], "^[0-9a-f]{32}$") || !Int32.TryParse(args[1], out starterId) ||
            !Int64.TryParse(args[2], out starterTicks)) return 2;
        string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
        string token = args[0];
        string ready = Path.Combine(root, "state", "relay-ready-" + token + ".json");
        string pipeName = "WirelessInternet.Log." + token;
        string exe = Path.Combine(root, "gnirehtet-rust-win64", "gnirehtet.exe");
        Process child = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ready));
            MemoryLogBuffer buffer = new MemoryLogBuffer();
            using (MemoryLogServer server = new MemoryLogServer(pipeName, buffer))
            {
                child = new Process();
                child.StartInfo = new ProcessStartInfo(exe, "relay") { WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                child.Start();
                child.StandardInput.Close();
                Process ownedChild = child;
                Task stdout = Task.Factory.StartNew(delegate { MemoryLogBuffer.Pump(ownedChild.StandardOutput, buffer, "中继"); }, TaskCreationOptions.LongRunning);
                Task stderr = Task.Factory.StartNew(delegate { MemoryLogBuffer.Pump(ownedChild.StandardError, buffer, "中继错误"); }, TaskCreationOptions.LongRunning);
                using (Process host = Process.GetCurrentProcess())
                {
                    var record = new { RelayProcessId = child.Id, RelayStartTimeUtcTicks = child.StartTime.ToUniversalTime().Ticks.ToString(),
                        RelayPath = exe, RelayLogPipe = pipeName, LogMode = "MemoryOnly",
                        RelayHostProcessId = host.Id, RelayHostStartTimeUtcTicks = host.StartTime.ToUniversalTime().Ticks.ToString() };
                    File.WriteAllText(ready + ".tmp", new JavaScriptSerializer().Serialize(record), new UTF8Encoding(false));
                    File.Move(ready + ".tmp", ready);
                }
                // If the starter crashes before recording this PID, remove this otherwise untracked relay.
                Stopwatch deadline = Stopwatch.StartNew();
                while (!File.Exists(ready + ".accepted"))
                {
                    if (child.HasExited) return child.ExitCode;
                    bool starterAlive = false;
                    try { using (Process starter = Process.GetProcessById(starterId)) starterAlive = !starter.HasExited && starter.StartTime.ToUniversalTime().Ticks == starterTicks; } catch { }
                    if (!starterAlive) throw new IOException("Starter exited before persisting relay ownership.");
                    if (deadline.Elapsed.TotalSeconds > 60) throw new IOException("Starter did not accept the relay process within 60 seconds.");
                    Thread.Sleep(100);
                }
                File.Delete(ready + ".accepted");
                while (!child.WaitForExit(500))
                    if (stdout.IsFaulted || stderr.IsFaulted) throw new IOException("The relay log stream failed.");
                Task.WaitAll(new Task[] { stdout, stderr }, 2000);
                return child.ExitCode;
            }
        }
        catch
        {
            // This object still holds the exact child created above; never target a shared process by name.
            try { if (child != null && !child.HasExited) { child.Kill(); child.WaitForExit(1000); } } catch { }
            return 1;
        }
        finally
        {
            if (child != null) child.Dispose();
            try { File.Delete(ready); File.Delete(ready + ".tmp"); File.Delete(ready + ".accepted"); } catch { }
        }
    }
}
