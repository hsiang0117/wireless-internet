// Offline tests: compile with NativeNetwork.cs; no WMI, ADB, sockets or network changes.
using System;
using System.Collections.Generic;
using System.Linq;
using WirelessInternet;

internal static class NativeNetworkTunRegression
{
    private const string WifiGuid = "11111111-1111-1111-1111-111111111111";
    private const string EthernetGuid = "22222222-2222-2222-2222-222222222222";
    private static int checks;
    private static readonly ISet<int> Candidates = new HashSet<int>(new int[] { 3, 12 });

    private static int Main()
    {
        try
        {
            IgnoredRowsAreNotParsed();
            RequiredPhysicalFieldsStayRequired();
            SnapshotRegression();
            SelectionSafetyRegression();
            Console.WriteLine("PASS " + checks + " offline native-network TUN and safety checks; WMI/ADB/network calls: 0.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL after " + checks + " checks: " + error);
            return 1;
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++;
    }

    private static void Reject(Action action, string requiredText, string name)
    {
        Exception failure = null;
        try { action(); } catch (Exception error) { failure = error; }
        Check(failure != null && (requiredText == null || failure.Message.Contains(requiredText)), name);
    }

    private static Func<string, object> Fields(Dictionary<string, object> row)
    {
        return delegate(string name) { object value; return row.TryGetValue(name, out value) ? value : null; };
    }

    private static Dictionary<string, object> Route(int index, string gateway, uint metric)
    {
        return new Dictionary<string, object> {
            { "InterfaceIndex", index }, { "AddressFamily", 2 }, { "DestinationPrefix", "0.0.0.0/0" },
            { "NextHop", gateway }, { "RouteMetric", metric }, { "Store", 1 }, { "CompartmentId", 1 }
        };
    }

    private static Dictionary<string, object> IpInterface(int index, uint metric)
    {
        return new Dictionary<string, object> {
            { "InterfaceIndex", index }, { "AddressFamily", 2 }, { "InterfaceMetric", metric },
            { "Store", 1 }, { "CompartmentId", 1 }
        };
    }

    private static NetworkSnapshot Snapshot()
    {
        NetworkSnapshot snapshot = new NetworkSnapshot();
        snapshot.Adapters.Add(new NetworkAdapterData { InterfaceIndex = 3, InterfaceGuid = EthernetGuid,
            Name = "Physical ethernet", HardwareInterface = true, Virtual = false, IsUp = true, NdisPhysicalMedium = 14 });
        snapshot.Adapters.Add(new NetworkAdapterData { InterfaceIndex = 12, InterfaceGuid = WifiGuid,
            Name = "Physical wifi", HardwareInterface = true, Virtual = false, IsUp = true, NdisPhysicalMedium = 9 });
        snapshot.Adapters.Add(new NetworkAdapterData { InterfaceIndex = 45, Name = "Virtual TUN",
            HardwareInterface = false, Virtual = true, IsUp = true, NdisPhysicalMedium = 14 });
        snapshot.Routes.Add(NativeNetwork.ParseCandidateRoute(Candidates, Fields(Route(3, "192.0.2.1", 0))));
        snapshot.Routes.Add(NativeNetwork.ParseCandidateRoute(Candidates, Fields(Route(12, "192.168.43.1", 0))));
        snapshot.IpInterfaces.Add(NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(IpInterface(3, 25))));
        snapshot.IpInterfaces.Add(NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(IpInterface(12, 30))));
        return snapshot;
    }

    private static NetworkLink Resolve(NetworkSnapshot snapshot, IDictionary<string, object> config = null,
        NetworkLink expected = null, bool wifiOnly = false)
    {
        return NativeNetwork.Resolve(config, snapshot.Adapters, snapshot.Routes, snapshot.IpInterfaces, expected, wifiOnly);
    }

    private static void IgnoredRowsAreNotParsed()
    {
        int reads = 0;
        Func<string, object> poisonedTun = delegate(string name) {
            reads++;
            if (name != "InterfaceIndex") throw new Exception("Unrelated virtual field was accessed: " + name);
            return 45;
        };
        Check(NativeNetwork.ParseCandidateRoute(Candidates, poisonedTun) == null && reads == 1,
            "Unrelated TUN route fields are never accessed");
        reads = 0;
        Check(NativeNetwork.ParseCandidateIpInterface(Candidates, poisonedTun) == null && reads == 1,
            "Unrelated TUN interface fields are never accessed");

        foreach (object metric in new object[] { null, "not-a-metric", -1, UInt64.MaxValue, new object() })
        {
            Dictionary<string, object> row = IpInterface(45, 0);
            row["InterfaceMetric"] = metric;
            Check(NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(row)) == null,
                "Unrelated TUN metric is ignored: " + (metric == null ? "NULL" : metric.GetType().Name));
            Dictionary<string, object> route = Route(45, "not-an-IP", 0);
            route["RouteMetric"] = metric;
            route["AddressFamily"] = null;
            Check(NativeNetwork.ParseCandidateRoute(Candidates, Fields(route)) == null,
                "Unrelated virtual route abnormalities are ignored");
        }

        foreach (object index in new object[] { null, "", 0, -1, "Meta", UInt32.MaxValue, 12.5 })
        {
            Dictionary<string, object> row = new Dictionary<string, object> { { "InterfaceIndex", index } };
            Check(NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(row)) == null,
                "Unusable unassigned interface index is ignored");
            Check(NativeNetwork.ParseCandidateRoute(Candidates, Fields(row)) == null,
                "Unusable unassigned route index is ignored");
        }
        Check(NativeNetwork.ParseCandidateIpInterface(new HashSet<int>(), poisonedTun) == null,
            "No physical candidates yields no interface records");
        Check(NativeNetwork.ParseCandidateRoute(new HashSet<int>(), poisonedTun) == null,
            "No physical candidates yields no route records");
    }

    private static void RequiredPhysicalFieldsStayRequired()
    {
        foreach (string name in new string[] { "AddressFamily", "InterfaceMetric", "Store", "CompartmentId" })
        {
            Dictionary<string, object> row = IpInterface(12, 30);
            row[name] = null;
            Reject(delegate { NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(row)); }, name,
                "NULL physical interface field remains required: " + name);
        }
        foreach (string name in new string[] { "AddressFamily", "DestinationPrefix", "NextHop", "RouteMetric", "Store", "CompartmentId" })
        {
            Dictionary<string, object> row = Route(12, "192.168.43.1", 0);
            row[name] = null;
            Reject(delegate { NativeNetwork.ParseCandidateRoute(Candidates, Fields(row)); }, name,
                "NULL physical route field remains required: " + name);
        }
        Dictionary<string, object> missing = IpInterface(3, 25);
        missing.Remove("InterfaceMetric");
        Reject(delegate { NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(missing)); }, "InterfaceMetric",
            "Missing Ethernet metric is not converted to zero");
        Dictionary<string, object> malformed = IpInterface(12, 30);
        malformed["InterfaceMetric"] = "bad";
        Reject(delegate { NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(malformed)); }, null,
            "Malformed physical metric remains rejected");
        malformed["InterfaceMetric"] = UInt64.MaxValue;
        Reject(delegate { NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(malformed)); }, null,
            "Overflowing physical metric remains rejected");
        Check(NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(IpInterface(3, 0))).InterfaceMetric == 0,
            "Actual reported zero metric remains valid");
    }

    private static void SnapshotRegression()
    {
        foreach (bool virtualFirst in new bool[] { true, false })
        {
            NetworkSnapshot snapshot = Snapshot();
            snapshot.Routes.Clear();
            snapshot.IpInterfaces.Clear();
            Dictionary<string, object> tun = IpInterface(45, 0);
            tun["InterfaceMetric"] = null;
            Dictionary<string, object> tunRoute = new Dictionary<string, object> { { "InterfaceIndex", 45 }, { "RouteMetric", null } };
            List<Dictionary<string, object>> interfaceRows = new List<Dictionary<string, object>> {
                IpInterface(3, 25), IpInterface(12, 30)
            };
            List<Dictionary<string, object>> routeRows = new List<Dictionary<string, object>> {
                Route(3, "192.0.2.1", 0), Route(12, "192.168.43.1", 0)
            };
            if (virtualFirst) { interfaceRows.Insert(0, tun); routeRows.Insert(0, tunRoute); }
            else { interfaceRows.Add(tun); routeRows.Add(tunRoute); }
            foreach (Dictionary<string, object> row in interfaceRows)
            {
                IpInterfaceData parsed = NativeNetwork.ParseCandidateIpInterface(Candidates, Fields(row));
                if (parsed != null) snapshot.IpInterfaces.Add(parsed);
            }
            foreach (Dictionary<string, object> row in routeRows)
            {
                RouteData parsed = NativeNetwork.ParseCandidateRoute(Candidates, Fields(row));
                if (parsed != null) snapshot.Routes.Add(parsed);
            }
            NetworkLink selected = Resolve(snapshot);
            Check(selected.WifiInterfaceIndex == 12 && selected.EthernetInterfaceIndex == 3 &&
                selected.WifiRouteCost == 30 && selected.EthernetRouteCost == 25 &&
                selected.PhoneGateway == "192.168.43.1" && snapshot.IpInterfaces.Count == 2 && snapshot.Routes.Count == 2,
                "TUN NULL records leave valid physical selection intact, independent of row ordering");
        }
        NetworkSnapshot missing = Snapshot();
        missing.IpInterfaces.RemoveAll(delegate(IpInterfaceData item) { return item.InterfaceIndex == 12; });
        Reject(delegate { Resolve(missing); }, "metric", "Missing selected physical metric record remains rejected");
        NetworkSnapshot missingEthernet = Snapshot();
        missingEthernet.IpInterfaces.RemoveAll(delegate(IpInterfaceData item) { return item.InterfaceIndex == 3; });
        Reject(delegate { Resolve(missingEthernet); }, "metric", "Missing selected Ethernet metric record remains rejected");
        Check(Resolve(missingEthernet, null, null, true).EthernetInterfaceIndex == 0,
            "Wi-Fi-only resolution still does not require an Ethernet metric");
    }

    private static void SelectionSafetyRegression()
    {
        NetworkSnapshot snapshot = Snapshot();
        NetworkLink expected = Resolve(snapshot);
        Check(Resolve(snapshot, new Dictionary<string, object> { { "WifiInterfaceIndex", 12 }, { "EthernetInterfaceIndex", 3 } }, expected).PhoneGateway == expected.PhoneGateway,
            "Explicit physical indices and expected session still resolve");
        snapshot.IpInterfaces[0].InterfaceMetric = 30;
        Reject(delegate { Resolve(snapshot); }, "not preferred", "Equal Ethernet and Wi-Fi costs remain rejected");
        snapshot.IpInterfaces[0].InterfaceMetric = 31;
        Reject(delegate { Resolve(snapshot); }, "not preferred", "Worse Ethernet cost remains rejected");
        snapshot = Snapshot(); snapshot.Routes.RemoveAt(0);
        Reject(delegate { Resolve(snapshot); }, "default route", "Missing physical default route remains rejected");
        snapshot = Snapshot(); snapshot.Routes[1].NextHop = "0.0.0.0";
        Reject(delegate { Resolve(snapshot); }, "gateway", "Unspecified Wi-Fi gateway remains rejected");
        snapshot = Snapshot(); snapshot.Routes.Add(new RouteData { InterfaceIndex = 12, NextHop = "192.168.44.1", DestinationPrefix = "0.0.0.0/0" });
        Reject(delegate { Resolve(snapshot); }, "one valid IPv4 gateway", "Multiple Wi-Fi gateways remain rejected");
        snapshot = Snapshot(); snapshot.Adapters[1].InterfaceGuid = "not-a-guid";
        Reject(delegate { Resolve(snapshot); }, "GUID", "Malformed selected physical GUID remains rejected");
        snapshot = Snapshot(); snapshot.Adapters[1].InterfaceGuid = "33333333-3333-3333-3333-333333333333";
        Reject(delegate { Resolve(snapshot, null, expected); }, "different physical adapter", "Changed session GUID remains rejected");
        snapshot = Snapshot(); snapshot.Routes[1].NextHop = "192.168.44.1";
        Reject(delegate { Resolve(snapshot, null, expected); }, "gateway changed", "Changed session gateway remains rejected");
        snapshot = Snapshot();
        Reject(delegate { Resolve(snapshot, new Dictionary<string, object> { { "WifiInterfaceIndex", 45 } }); }, "configured physical", "Virtual TUN cannot replace the physical Wi-Fi role");
        Reject(delegate { Resolve(snapshot, new Dictionary<string, object> { { "EthernetInterfaceIndex", 45 } }); }, "configured physical", "Virtual TUN cannot replace the physical Ethernet role");
        Reject(delegate { Resolve(snapshot, new Dictionary<string, object> { { "WifiInterfaceIndex", -1 } }); }, "Invalid WifiInterfaceIndex", "Negative configured index remains rejected");
        Reject(delegate { Resolve(snapshot, new Dictionary<string, object> { { "WifiInterfaceIndex", 12 }, { "WifiInterfaceAlias", "wrong" } }); }, "index/alias disagree", "Conflicting index and alias remain rejected");
        snapshot.Adapters[1].Name = "Renamed Wi-Fi";
        Check(Resolve(snapshot, null, expected).WifiInterfaceGuid == expected.WifiInterfaceGuid,
            "A harmless adapter rename does not invalidate a GUID-pinned session");
        snapshot = Snapshot();
        snapshot.Routes.Add(new RouteData { InterfaceIndex = 12, NextHop = "192.168.55.1", DestinationPrefix = "0.0.0.0/0", AddressFamily = 23 });
        snapshot.Routes.Add(new RouteData { InterfaceIndex = 12, NextHop = "192.168.55.1", DestinationPrefix = "0.0.0.0/0", Store = 0 });
        snapshot.Routes.Add(new RouteData { InterfaceIndex = 12, NextHop = "192.168.55.1", DestinationPrefix = "0.0.0.0/0", CompartmentId = 2 });
        snapshot.IpInterfaces.Add(new IpInterfaceData { InterfaceIndex = 12, InterfaceMetric = 0, AddressFamily = 23 });
        snapshot.IpInterfaces.Add(new IpInterfaceData { InterfaceIndex = 12, InterfaceMetric = 0, Store = 0 });
        snapshot.IpInterfaces.Add(new IpInterfaceData { InterfaceIndex = 12, InterfaceMetric = 0, CompartmentId = 2 });
        Check(Resolve(snapshot).WifiRouteCost == 30, "Foreign AF/store/compartment data remains excluded");
        snapshot = Snapshot();
        snapshot.Routes[1].RouteMetric = UInt32.MaxValue;
        snapshot.IpInterfaces[1].InterfaceMetric = UInt32.MaxValue;
        Check(Resolve(snapshot).WifiRouteCost == 8589934590L, "Combined route cost remains a non-overflowing 64-bit sum");
        snapshot = Snapshot(); snapshot.IpInterfaces.Add(new IpInterfaceData { InterfaceIndex = 12, InterfaceMetric = 30 });
        Reject(delegate { Resolve(snapshot); }, "uniquely", "Duplicate selected interface metrics remain rejected");
        snapshot = Snapshot(); snapshot.Adapters.Add(new NetworkAdapterData { InterfaceIndex = 13, Name = "Other physical Wi-Fi",
            InterfaceGuid = "44444444-4444-4444-4444-444444444444", HardwareInterface = true, IsUp = true, NdisPhysicalMedium = 9 });
        snapshot.Routes.Add(new RouteData { InterfaceIndex = 13, NextHop = "192.168.50.1", DestinationPrefix = "0.0.0.0/0" });
        snapshot.IpInterfaces.Add(new IpInterfaceData { InterfaceIndex = 13, InterfaceMetric = 30 });
        Reject(delegate { Resolve(snapshot); }, "unique active physical Wifi", "Ambiguous physical Wi-Fi selection remains rejected");
        Check(Resolve(snapshot, new Dictionary<string, object> { { "WifiInterfaceIndex", 12 } }).WifiInterfaceIndex == 12,
            "Explicit index still resolves physical Wi-Fi ambiguity");
        Check(NativeNetwork.ValidateGatewayEndpoint("192.168.43.1:37123", "192.168.43.1") == "192.168.43.1:37123",
            "Same-gateway saved endpoint remains accepted");
        Reject(delegate { NativeNetwork.ValidateGatewayEndpoint("192.168.44.1:37123", "192.168.43.1"); }, "different hotspot gateway",
            "Saved endpoint gateway pin remains enforced");
        Reject(delegate { NativeNetwork.ValidateGatewayEndpoint("192.168.43.1:65536", "192.168.43.1"); }, "invalid",
            "Saved endpoint port range remains enforced");
    }
}
