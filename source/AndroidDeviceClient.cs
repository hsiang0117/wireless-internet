using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;

namespace WirelessInternet
{
    // Worker-side phone commands. The controller still owns the core lock and
    // operation ordering; the monitor retains its separate recovery strategy.
    public sealed class AndroidDeviceClient
    {
        readonly RuntimeContext context;
        public AndroidDeviceClient(RuntimeContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            this.context = context;
        }

        public string DiscoverNativeSerial(string gateway, string identity, string fallback)
        {
            HashSet<int> ports = new HashSet<int>();
            for (int attempt = 0; attempt < 6; attempt++)
            {
                RuntimeCommandResult result = context.Adb(new string[] { "mdns", "services" }, true);
                if (!result.Success) break;
                ports = AndroidProtocol.NativeTlsPorts(result.Lines, identity);
                if (ports.Count == 1) return gateway + ":" + ports.First().ToString(CultureInfo.InvariantCulture);
                if (attempt < 5) context.Sleep(400);
            }
            if (ports.Count > 1) throw new IOException("Multiple native debugging ports were discovered for this phone. Wait a few seconds and retry.");
            if (!String.IsNullOrEmpty(fallback))
            {
                int port;
                IPAddress address;
                if (!AndroidProtocol.TryParseIpv4Endpoint(fallback, out address, out port) || address.ToString() != gateway)
                    throw new IOException("The saved native connection endpoint is invalid or belongs to another hotspot gateway. Update phone binding.");
                return gateway + ":" + port.ToString(CultureInfo.InvariantCulture);
            }
            throw new IOException("Native wireless debugging was not discovered. Keep phone Wi-Fi, hotspot and Wireless debugging ON, or update its main-page connection port in phone binding.");
        }

        public void VerifyPhone(string serial, string identity, Action<string> output)
        {
            string actual = context.Adb(new string[] { "-s", serial, "shell", "getprop", "ro.serialno" }).Text;
            if (String.IsNullOrEmpty(actual) || actual != identity) throw new IOException("The wireless endpoint does not match the configured phone.");
            if (context.Adb(new string[] { "-s", serial, "shell", "am", "get-current-user" }).Text != "0")
                throw new IOException("This runtime supports only the phone owner profile (user 0).");
            if (context.Adb(new string[] { "-s", serial, "shell", "settings", "get", "global", "adb_wifi_enabled" }).Text != "1")
                throw new IOException("The phone system Wireless debugging switch is OFF. Turn it ON manually; no settings were changed.");
            Report(output, "Native TLS wireless debugging verified: " + serial + "; configured phone identity verified; system switch ON.");
        }

        public void VerifyRestrictionsOff(string serial, Action<string> output)
        {
            string alwaysOn = context.Adb(new string[] { "-s", serial, "shell", "settings", "get", "secure", "always_on_vpn_app" }).Text;
            string lockdown = context.Adb(new string[] { "-s", serial, "shell", "settings", "get", "secure", "always_on_vpn_lockdown" }).Text;
            Report(output, "Always-on VPN: " + alwaysOn + "; block non-VPN connections: " + lockdown);
            if (alwaysOn == AndroidProtocol.GnirehtetPackage || (lockdown != "0" && lockdown != "null"))
                throw new IOException("Manually turn OFF both gnirehtet VPN restrictions before Start or Disconnect. The tracked relay was left running.");
        }

        public void VerifyRuntimeRestrictionsOff(string serial)
        {
            string dump = context.Adb(new string[] { "-s", serial, "shell", "dumpsys", "vpn_management" }).Text;
            OwnerVpnRestrictionState restrictions = AndroidProtocol.OwnerVpnRestrictions(dump);
            if (restrictions == OwnerVpnRestrictionState.OwnerMissing)
                throw new IOException("Cannot verify owner-profile VPN restrictions. Relay left running.");
            if (restrictions != OwnerVpnRestrictionState.Disabled)
                throw new IOException("Cannot verify runtime VPN restrictions are OFF. Relay left running; check the phone switches.");
        }

        static void Report(Action<string> output, string message)
        { if (output != null && !String.IsNullOrWhiteSpace(message)) try { output(message); } catch { } }
    }
}
