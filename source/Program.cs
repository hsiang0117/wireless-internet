using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("无线网络共享")]
[assembly: System.Reflection.AssemblyProduct("无线网络共享")]
[assembly: System.Reflection.AssemblyDescription("Android 原生无线调试 · gnirehtet 控制与流量监控")]
[assembly: System.Reflection.AssemblyVersion("1.4.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.4.1.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace WirelessInternet
{
    internal static class Program
    {
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SetCurrentProcessExplicitAppUserModelID(string id);
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string capture = null, probe = null;
            bool preview = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--preview") preview = true;
                else if (args[i] == "--root" && i + 1 < args.Length) root = Path.GetFullPath(args[++i]);
                else if (args[i] == "--capture" && i + 1 < args.Length) capture = Path.GetFullPath(args[++i]);
                else if (args[i] == "--probe" && i + 1 < args.Length) probe = Path.GetFullPath(args[++i]);
            }
            Mutex releaseMarker = null;
            try
            {
                // Inno Setup checks object existence; ownership is intentionally unnecessary.
                releaseMarker = new Mutex(false, "Local\\WirelessInternetDesktopRelease");
                if (probe != null)
                {
                    using (Backend backend = new Backend(root, preview))
                    {
                        Snapshot first = backend.Poll();
                        Thread.Sleep(1100);
                        Snapshot second = backend.Poll();
                        var result = new { First = first, Second = second };
                        File.WriteAllText(probe, new JavaScriptSerializer().Serialize(result), new UTF8Encoding(false));
                        return second.Healthy ? 0 : 1;
                    }
                }
                string key;
                using (SHA256 hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(root.ToLowerInvariant()))).Replace("-", "").Substring(0, 20);
                bool created;
                string instanceName = "Local\\WirelessInternetDesktop_" + key + (preview ? "_preview" : "");
                using (Mutex mutex = new Mutex(true, instanceName, out created))
                using (EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "_show"))
                {
                    if (!created)
                    {
                        using (Process self = Process.GetCurrentProcess())
                        foreach (Process p in Process.GetProcessesByName(self.ProcessName))
                        using (p)
                        {
                            try
                            {
                                if (p.Id != self.Id &&
                                    String.Equals(p.MainModule.FileName, self.MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                                {
                                    AllowSetForegroundWindow(p.Id);
                                    break;
                                }
                            }
                            catch { }
                        }
                        showEvent.Set();
                        return 0;
                    }
                    SetCurrentProcessExplicitAppUserModelID("Local.WirelessInternet.Desktop");
                    using (AppForm form = new AppForm(root, preview))
                    {
                        form.AutomaticSetupEnabled = !preview && capture == null && probe == null;
                        string icon = Path.Combine(root, "support", "wireless.ico");
                        if (File.Exists(icon)) form.Icon = new Icon(icon);
                        RegisteredWaitHandle activation = null;
                        form.Shown += delegate {
                            activation = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate(object state, bool timedOut) {
                                try
                                {
                                    if (!form.IsDisposed && form.IsHandleCreated)
                                        form.BeginInvoke((Action)delegate { if (!form.IsDisposed) form.RestoreFromTray(); });
                                }
                                catch (InvalidOperationException) { }
                            }, null, Timeout.Infinite, false);
                        };
                        System.Windows.Forms.Timer captureTimer = null;
                        if (capture != null)
                        {
                            captureTimer = new System.Windows.Forms.Timer { Interval = preview ? 6000 : 5000 };
                            captureTimer.Tick += delegate {
                                captureTimer.Stop(); form.Capture(capture); form.CloseObserverAfterCapture();
                            };
                            form.Shown += delegate { captureTimer.Start(); };
                        }
                        try { Application.Run(form); }
                        finally
                        {
                            if (activation != null) activation.Unregister(null);
                            if (captureTimer != null) captureTimer.Dispose();
                        }
                    }
                    mutex.ReleaseMutex();
                }
                return 0;
            }
            catch (Exception e)
            {
                if (probe == null) MessageBox.Show("界面启动失败：" + e.Message + "\n后台共享与失联保护不受界面退出影响。", "无线网络共享", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
            finally { if (releaseMarker != null) releaseMarker.Dispose(); }
        }
    }
}
