using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WirelessInternet
{
    public enum OwnerVpnRestrictionState { OwnerMissing, Unknown, Disabled, Enabled }

    // Facts parsed from Android/ADB replies. Retry, recovery, binding and session
    // ownership policies remain with the caller; this class performs no I/O.
    public static class AndroidProtocol
    {
        public const string GnirehtetPackage = "com.genymobile.gnirehtet";

        public static HashSet<int> NativeTlsPorts(IEnumerable<string> lines, string identity)
        {
            string pattern = "^adb-" + Regex.Escape(identity) + "-\\S+[ \\t]+_adb-tls-connect[.]_tcp[.]?[ \\t]+[0-9.]+:(?<port>[0-9]+)[ \\t]*\\r?$";
            HashSet<int> ports = new HashSet<int>();
            foreach (string line in lines ?? new string[0])
            {
                Match service = Regex.Match(line, pattern);
                int port;
                if (service.Success && Int32.TryParse(service.Groups["port"].Value, out port) && port >= 1 && port <= 65535)
                    ports.Add(port);
            }
            return ports;
        }

        public static bool TryParseIpv4Endpoint(string text, out IPAddress address, out int port)
        {
            Match match = Regex.Match(text ?? String.Empty, "^(?<ip>[0-9]{1,3}(?:[.][0-9]{1,3}){3}):(?<port>[0-9]{1,5})$");
            address = null; port = 0;
            return match.Success && IPAddress.TryParse(match.Groups["ip"].Value, out address) &&
                address.AddressFamily == AddressFamily.InterNetwork &&
                Int32.TryParse(match.Groups["port"].Value, out port) && port >= 1 && port <= 65535;
        }

        public static bool TryParseEnteredEndpoint(string text, out string address, out int port)
        {
            Match match = Regex.Match((text ?? String.Empty).Trim(), "^(?:(?<ip>[0-9]{1,3}(?:[.][0-9]{1,3}){3}):)?(?<port>[0-9]{1,5})$");
            IPAddress parsed;
            address = match.Groups["ip"].Value; port = 0;
            return match.Success && Int32.TryParse(match.Groups["port"].Value, out port) && port >= 1 && port <= 65535 &&
                (address.Length == 0 || (IPAddress.TryParse(address, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork));
        }

        public static bool PhoneVpnConnected(string text)
        {
            return Regex.IsMatch(text ?? String.Empty,
                "(?m)^  NetworkAgentInfo\\{network\\{\\d+\\}[^\\r\\n]*ni\\{VPN CONNECTED extra: VPN:com[.]genymobile[.]gnirehtet\\}");
        }

        public static bool ReverseReady(string text)
        { return Regex.IsMatch(text ?? String.Empty, @"localabstract:gnirehtet\s+tcp:31416"); }

        public static bool GuardActive(string text, string token)
        { return Regex.IsMatch(text ?? String.Empty, "^ACTIVE " + Regex.Escape(token ?? String.Empty) + " "); }

        public static OwnerVpnRestrictionState OwnerVpnRestrictions(string dump)
        {
            Match owner = Regex.Match(dump, @"(?ms)^  0:[^\r\n]*\r?\n(?<body>.*?)(?=^  \d+:|\z)");
            if (!owner.Success) return OwnerVpnRestrictionState.OwnerMissing;
            Match latest = null;
            foreach (Match mode in Regex.Matches(owner.Groups["body"].Value,
                @"(?m)^[ \t]*(\d{4}-\d\d-\d\dT\S+) - \[LockdownAlwaysOn\] Mode changed: lockdown=(true|false) alwaysOn=(true|false)"))
                if (latest == null || String.CompareOrdinal(mode.Groups[1].Value, latest.Groups[1].Value) > 0) latest = mode;
            if (latest == null) return OwnerVpnRestrictionState.Unknown;
            return latest.Groups[2].Value == "false" && latest.Groups[3].Value == "false" ?
                OwnerVpnRestrictionState.Disabled : OwnerVpnRestrictionState.Enabled;
        }
    }
}
