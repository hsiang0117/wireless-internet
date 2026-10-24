using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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
            string root = null, action = null, token = null;
            bool monitor = false;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--root" && i + 1 < args.Length && root == null) root = args[++i];
                    else if (args[i] == "--action" && i + 1 < args.Length && action == null) action = args[++i];
                    else if (args[i] == "--monitor" && i + 1 < args.Length && token == null) { token = args[++i]; monitor = true; }
                    else throw new ArgumentException("Invalid desktop worker arguments.");
                }
                if (root == null || (monitor ? action != null : action == null)) throw new ArgumentException("A worker mode and root are required.");
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
                ConnectionController controller = new ConnectionController(context);
                if (action == "Start") return controller.Start(Console.WriteLine);
                if (action == "Stop") return controller.Stop(Console.WriteLine);
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
}
