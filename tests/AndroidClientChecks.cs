using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using WirelessInternet;

// Pure protocol and injected-command tests. No ADB, network, mutex or process I/O.
static class AndroidClientChecks
{
    const string Phone = "FAKE_PHONE", Gateway = "192.0.2.1", Endpoint = "192.0.2.1:37123";
    static int checks;
    static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); checks++; }
    static void Reject(Action action, string message, string name)
    {
        Exception error = null;
        try { action(); } catch (Exception caught) { error = caught; }
        Check(error is IOException && error.Message == message, name);
    }
    sealed class Reply
    { public string Arguments, Text; public int ExitCode; public bool AllowFailure; }
    sealed class Context : RuntimeContext
    {
        readonly Queue<Reply> replies = new Queue<Reply>();
        public readonly List<int> Sleeps = new List<int>();
        public readonly List<string> Calls = new List<string>();
        public Context() : base(Path.Combine(Path.GetTempPath(), "AndroidClientChecks-no-io")) { }
        public void Add(string command, string response, int exitCode = 0, bool allowFailure = false)
        { replies.Enqueue(new Reply { Arguments = command, Text = response, ExitCode = exitCode, AllowFailure = allowFailure }); }
        public override RuntimeCommandResult Adb(string[] arguments, bool allowFailure = false, int timeoutMs = 8000)
        {
            string command = String.Join(" ", arguments); Calls.Add(command);
            Check(replies.Count > 0, "only expected fake commands are issued: " + command);
            Reply reply = replies.Dequeue();
            Check(command == reply.Arguments && reply.AllowFailure == allowFailure && timeoutMs == 8000, "command and failure policy preserve order: " + command);
            return new RuntimeCommandResult { ExitCode = reply.ExitCode, Output = reply.Text };
        }
        public override void Sleep(int milliseconds) { Sleeps.Add(milliseconds); }
        public void Complete() { Check(replies.Count == 0, "all expected commands consumed"); }
    }
    sealed class RejectRunner : ISetupCommandRunner
    { public SetupCommandResult Run(string executable, string arguments, string standardInput, int timeoutMilliseconds) { throw new Exception("No setup command should run."); } }
    sealed class Networks : ISetupNetworkProbe
    {
        public List<SetupNetworkInfo> GetWirelessGateways()
        { return new List<SetupNetworkInfo> { new SetupNetworkInfo { Gateway = Gateway, WifiInterfaceIndex = 12, InterfaceName = "fixture" } }; }
    }
    static string Mdns(int port, string phone = Phone, string suffix = "")
    { return "adb-" + phone + "-nonce _adb-tls-connect._tcp" + suffix + " 198.51.100.9:" + port; }
    static string PhoneCommand(string tail) { return "-s " + Endpoint + " shell " + tail; }
    static string RestrictionDump(string modes)
    { return "VPNs:\n  0: owner\n" + modes + "\n  10: profile\n    2026-01-01T00:00:02 - [LockdownAlwaysOn] Mode changed: lockdown=true alwaysOn=true\n"; }
    static string Mode(string time, string lockdown, string alwaysOn)
    { return "    " + time + " - [LockdownAlwaysOn] Mode changed: lockdown=" + lockdown + " alwaysOn=" + alwaysOn; }
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine("FAIL after " + checks + " checks: " + error.GetType().Name + ": " + error.Message); return 1; }
    }
    static void Run()
    {
        IPAddress address; int port; string entered;
        foreach (string valid in new[] { "192.0.2.1:1", "192.0.2.1:65535", "127.0.0.1:5555", "0.0.0.0:1" })
            Check(AndroidProtocol.TryParseIpv4Endpoint(valid, out address, out port), "fact parser accepts valid IPv4 endpoint without monitor policy: " + valid);
        foreach (string invalid in new[] { "", "192.0.2.1", "192.0.2.1:0", "192.0.2.1:65536", "999.0.2.1:1234", "localhost:1234", "[::1]:1234", " 192.0.2.1:1234", "192.0.2.1:1234 " })
            Check(!AndroidProtocol.TryParseIpv4Endpoint(invalid, out address, out port), "invalid exact endpoint rejected: " + invalid);
        Check(AndroidProtocol.TryParseEnteredEndpoint(" 37123 ", out entered, out port) && entered == "" && port == 37123, "setup keeps port-only and trim policy");
        Check(AndroidProtocol.TryParseEnteredEndpoint("198.51.100.4:37123", out entered, out port) && entered == "198.51.100.4", "setup can accept non-gateway entered IP before target resolution");
        Check(!AndroidProtocol.TryParseEnteredEndpoint("::1:37123", out entered, out port), "setup rejects IPv6 input");
        HashSet<int> ports = AndroidProtocol.NativeTlsPorts(new[] { Mdns(37123), Mdns(37123, Phone, "."), Mdns(1, "OTHER"), "adb-FAKE_PHONE-nonce _adb-tls-pairing._tcp 192.0.2.1:12345", Mdns(0), Mdns(65536) }, Phone);
        Check(ports.Count == 1 && ports.Contains(37123), "mDNS ports deduplicate and isolate identity/service/bounds");
        Check(AndroidProtocol.NativeTlsPorts(new[] { Mdns(37123), Mdns(37124) }, Phone).Count == 2, "parser keeps ambiguous ports for caller policy");
        Check(AndroidProtocol.NativeTlsPorts(null, Phone).Count == 0, "missing lines produce no ports");
        Check(AndroidProtocol.NativeTlsPorts(new[] { Mdns(12, "FAKE.PHONE") }, "FAKE.PHONE").SetEquals(new[] { 12 }), "identity regex metacharacters remain escaped");
        Check(!AndroidProtocol.PhoneVpnConnected(null) && !AndroidProtocol.PhoneVpnConnected("VPN CONNECTED extra: VPN:com.genymobile.gnirehtet"), "VPN parser requires original anchored Android agent format");
        Check(AndroidProtocol.PhoneVpnConnected("  NetworkAgentInfo{network{123} extra ni{VPN CONNECTED extra: VPN:com.genymobile.gnirehtet}"), "VPN connected fact parsed");
        Check(!AndroidProtocol.PhoneVpnConnected("  NetworkAgentInfo{network{123} extra ni{VPN CONNECTED extra: VPN:other.package}"), "other VPN does not satisfy gnirehtet fact");
        Check(AndroidProtocol.ReverseReady("fixture localabstract:gnirehtet tcp:31416"), "legacy controller reverse recognition preserved");
        Check(!AndroidProtocol.ReverseReady("localabstract:gnirehtet tcp:12345"), "wrong reverse target rejected");
        Check(AndroidProtocol.GuardActive("ACTIVE abc 123 123", "abc") && !AndroidProtocol.GuardActive("ACTIVE other 123", "abc"), "guard activity is token-specific");
        Check(AndroidProtocol.OwnerVpnRestrictions("other output") == OwnerVpnRestrictionState.OwnerMissing, "missing owner distinguished");
        Check(AndroidProtocol.OwnerVpnRestrictions(RestrictionDump("no records")) == OwnerVpnRestrictionState.Unknown, "unknown owner mode distinguished");
        string disabled = Mode("2026-01-01T00:00:01", "false", "false");
        string enabled = Mode("2026-01-01T00:00:02", "true", "false");
        Check(AndroidProtocol.OwnerVpnRestrictions(RestrictionDump(disabled)) == OwnerVpnRestrictionState.Disabled, "other profile cannot change owner mode");
        Check(AndroidProtocol.OwnerVpnRestrictions(RestrictionDump(enabled + "\n" + disabled)) == OwnerVpnRestrictionState.Enabled, "latest owner record selected by timestamp rather than position");
        Check(AndroidProtocol.OwnerVpnRestrictions(RestrictionDump(enabled + "\n" + Mode("2026-01-01T00:00:03", "false", "false"))) == OwnerVpnRestrictionState.Disabled, "later disabled owner mode recognized");
        var context = new Context();
        context.Add("mdns services", "", 0, true); context.Add("mdns services", Mdns(37123), 0, true);
        Check(new AndroidDeviceClient(context).DiscoverNativeSerial(Gateway, Phone, null) == Endpoint, "discovery retries then uses hotspot gateway with discovered port");
        Check(context.Sleeps.SequenceEqual(new[] { 400 }), "discovery retry delay preserved"); context.Complete();
        context = new Context(); for (int attempt = 0; attempt < 6; attempt++) context.Add("mdns services", "", 0, true);
        Check(new AndroidDeviceClient(context).DiscoverNativeSerial(Gateway, Phone, Endpoint) == Endpoint, "six empty discoveries may use verified saved gateway endpoint");
        Check(context.Sleeps.SequenceEqual(Enumerable.Repeat(400, 5)), "six attempts have five delays"); context.Complete();
        context = new Context(); context.Add("mdns services", "not available", 1, true);
        Check(new AndroidDeviceClient(context).DiscoverNativeSerial(Gateway, Phone, Endpoint) == Endpoint && context.Sleeps.Count == 0, "failed discovery breaks directly to fallback"); context.Complete();
        context = new Context(); for (int attempt = 0; attempt < 6; attempt++) context.Add("mdns services", Mdns(12) + "\n" + Mdns(13), 0, true);
        Reject(delegate { new AndroidDeviceClient(context).DiscoverNativeSerial(Gateway, Phone, Endpoint); },
            "Multiple native debugging ports were discovered for this phone. Wait a few seconds and retry.", "ambiguous discovery cannot be hidden by fallback"); context.Complete();
        context = new Context(); context.Add("mdns services", "", 1, true);
        Reject(delegate { new AndroidDeviceClient(context).DiscoverNativeSerial(Gateway, Phone, "198.51.100.1:37123"); },
            "The saved native connection endpoint is invalid or belongs to another hotspot gateway. Update phone binding.", "saved endpoint cannot escape selected gateway"); context.Complete();
        context = new Context(); context.Add("mdns services", "", 1, true);
        Reject(delegate { new AndroidDeviceClient(context).DiscoverNativeSerial(Gateway, Phone, null); },
            "Native wireless debugging was not discovered. Keep phone Wi-Fi, hotspot and Wireless debugging ON, or update its main-page connection port in phone binding.", "missing discovery/fallback preserves guidance"); context.Complete();
        context = new Context(); context.Add(PhoneCommand("getprop ro.serialno"), "OTHER");
        Reject(delegate { new AndroidDeviceClient(context).VerifyPhone(Endpoint, Phone, null); }, "The wireless endpoint does not match the configured phone.", "identity mismatch stops before other phone queries"); context.Complete();
        context = new Context(); context.Add(PhoneCommand("getprop ro.serialno"), Phone); context.Add(PhoneCommand("am get-current-user"), "10");
        Reject(delegate { new AndroidDeviceClient(context).VerifyPhone(Endpoint, Phone, null); }, "This runtime supports only the phone owner profile (user 0).", "non-owner rejected before switch query"); context.Complete();
        context = new Context(); context.Add(PhoneCommand("getprop ro.serialno"), Phone); context.Add(PhoneCommand("am get-current-user"), "0"); context.Add(PhoneCommand("settings get global adb_wifi_enabled"), "0");
        Reject(delegate { new AndroidDeviceClient(context).VerifyPhone(Endpoint, Phone, null); }, "The phone system Wireless debugging switch is OFF. Turn it ON manually; no settings were changed.", "wireless switch is observed, never modified"); context.Complete();
        context = new Context(); context.Add(PhoneCommand("getprop ro.serialno"), Phone); context.Add(PhoneCommand("am get-current-user"), "0"); context.Add(PhoneCommand("settings get global adb_wifi_enabled"), "1");
        new AndroidDeviceClient(context).VerifyPhone(Endpoint, Phone, delegate { throw new Exception("rejected status handler"); });
        context.Complete();
        foreach (string[] restriction in new[] { new[] { "null", "0", "ok" }, new[] { "other.vpn", "null", "ok" }, new[] { AndroidProtocol.GnirehtetPackage, "0", "reject" }, new[] { "null", "1", "reject" } })
        {
            context = new Context(); context.Add(PhoneCommand("settings get secure always_on_vpn_app"), restriction[0]); context.Add(PhoneCommand("settings get secure always_on_vpn_lockdown"), restriction[1]);
            if (restriction[2] == "ok") new AndroidDeviceClient(context).VerifyRestrictionsOff(Endpoint, null);
            else Reject(delegate { new AndroidDeviceClient(context).VerifyRestrictionsOff(Endpoint, null); }, "Manually turn OFF both gnirehtet VPN restrictions before Start or Disconnect. The tracked relay was left running.", "VPN restrictions fail without mutation");
            context.Complete();
        }
        context = new Context(); context.Add(PhoneCommand("dumpsys vpn_management"), RestrictionDump(disabled)); new AndroidDeviceClient(context).VerifyRuntimeRestrictionsOff(Endpoint); context.Complete();
        context = new Context(); context.Add(PhoneCommand("dumpsys vpn_management"), "missing owner");
        Reject(delegate { new AndroidDeviceClient(context).VerifyRuntimeRestrictionsOff(Endpoint); }, "Cannot verify owner-profile VPN restrictions. Relay left running.", "missing runtime owner preserves error"); context.Complete();
        context = new Context(); context.Add(PhoneCommand("dumpsys vpn_management"), RestrictionDump(enabled));
        Reject(delegate { new AndroidDeviceClient(context).VerifyRuntimeRestrictionsOff(Endpoint); }, "Cannot verify runtime VPN restrictions are OFF. Relay left running; check the phone switches.", "runtime restriction refuses cleanup safely"); context.Complete();
        var setup = new SetupBackend(Path.GetTempPath(), new RejectRunner(), new Networks());
        Check(setup.ResolveTarget("198.51.100.9:37123", 0).Endpoint == Endpoint, "setup retains manual-IP replacement with selected hotspot gateway");
        Check(setup.ResolveTarget(" 37123 ", 0).Endpoint == Endpoint, "setup port-only path remains intact");
        Reject(delegate { setup.ResolveTarget("371234", 0); }, "请输入端口号（1–65535），或 IPv4地址:端口。例如 37123 或 192.168.43.1:37123。", "setup error text preserved");
        Console.WriteLine("PASS " + checks + " Android client/protocol checks; real ADB/network/mutex/process calls: 0.");
    }
}
