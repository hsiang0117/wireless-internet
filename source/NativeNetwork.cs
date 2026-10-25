using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WirelessInternet
{
    public sealed class NetworkAdapterData
    {
        public int InterfaceIndex;
        public string Name, InterfaceAlias, InterfaceGuid, PhysicalMediaType;
        public uint NdisPhysicalMedium;
        public bool HardwareInterface, Virtual, IsUp;
    }

    public sealed class RouteData
    {
        public int InterfaceIndex;
        public string DestinationPrefix, NextHop;
        public uint RouteMetric;
        // Explicit defaults keep pure fixtures equivalent to an ActiveStore IPv4 snapshot.
        public ushort AddressFamily = 2;
        public uint CompartmentId = 1;
        public byte Store = 1;
    }

    public sealed class IpInterfaceData
    {
        public int InterfaceIndex;
        public uint InterfaceMetric;
        public ushort AddressFamily = 2;
        public uint CompartmentId = 1;
        public byte Store = 1;
    }

    // RuntimeState may inherit this class; all persisted names match previous session records.
    public class NetworkLink
    {
        public int WifiInterfaceIndex, EthernetInterfaceIndex;
        public string WifiInterfaceGuid, EthernetInterfaceGuid, WifiInterfaceAlias, EthernetInterfaceAlias;
        public string PhoneGateway, WirelessSerial;
        public long WifiRouteCost;
        public long? EthernetRouteCost;
    }

    public sealed class NetworkSnapshot
    {
        public List<NetworkAdapterData> Adapters = new List<NetworkAdapterData>();
        public List<RouteData> Routes = new List<RouteData>();
        public List<IpInterfaceData> IpInterfaces = new List<IpInterfaceData>();
    }

    public static class NativeNetwork
    {
        const string ScopePath = @"\\.\root\StandardCimv2";
        const uint PrimaryCompartment = 1;
        const byte ActiveStore = 1;

        public static NetworkLink Read(IDictionary<string, object> config, NetworkLink expected = null, bool wifiOnly = false)
        {
            NetworkSnapshot snapshot = ReadSnapshot();
            return Resolve(config, snapshot.Adapters, snapshot.Routes, snapshot.IpInterfaces, expected, wifiOnly);
        }

        public static NetworkSnapshot ReadSnapshot()
        {
            NetworkSnapshot result = new NetworkSnapshot();
            Query("SELECT Name,InterfaceIndex,InterfaceGuid,NdisPhysicalMedium,HardwareInterface,Virtual,InterfaceOperationalStatus FROM MSFT_NetAdapter WHERE HardwareInterface=TRUE AND Virtual=FALSE AND InterfaceIndex>0", delegate(ManagementObject row)
            {
                result.Adapters.Add(new NetworkAdapterData {
                    InterfaceIndex = ReadIndex(row), Name = ReadText(row, "Name"), InterfaceAlias = ReadText(row, "Name"),
                    InterfaceGuid = ReadText(row, "InterfaceGuid"), NdisPhysicalMedium = ReadUInt(row, "NdisPhysicalMedium"),
                    HardwareInterface = ReadBool(row, "HardwareInterface"), Virtual = ReadBool(row, "Virtual"),
                    // The CIM base Status property is not Get-NetAdapter's computed "Up" value.
                    IsUp = ReadUInt(row, "InterfaceOperationalStatus") == 1
                });
            });
            HashSet<int> physicalCandidates = new HashSet<int>(result.Adapters
                .Where(a => a.IsUp && (GetKind(a) == "Wifi" || GetKind(a) == "Ethernet"))
                .Select(a => a.InterfaceIndex));
            Query("SELECT InterfaceIndex,AddressFamily,DestinationPrefix,NextHop,RouteMetric,Store,CompartmentId FROM MSFT_NetRoute WHERE AddressFamily=2 AND Store=1 AND CompartmentId=1 AND DestinationPrefix='0.0.0.0/0'", delegate(ManagementObject row)
            {
                RouteData route = ParseCandidateRoute(physicalCandidates, Fields(row));
                if (route != null) result.Routes.Add(route);
            });
            Query("SELECT InterfaceIndex,AddressFamily,InterfaceMetric,Store,CompartmentId FROM MSFT_NetIPInterface WHERE AddressFamily=2 AND Store=1 AND CompartmentId=1", delegate(ManagementObject row)
            {
                IpInterfaceData ipInterface = ParseCandidateIpInterface(physicalCandidates, Fields(row));
                if (ipInterface != null) result.IpInterfaces.Add(ipInterface);
            });
            return result;
        }

        // Ignore unrelated virtual interfaces before reading fields that their WMI provider
        // may omit. Required data for a physical candidate must still fail closed.
        internal static RouteData ParseCandidateRoute(ISet<int> candidates, Func<string, object> field)
        {
            int index = CandidateIndex(candidates, field);
            if (index == 0) return null;
            return new RouteData {
                InterfaceIndex = index, AddressFamily = checked((ushort)ReadUInt(field, "AddressFamily")),
                DestinationPrefix = Convert.ToString(Required(field, "DestinationPrefix"), CultureInfo.InvariantCulture),
                NextHop = Convert.ToString(Required(field, "NextHop"), CultureInfo.InvariantCulture),
                RouteMetric = ReadUInt(field, "RouteMetric"), Store = checked((byte)ReadUInt(field, "Store")),
                CompartmentId = ReadUInt(field, "CompartmentId")
            };
        }

        internal static IpInterfaceData ParseCandidateIpInterface(ISet<int> candidates, Func<string, object> field)
        {
            int index = CandidateIndex(candidates, field);
            if (index == 0) return null;
            return new IpInterfaceData {
                InterfaceIndex = index, AddressFamily = checked((ushort)ReadUInt(field, "AddressFamily")),
                InterfaceMetric = ReadUInt(field, "InterfaceMetric"), Store = checked((byte)ReadUInt(field, "Store")),
                CompartmentId = ReadUInt(field, "CompartmentId")
            };
        }

        static int CandidateIndex(ISet<int> candidates, Func<string, object> field)
        {
            int index;
            if (!Int32.TryParse(Convert.ToString(field("InterfaceIndex"), CultureInfo.InvariantCulture),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index <= 0 || !candidates.Contains(index)) return 0;
            return index;
        }

        static Func<string, object> Fields(ManagementBaseObject row)
        {
            return delegate(string name) { PropertyData property = row.Properties[name]; return property == null ? null : property.Value; };
        }

        static object Required(Func<string, object> field, string name)
        {
            object value = field(name);
            if (value == null) throw new InvalidOperationException("A required Windows network field is unavailable: " + name);
            return value;
        }

        static uint ReadUInt(Func<string, object> field, string name) { return Convert.ToUInt32(Required(field, name), CultureInfo.InvariantCulture); }

        public static bool WifiHasIPv6Default(int wifiInterfaceIndex)
        {
            if (wifiInterfaceIndex <= 0) return false;
            try
            {
                bool found = false;
                Query("SELECT InterfaceIndex FROM MSFT_NetRoute WHERE AddressFamily=23 AND Store=1 AND CompartmentId=1 AND DestinationPrefix='::/0' AND InterfaceIndex=" + wifiInterfaceIndex.ToString(CultureInfo.InvariantCulture),
                    delegate(ManagementObject row) { if (ReadIndex(row) == wifiInterfaceIndex) found = true; });
                return found;
            }
            catch (InvalidOperationException)
            {
                try { Console.Error.WriteLine("Warning: the phone-hotspot IPv6 default route could not be checked. IPv4 route validation is unchanged."); } catch { }
                return false;
            }
        }

        // Pure selection: never performs WMI, starts a process, or changes a route/interface.
        public static NetworkLink Resolve(IDictionary<string, object> config, IEnumerable<NetworkAdapterData> adapters,
            IEnumerable<RouteData> routes, IEnumerable<IpInterfaceData> ipInterfaces, NetworkLink expected = null, bool wifiOnly = false)
        {
            if (adapters == null || routes == null || ipInterfaces == null) throw new ArgumentNullException("Network snapshot collections must not be null.");
            List<NetworkAdapterData> adapterList = adapters.Where(a => a != null).ToList();
            List<RouteData> routeList = routes.Where(IsActiveIpv4Default).ToList();
            List<IpInterfaceData> interfaceList = ipInterfaces.Where(i => i != null && i.AddressFamily == 2 && i.Store == ActiveStore && i.CompartmentId == PrimaryCompartment).ToList();
            NetworkAdapterData wifi = Select(config, "Wifi", adapterList, routeList);
            string[] gateways = routeList.Where(r => r.InterfaceIndex == wifi.InterfaceIndex).Select(r => NormalizeIpv4(r.NextHop))
                .Where(g => g != null).Distinct(StringComparer.Ordinal).ToArray();
            if (gateways.Length != 1) throw new InvalidOperationException("The selected phone-hotspot Wi-Fi interface must have one valid IPv4 gateway.");
            NetworkLink result = new NetworkLink {
                WifiInterfaceIndex = wifi.InterfaceIndex, WifiInterfaceGuid = RequireGuid(wifi.InterfaceGuid, "Wi-Fi"),
                WifiInterfaceAlias = wifi.Name ?? wifi.InterfaceAlias ?? "", PhoneGateway = gateways[0],
                WifiRouteCost = DefaultCost(wifi.InterfaceIndex, routeList, interfaceList),
                EthernetInterfaceIndex = 0, EthernetInterfaceGuid = "", EthernetInterfaceAlias = "", EthernetRouteCost = null
            };
            if (!wifiOnly)
            {
                NetworkAdapterData ethernet = Select(config, "Ethernet", adapterList, routeList);
                if (ethernet.InterfaceIndex == wifi.InterfaceIndex) throw new InvalidOperationException("Wi-Fi and Ethernet must be different physical interfaces.");
                string ethernetGuid = RequireGuid(ethernet.InterfaceGuid, "Ethernet");
                long ethernetCost = DefaultCost(ethernet.InterfaceIndex, routeList, interfaceList);
                if (ethernetCost >= result.WifiRouteCost) throw new InvalidOperationException("The selected Ethernet IPv4 route is not preferred over the phone hotspot. No routes were changed.");
                result.EthernetInterfaceIndex = ethernet.InterfaceIndex; result.EthernetInterfaceGuid = ethernetGuid;
                result.EthernetInterfaceAlias = ethernet.Name ?? ethernet.InterfaceAlias ?? ""; result.EthernetRouteCost = ethernetCost;
            }
            ValidateExpected(result, expected, wifiOnly);
            return result;
        }

        public static string GetKind(NetworkAdapterData adapter)
        {
            if (adapter == null || !adapter.HardwareInterface || adapter.Virtual) return "";
            if (adapter.NdisPhysicalMedium == 1 || adapter.NdisPhysicalMedium == 9) return "Wifi";
            if (adapter.NdisPhysicalMedium == 14) return "Ethernet";
            if (adapter.NdisPhysicalMedium != 0) return "";
            string text = Regex.Replace((adapter.PhysicalMediaType ?? "").ToLowerInvariant(), "[^a-z0-9]", "");
            if (text == "native80211" || text == "wirelesslan" || text == "ndisphysicalmediumnative80211" || text == "ndisphysicalmediumwirelesslan") return "Wifi";
            if (text == "8023" || text == "ndisphysicalmedium8023") return "Ethernet";
            return "";
        }

        public static string NormalizeIpv4(string value)
        {
            IPAddress address;
            if (value == null || !Regex.IsMatch(value, @"^(?:[0-9]{1,3}\.){3}[0-9]{1,3}$") ||
                !IPAddress.TryParse(value, out address) || address.AddressFamily != AddressFamily.InterNetwork) return null;
            byte first = address.GetAddressBytes()[0];
            return first == 0 || first == 127 || first >= 224 ? null : address.ToString();
        }

        public static string ValidateGatewayEndpoint(string endpoint, string gateway)
        {
            Match match = Regex.Match(endpoint ?? "", @"^(?<address>(?:[0-9]{1,3}\.){3}[0-9]{1,3}):(?<port>[0-9]{1,5})$");
            int port;
            if (!match.Success || !Int32.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new InvalidOperationException("The saved native ConnectEndpoint is invalid. Complete phone setup again.");
            string address = NormalizeIpv4(match.Groups["address"].Value);
            if (address == null || !String.Equals(address, gateway, StringComparison.Ordinal))
                throw new InvalidOperationException("The saved native ConnectEndpoint belongs to a different hotspot gateway. Update it in phone setup.");
            return address + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        public static NetworkLink ExpectedFromDictionary(IDictionary<string, object> record)
        {
            if (record == null) return null;
            return new NetworkLink {
                WifiInterfaceIndex = ConfigIndex(record, "WifiInterfaceIndex"), EthernetInterfaceIndex = ConfigIndex(record, "EthernetInterfaceIndex"),
                WifiInterfaceGuid = ConfigText(record, "WifiInterfaceGuid"), EthernetInterfaceGuid = ConfigText(record, "EthernetInterfaceGuid"),
                WifiInterfaceAlias = ConfigText(record, "WifiInterfaceAlias"), EthernetInterfaceAlias = ConfigText(record, "EthernetInterfaceAlias"),
                PhoneGateway = ConfigText(record, "PhoneGateway"), WirelessSerial = ConfigText(record, "WirelessSerial")
            };
        }

        static NetworkAdapterData Select(IDictionary<string, object> config, string kind, List<NetworkAdapterData> adapters, List<RouteData> routes)
        {
            int index = ConfigIndex(config, kind + "InterfaceIndex");
            string alias = ConfigText(config, kind + "InterfaceAlias").Trim();
            IEnumerable<NetworkAdapterData> matches = adapters.Where(a => GetKind(a) == kind && a.IsUp);
            if (index != 0) matches = matches.Where(a => a.InterfaceIndex == index);
            if (alias.Length != 0) matches = matches.Where(a => String.Equals(a.Name, alias, StringComparison.OrdinalIgnoreCase) || String.Equals(a.InterfaceAlias, alias, StringComparison.OrdinalIgnoreCase));
            NetworkAdapterData[] available = matches.Where(a => a.InterfaceIndex > 0 && routes.Any(r => r.InterfaceIndex == a.InterfaceIndex)).ToArray();
            if (available.Length != 1)
            {
                if (index != 0 || alias.Length != 0) throw new InvalidOperationException("The configured physical " + kind + " interface is unavailable, has no IPv4 default route, or its index/alias disagree. Check setup.");
                throw new InvalidOperationException("A unique active physical " + kind + " interface with an IPv4 default route was not found. Select the intended interface in setup.");
            }
            return available[0];
        }

        static long DefaultCost(int index, List<RouteData> routes, List<IpInterfaceData> interfaces)
        {
            IpInterfaceData[] matches = interfaces.Where(i => i.InterfaceIndex == index).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("The IPv4 interface metric could not be determined uniquely.");
            long[] costs = routes.Where(r => r.InterfaceIndex == index).Select(r => (long)r.RouteMetric + matches[0].InterfaceMetric).ToArray();
            if (costs.Length == 0) throw new InvalidOperationException("An IPv4 default route is missing from the selected interface.");
            return costs.Min();
        }

        static void ValidateExpected(NetworkLink actual, NetworkLink expected, bool wifiOnly)
        {
            if (expected == null) return;
            ValidateExpectedInterface(actual.WifiInterfaceIndex, actual.WifiInterfaceGuid, expected.WifiInterfaceIndex, expected.WifiInterfaceGuid);
            if (!wifiOnly) ValidateExpectedInterface(actual.EthernetInterfaceIndex, actual.EthernetInterfaceGuid, expected.EthernetInterfaceIndex, expected.EthernetInterfaceGuid);
            string gateway = expected.PhoneGateway;
            if (String.IsNullOrEmpty(gateway))
            {
                Match match = Regex.Match(expected.WirelessSerial ?? "", @"^(?<address>(?:[0-9]{1,3}\.){3}[0-9]{1,3}):[0-9]+$");
                if (match.Success) gateway = NormalizeIpv4(match.Groups["address"].Value);
            }
            if (!String.IsNullOrEmpty(gateway) && !String.Equals(gateway, actual.PhoneGateway, StringComparison.Ordinal))
                throw new InvalidOperationException("The tracked phone-hotspot gateway changed. Stop and start a new session.");
        }

        static void ValidateExpectedInterface(int actualIndex, string actualGuid, int expectedIndex, string expectedGuid)
        {
            if (expectedIndex != 0 && expectedIndex != actualIndex) throw new InvalidOperationException("A tracked physical network interface changed. Stop the old session before selecting another interface.");
            if (!String.IsNullOrEmpty(expectedGuid) && !String.Equals(RequireGuid(expectedGuid, "tracked"), actualGuid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A tracked interface index now belongs to a different physical adapter.");
        }

        static string RequireGuid(string text, string role)
        {
            Guid value;
            if (!Guid.TryParse(text, out value) || value == Guid.Empty) throw new InvalidOperationException("The selected " + role + " interface does not expose a stable interface GUID.");
            return value.ToString("D");
        }

        static bool IsActiveIpv4Default(RouteData route)
        {
            return route != null && route.AddressFamily == 2 && route.CompartmentId == PrimaryCompartment && route.Store == ActiveStore && route.DestinationPrefix == "0.0.0.0/0";
        }

        static object ConfigValue(IDictionary<string, object> config, string key)
        {
            if (config == null) return null;
            object result = null; bool found = false;
            foreach (KeyValuePair<string, object> item in config)
                if (String.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    if (found) throw new InvalidOperationException("Duplicate configuration key: " + key);
                    result = item.Value; found = true;
                }
            return result;
        }
        static string ConfigText(IDictionary<string, object> config, string key) { return Convert.ToString(ConfigValue(config, key), CultureInfo.InvariantCulture) ?? ""; }
        static int ConfigIndex(IDictionary<string, object> config, string key)
        {
            string text = ConfigText(config, key); int value;
            if (text.Length == 0) return 0;
            if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
                throw new InvalidOperationException("Invalid " + key + "; choose an interface in setup.");
            return value;
        }

        static void Query(string query, Action<ManagementObject> consume)
        {
            try
            {
                ManagementScope scope = new ManagementScope(ScopePath, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(5) });
                EnumerationOptions options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(5) };
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), options))
                using (ManagementObjectCollection rows = searcher.Get())
                    foreach (ManagementObject row in rows) using (row) consume(row);
            }
            catch (Exception ex)
            {
                if (!(ex is ManagementException) && !(ex is System.Runtime.InteropServices.COMException) && !(ex is UnauthorizedAccessException) &&
                    !(ex is InvalidCastException) && !(ex is FormatException) && !(ex is OverflowException) && !(ex is InvalidOperationException)) throw;
                throw new InvalidOperationException("Windows network information could not be read safely. No network settings were changed.", ex);
            }
        }
        static object Required(ManagementBaseObject row, string name)
        {
            PropertyData property = row.Properties[name];
            if (property == null || property.Value == null) throw new InvalidOperationException("A required Windows network field is unavailable: " + name);
            return property.Value;
        }
        static string ReadText(ManagementBaseObject row, string name) { return Convert.ToString(Required(row, name), CultureInfo.InvariantCulture); }
        static uint ReadUInt(ManagementBaseObject row, string name) { return Convert.ToUInt32(Required(row, name), CultureInfo.InvariantCulture); }
        static bool ReadBool(ManagementBaseObject row, string name) { return Convert.ToBoolean(Required(row, name), CultureInfo.InvariantCulture); }
        static int ReadIndex(ManagementBaseObject row)
        {
            int value = checked((int)ReadUInt(row, "InterfaceIndex"));
            if (value <= 0) throw new InvalidOperationException("A Windows network interface has no valid index.");
            return value;
        }
    }
}
