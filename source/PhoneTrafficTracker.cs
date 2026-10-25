using System;
using System.Diagnostics;

namespace WirelessInternet
{
    // Pure telemetry state. No ADB, filesystem, process, health or UI decisions.
    // The assembler owns observation ordering and supplies each monotonic timestamp.
    internal sealed class PhoneTrafficTracker
    {
        readonly PhoneSamplingPolicy samplingPolicy;
        string counterKey = "", lastSampleError = "";
        ulong lastDown, lastUp, savedDown, savedUp;
        double lastPhoneUptime;
        long lastSampleQpc, lastAttemptQpc;
        bool hasAttempt, hasSampleClock, hasRateBaseline, samplingPaused;
        string samplingEndpoint = "", samplingIdentity = "";
        double savedDownRate = double.NaN, savedUpRate = double.NaN;
        DateTime? savedStart;
        bool savedTrafficAvailable;
        int sampleFailures;

        public PhoneTrafficTracker(PhoneSamplingPolicy samplingPolicy)
        { this.samplingPolicy = samplingPolicy; }
        public Snapshot CreateSnapshot()
        {
            return new Snapshot { DownloadBytes = savedDown, UploadBytes = savedUp, StartedAt = savedStart, TrafficAvailable = savedTrafficAvailable,
                TelemetryState = savedTrafficAvailable ? TelemetryState.Stale : TelemetryState.Unavailable,
                TrafficNote = ActiveTrafficNote() };
        }
        public void Reset(bool clearTraffic)
        {
            counterKey = ""; lastSampleQpc = lastAttemptQpc = 0;
            hasAttempt = hasSampleClock = hasRateBaseline = samplingPaused = false;
            sampleFailures = 0; lastSampleError = "";
            savedDownRate = savedUpRate = double.NaN;
            if (clearTraffic) { savedDown = savedUp = 0; savedStart = null; savedTrafficAvailable = false; }
        }
        public void ForgetEndpoint() { samplingEndpoint = samplingIdentity = ""; }
        public void BindEndpoint(SessionRecord state, Snapshot snapshot)
        {
            if (samplingEndpoint != state.WirelessSerial || samplingIdentity != state.PhoneIdentity)
            {
                Reset(true);
                samplingEndpoint = state.WirelessSerial; samplingIdentity = state.PhoneIdentity;
                snapshot.TrafficAvailable = false; snapshot.DownloadBytes = snapshot.UploadBytes = 0; snapshot.StartedAt = null;
                snapshot.TelemetryState = TelemetryState.Unavailable;
            }
        }
        public bool PrepareSampling(Snapshot snapshot, PhoneSamplingMode mode, long sampleClock, bool relayAvailable)
        {
            bool resume = samplingPaused && mode != PhoneSamplingMode.Paused;
            if (mode == PhoneSamplingMode.Paused || mode == PhoneSamplingMode.Refresh || resume)
            {
                hasRateBaseline = false; savedDownRate = savedUpRate = double.NaN;
            }
            samplingPaused = mode == PhoneSamplingMode.Paused;
            double sinceAttempt = (sampleClock - lastAttemptQpc) / (double)Stopwatch.Frequency;
            bool queryPhone = relayAvailable && samplingPolicy.ShouldQuery(resume ? PhoneSamplingMode.Refresh : mode, hasAttempt, sinceAttempt);
            ReusePhoneTelemetry(snapshot, mode, sampleClock);
            if (!relayAvailable)
            {
                hasRateBaseline = hasAttempt = false; savedDownRate = savedUpRate = double.NaN;
                snapshot.SamplingOk = false; snapshot.DownloadBytesPerSecond = snapshot.UploadBytesPerSecond = double.NaN;
                snapshot.TelemetryState = snapshot.TrafficAvailable ? TelemetryState.Stale : TelemetryState.Unavailable;
                if (mode != PhoneSamplingMode.Paused) snapshot.TrafficNote = "共享链路待确认：保留最后流量，速度显示 —。";
            }
            return queryPhone;
        }
        public void BeginAttempt(Snapshot snapshot, long sampleClock)
        {
            hasAttempt = true; lastAttemptQpc = sampleClock;
            snapshot.DownloadBytesPerSecond = snapshot.UploadBytesPerSecond = double.NaN;
        }
        public void AcceptSample(PhoneCounters counters, long now, Snapshot snapshot, Action<string, string, string> add)
        {
            bool continuous = counterKey == counters.Key && hasRateBaseline && counters.Uptime >= lastPhoneUptime &&
                counters.Download >= lastDown && counters.Upload >= lastUp;
            double seconds = (now - lastSampleQpc) / (double)Stopwatch.Frequency;
            if (continuous && sampleFailures == 0 && seconds > 0 && seconds < 10)
            {
                snapshot.DownloadBytesPerSecond = (counters.Download - lastDown) / seconds;
                snapshot.UploadBytesPerSecond = (counters.Upload - lastUp) / seconds;
            }
            if (counterKey != "" && (counterKey != counters.Key || counters.Uptime < lastPhoneUptime || counters.Download < lastDown || counters.Upload < lastUp))
                add("流量", "VPN 接口或计数已重建，流量与速度基线已重新读取。", "WARN");
            savedDown = snapshot.DownloadBytes = lastDown = counters.Download;
            savedUp = snapshot.UploadBytes = lastUp = counters.Upload;
            savedStart = snapshot.StartedAt = counters.StartedAt;
            counterKey = counters.Key; lastSampleQpc = now; lastPhoneUptime = counters.Uptime;
            hasSampleClock = hasRateBaseline = true;
            savedDownRate = snapshot.DownloadBytesPerSecond; savedUpRate = snapshot.UploadBytesPerSecond;
            snapshot.IsNewTelemetrySample = true; snapshot.TelemetryAgeSeconds = 0;
            snapshot.SamplingOk = true;
            snapshot.TelemetryState = TelemetryState.Current;
            snapshot.TrafficNote = ActiveTrafficNote();
            savedTrafficAvailable = snapshot.TrafficAvailable = true;
            if (sampleFailures > 0) add("流量", "手机采样已恢复；重新建立速度基线。", "INFO");
            sampleFailures = 0; lastSampleError = "";
        }
        public void FailSample(Exception error, Snapshot snapshot, Action<string, string, string> add)
        {
            sampleFailures++;
            hasRateBaseline = false; savedDownRate = savedUpRate = double.NaN;
            snapshot.SamplingOk = false; snapshot.DownloadBytesPerSecond = snapshot.UploadBytesPerSecond = double.NaN;
            snapshot.TelemetryState = snapshot.TrafficAvailable ? TelemetryState.Stale : TelemetryState.Unavailable;
            if (lastSampleError != error.Message) { add("手机采样", error.Message, "WARN"); lastSampleError = error.Message; }
            snapshot.TrafficNote = "统计采样暂不可用：保留最后流量，速度显示 —。连接与保护状态分别判断。";
        }
        void ReusePhoneTelemetry(Snapshot snapshot, PhoneSamplingMode mode, long now)
        {
            double age = hasSampleClock ? (now - lastSampleQpc) / (double)Stopwatch.Frequency : double.NaN;
            snapshot.TelemetryAgeSeconds = age >= 0 ? age : double.NaN;
            bool current = mode != PhoneSamplingMode.Paused && sampleFailures == 0 && savedTrafficAvailable && samplingPolicy.RateIsFresh(age);
            snapshot.SamplingOk = current;
            snapshot.TelemetryState = current ? TelemetryState.Current : savedTrafficAvailable ? TelemetryState.Stale : TelemetryState.Unavailable;
            if (current) { snapshot.DownloadBytesPerSecond = savedDownRate; snapshot.UploadBytesPerSecond = savedUpRate; }
            if (mode == PhoneSamplingMode.Paused)
                snapshot.TrafficNote = "手机统计已暂停：窗口恢复后重新采样；保留最后流量，速度显示 —。共享与失联保护继续运行。";
            else if (sampleFailures > 0)
                snapshot.TrafficNote = "统计采样暂不可用：保留最后流量，速度显示 —。连接与保护状态分别判断。";
            else if (!current && savedTrafficAvailable)
                snapshot.TrafficNote = "最近统计已过期：保留最后流量，速度显示 —；等待下一次有效采样。";
        }
        string ActiveTrafficNote()
        {
            return "手机 VPN 接口累计（含协议开销），不代表运营商计费；前台约 " +
                samplingPolicy.IntervalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                " 秒更新，速度为最近采样间隔平均值。";
        }
    }
}
