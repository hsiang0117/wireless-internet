using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using WirelessInternet;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

internal static class PhoneSamplingChecks
{
    static readonly List<object> Results = new List<object>();
    sealed class Source : ISessionObservationSource
    {
        public SessionObservation Current = new SessionObservation { State = new SessionRecord { ProtectionSessionId = new string('a', 32), PhoneIdentity = "FAKE_PHONE", WirelessSerial = "192.0.2.1:37123", ProtectionTimeoutSeconds = 30, RelayLogPipe = "WirelessInternet.Log." + new string('a',32) }, RelayOwned = true, MonitorOwned = true, HeartbeatAgeSeconds = 1 };
        public long Now;
        public int PhoneCalls, StateCalls, LogCalls;
        public bool FailPhone, BlockPhone;
        public Action DuringPhone;
        public readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
        public string VpnKey = "vpn-one";
        public SessionObservation ReadSession() { Interlocked.Increment(ref StateCalls); return Current; }
        public List<ConnectionInfo> ReadConnections(SessionRecord state) { return new List<ConnectionInfo>(); }
        public PhoneCounters ReadPhone(SessionRecord state)
        {
            Interlocked.Increment(ref PhoneCalls);
            if (BlockPhone) { BlockPhone = false; Entered.Set(); if (!Release.WaitOne(4000)) throw new IOException("fixture barrier timeout"); }
            if (DuringPhone != null) DuringPhone();
            if (FailPhone) throw new IOException("fixture sample failure");
            double seconds = Now / (double)Stopwatch.Frequency;
            return new PhoneCounters { Key = VpnKey, Interface = "tun0", Download = (ulong)(1000 + Math.Max(0,seconds) * 300), Upload = (ulong)(100 + Math.Max(0,seconds) * 30), Uptime = 100 + Math.Max(0,seconds) };
        }
        public MemoryLogBatch ReadLogs(string pipe, long after) { LogCalls++; return new MemoryLogBatch(); }
        public long Timestamp { get { return Interlocked.Read(ref Now); } }
        public void At(double seconds) { Interlocked.Exchange(ref Now, (long)(seconds * Stopwatch.Frequency)); }
    }
    static void Check(bool value, string name)
    { Results.Add(new { Name = name, Passed = value }); if (!value) throw new InvalidOperationException(name); }
    [STAThread]
    static int Main(string[] args)
    {
        string directory=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"phone-sampling-evidence"));Directory.CreateDirectory(directory);
        string root=Path.Combine(directory,"sampling-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root,"wireless-config.json"),"{\"Transport\":\"NativeTls\",\"PhoneIdentity\":\"FAKE_PHONE\"}");
        string failure=null;
        try
        {
            PhoneSamplingPolicy policy=new PhoneSamplingPolicy();
            Check(policy.RateIsFresh(4) && !policy.RateIsFresh(4.001) && !policy.RateIsFresh(double.NaN),"freshness.explicit-four-second-window");
            Source source=new Source(); Backend backend=new Backend(root,false,source);
            Snapshot s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==1 && s.IsNewTelemetrySample && double.IsNaN(s.DownloadBytesPerSecond),"first-at-zero.clock-establishes-baseline");
            source.At(1);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==1 && !s.IsNewTelemetrySample && s.TelemetryState==TelemetryState.Current && s.SamplingOk,"throttle.normal-not-a-failure");
            source.At(2.99);backend.Poll(PhoneSamplingMode.Foreground);Check(source.PhoneCalls==1,"interval.no-early-query");
            source.At(3);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==2 && s.IsNewTelemetrySample && s.DownloadBytesPerSecond==300 && s.UploadBytesPerSecond==30,"interval.three-seconds-new-average-both-directions");
            source.At(4);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==2 && !s.IsNewTelemetrySample && s.DownloadBytesPerSecond==300 && s.TelemetryAgeSeconds==1,"cache.recent-rate-with-age-no-new-point");
            int logs=source.LogCalls;s=backend.Poll(PhoneSamplingMode.Paused);
            Check(source.PhoneCalls==2 && source.LogCalls==logs+1 && s.Healthy,"pause.no-phone-but-local-status-logs-continue");
            Check(!s.SamplingOk && double.IsNaN(s.DownloadBytesPerSecond) && s.TrafficAvailable && s.TrafficNote.Contains("已暂停"),"pause.retains-total-not-current-speed");
            source.At(30);backend.Poll(PhoneSamplingMode.Paused);Check(source.PhoneCalls==2,"pause.long-hidden-no-query");
            s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==3 && s.IsNewTelemetrySample && double.IsNaN(s.DownloadBytesPerSecond),"resume.forces-query-and-new-baseline");
            Check(!s.TrafficNote.Contains("暂不可用") && !s.Logs.Exists(x=>x.Message.Contains("接口或计数已重建")),"resume.not-misreported-as-failure-or-vpn-rebuild");
            source.At(33);s=backend.Poll(PhoneSamplingMode.Foreground);Check(s.DownloadBytesPerSecond==300,"resume.next-interval-restores-speed");
            source.FailPhone=true;source.At(36);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==5 && s.Healthy && !s.SamplingOk && s.TelemetryState==TelemetryState.Stale,"failure.only-telemetry-degraded");
            source.At(37);s=backend.Poll(PhoneSamplingMode.Foreground);Check(source.PhoneCalls==5 && !s.IsNewTelemetrySample && !s.SamplingOk,"failure.retry-is-throttled");
            source.FailPhone=false;source.At(39);s=backend.Poll(PhoneSamplingMode.Foreground);Check(source.PhoneCalls==6 && double.IsNaN(s.DownloadBytesPerSecond) && !s.TrafficNote.Contains("暂不可用"),"failure.recovery-resets-baseline-and-note");
            source.At(42);s=backend.Poll(PhoneSamplingMode.Foreground);Check(s.DownloadBytesPerSecond==300,"failure.next-good-sample-speed");
            source.Current.State.WirelessSerial="192.0.2.1:37124";source.At(43);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==8 && s.IsNewTelemetrySample && double.IsNaN(s.DownloadBytesPerSecond),"endpoint.change-never-reuses-rate-cache");
            source.Current.State.ProtectionSessionId=new string('b',32);source.FailPhone=true;source.At(44);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(!s.TrafficAvailable && s.DownloadBytes==0 && source.PhoneCalls==9,"session.change-never-reuses-old-totals");
            source.FailPhone=false;source.At(47);backend.Poll(PhoneSamplingMode.Foreground);source.At(50);backend.Poll(PhoneSamplingMode.Foreground);
            source.VpnKey="vpn-two";source.At(53);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(double.IsNaN(s.DownloadBytesPerSecond) && s.Logs.Exists(x=>x.Message.Contains("接口或计数已重建")),"vpn.rebuild-clears-old-rate");
            int before=source.PhoneCalls;backend.Poll();backend.Poll();Check(source.PhoneCalls==before+2,"legacy.no-argument-poll-is-immediate");
            source=new Source();backend=new Backend(root,false,source,new PhoneSamplingPolicy(10,10));backend.Poll(PhoneSamplingMode.Foreground);
            source.At(10);backend.Poll(PhoneSamplingMode.Foreground);source.Current.RelayOwned=false;source.At(21);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(!s.SamplingOk && double.IsNaN(s.DownloadBytesPerSecond) && s.TelemetryState==TelemetryState.Stale,"expired-cache.never-current-speed");
            source=new Source();backend=new Backend(root,false,source);source.DuringPhone=delegate{source.Current=new SessionObservation{State=new SessionRecord{ProtectionSessionId=new string('c',32),PhoneIdentity="FAKE_PHONE",WirelessSerial="192.0.2.1:37123"}};};
            s=backend.Poll(PhoneSamplingMode.Foreground);Check(!s.TrafficAvailable && !s.IsNewTelemetrySample && s.ConnectionState==ConnectionState.Unknown,"during-query.session-change-discards-sample");
            source=new Source();backend=new Backend(root,false,source);source.At(10);backend.Poll(PhoneSamplingMode.Foreground);source.At(9);s=backend.Poll(PhoneSamplingMode.Foreground);
            Check(source.PhoneCalls==2 && double.IsNaN(s.DownloadBytesPerSecond),"clock.rollback-rebuilds-instead-of-negative-speed");
            source=new Source();backend=new Backend(root,false,source);
            for(int i=0;i<30;i++){source.At(i);backend.Poll(PhoneSamplingMode.Foreground);}
            Check(source.PhoneCalls==10 && source.LogCalls==30 && source.StateCalls==40,"request-count.30-local-ticks-only-10-complete-phone-queries");
            for(int i=30;i<60;i++){source.At(i);backend.Poll(PhoneSamplingMode.Paused);}
            Check(source.PhoneCalls==10 && source.LogCalls==60,"request-count.30-paused-ticks-zero-new-phone-queries");
            source.Current.State.WirelessSerial="192.0.2.1:40000";s=backend.Poll(PhoneSamplingMode.Paused);
            Check(source.PhoneCalls==10 && !s.TrafficAvailable && s.DownloadBytes==0,"endpoint.paused-change-invalidates-totals-without-query");

        }
        catch(Exception exception){failure=exception.ToString();}
        File.WriteAllText(Path.Combine(directory,"phone-sampling-results.json"),new JavaScriptSerializer().Serialize(new{Passed=failure==null,Count=Results.Count,Failure=failure,Checks=Results}));
        Console.WriteLine((failure==null?"PASS ":"FAIL ")+Results.Count+" sampling checks"+(failure==null?"":"\n"+failure));return failure==null?0:1;
    }
}
