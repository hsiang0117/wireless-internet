using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using WirelessInternet;

// Pure decoding plus files under the driver's marked test directory. No child
// processes, ADB, network discovery, production session or system mutex is used.
internal static class SharedModelChecks
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static RuntimeContext context;
    static string configPath;
    static int passed;
    static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Case(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch { rejected = true; }
        Require(rejected, message);
    }
    static RuntimeState State()
    {
        string token = new string('a', 32);
        return new RuntimeState {
            PhoneIdentity = "MODEL_FIXTURE", WirelessSerial = "192.0.2.5:37123", ProtectionSessionId = token,
            ProtectionMode = "PcOnly",
            RelayPath = context.RelayPath, RelayStartTimeUtcTicks = "639000012345678901", RelayProcessId = 123,
            RelayLogPipe = "WirelessInternet.Log.fixture", LogMode = "MemoryOnly",
            WatchdogStartTimeUtcTicks = "639000012345678902", MonitorHeartbeatQpcTicks = "9007199254740993",
            WatchdogPath = context.WorkerPath, RuntimeEngine = "CSharp", WatchdogProcessId = 124,
            WifiRouteCost = 9007199254740993L, EthernetRouteCost = null
        };
    }
    static Dictionary<string, object> Fields() { return Json.Deserialize<Dictionary<string, object>>(Json.Serialize(State())); }
    static void WriteState(Dictionary<string, object> fields)
    { File.WriteAllText(context.StatePath, Json.Serialize(fields), new UTF8Encoding(false)); }
    static void WriteConfig(string text) { File.WriteAllText(configPath, text, new UTF8Encoding(false)); }
    static SessionRecord Observe() { return SystemSessionObservationSource.ReadState(context.StatePath); }
    sealed class RejectingContext : RuntimeContext
    {
        internal int Calls;
        internal RejectingContext(string root) : base(root) { }
        public override bool ProtectionValid(RuntimeState state) { Calls++; return false; }
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Expected the driver's isolated test directory.");
            string root = Path.Combine(Path.GetFullPath(args[0]), "models");
            Directory.CreateDirectory(Path.Combine(root, "state"));
            context = new RuntimeContext(root); configPath = Path.Combine(root, "wireless-config.json");
            Stopwatch watch = Stopwatch.StartNew();
            Case("worker-to-observer-wire-fields", delegate {
                RuntimeState state = State(); context.WriteState(state);
                SessionRecord view = Observe();
                foreach (string name in new string[] { "ProtectionSessionId", "PhoneIdentity", "WirelessSerial", "RelayStartTimeUtcTicks", "RelayPath",
                    "WatchdogStartTimeUtcTicks", "MonitorHeartbeatQpcTicks", "WatchdogPath", "RuntimeEngine", "RelayProcessId", "WatchdogProcessId",
                    "ProtectionTimeoutSeconds", "RelayLogPipe", "LogMode" })
                {
                    object expected = typeof(RuntimeState).GetField(name).GetValue(state);
                    object actual = typeof(SessionRecord).GetField(name).GetValue(view);
                    Require(Object.Equals(expected, actual), "wire field changed: " + name);
                }
            });
            Case("wide-and-nullable-network-values", delegate {
                RuntimeState state = State(); state.EthernetRouteCost = 9007199254740995L; context.WriteState(state);
                RuntimeState loaded = context.ReadState();
                Require(loaded.WifiRouteCost == 9007199254740993L && loaded.EthernetRouteCost == 9007199254740995L,
                    "network costs lost 64-bit integer precision");
                loaded.EthernetRouteCost = null; context.WriteState(loaded);
                Require(context.ReadState().EthernetRouteCost == null, "nullable route cost changed");
            });
            Case("different-reader-defaults", delegate {
                Dictionary<string, object> fields = Fields(); fields.Remove("ProtectionTimeoutSeconds"); fields.Remove("Transport"); WriteState(fields);
                Require(Observe().ProtectionTimeoutSeconds == 0, "GUI default timeout changed");
                RuntimeState loaded = context.ReadState();
                Require(loaded.ProtectionTimeoutSeconds == 30 && loaded.Transport == "NativeTls", "worker defaults changed");
            });
            Case("observer-ignores-non-observed-metadata", delegate {
                Dictionary<string, object> fields = Fields();
                fields["WifiRouteCost"] = new object[] { "not a route cost" };
                fields["WifiInterfaceIndex"] = "not an index"; fields["EthernetRouteCost"] = new Dictionary<string, object>();
                fields["RelayHostProcessId"] = "not a PID"; fields["Transport"] = new object[] { 1, 2 };
                WriteState(fields);
                Require(Observe().PhoneIdentity == "MODEL_FIXTURE", "worker-only metadata tightened GUI decoding");
                Reject(delegate { context.ReadState(); }, "worker unexpectedly accepted invalid numeric state");
            });
            Case("observer-rejects-malformed-used-fields", delegate {
                Dictionary<string, object> fields = Fields(); fields["RelayProcessId"] = "not a PID"; WriteState(fields);
                Reject(delegate { Observe(); }, "GUI accepted invalid observed PID");
                fields = Fields(); fields["ProtectionTimeoutSeconds"] = new object[] { 30 }; WriteState(fields);
                Reject(delegate { Observe(); }, "GUI accepted invalid observed timeout");
            });
            Case("observer-keeps-identity-and-token-validation", delegate {
                foreach (string key in new string[] { "PhoneIdentity", "ProtectionSessionId" })
                {
                    Dictionary<string, object> fields = Fields(); fields[key] = "invalid value!"; WriteState(fields);
                    Reject(delegate { Observe(); }, "GUI accepted invalid " + key);
                }
            });
            Case("observer-and-worker-endpoint-policy-stays-distinct", delegate {
                Dictionary<string, object> fields = Fields(); fields["WirelessSerial"] = "999.999.999.999:99999"; WriteState(fields);
                Require(Observe().WirelessSerial == "999.999.999.999:99999", "read-only endpoint policy became stricter");
                Reject(delegate { context.ReadState(); }, "worker accepted an unusable IPv4 endpoint");
            });
            Case("worker-keeps-protection-validation", delegate {
                Dictionary<string, object> fields = Fields(); fields["PhoneControlPath"] = "/wrong/control.sh"; WriteState(fields);
                Reject(delegate { context.ReadState(); }, "worker accepted mismatched control path");
                fields = Fields(); fields["ProtectionTimeoutSeconds"] = 121; WriteState(fields);
                Reject(delegate { context.ReadState(); }, "worker accepted invalid protection duration");
                fields = Fields(); fields["ProtectionMode"] = "Unrecognized"; WriteState(fields);
                Reject(delegate { context.ReadState(); }, "worker accepted unknown protection mode");
                RuntimeState legacy = State(); legacy.ProtectionMode = null;
                legacy.PhoneControlPath = "/data/local/tmp/codex-wireless-internet/" + legacy.ProtectionSessionId + "/control.sh";
                context.WriteState(legacy);
                Require(context.ReadState().PhoneControlPath == legacy.PhoneControlPath, "legacy recovery metadata no longer readable");
                context.WriteState(State());
                Require(context.ReadState().ProtectionMode == "PcOnly" && String.IsNullOrEmpty(context.ReadState().PhoneControlPath), "PC-only state should not require phone script");
            });
            Case("worker-validation-override-remains-effective", delegate {
                WriteState(Fields()); RejectingContext rejecting = new RejectingContext(root);
                Reject(delegate { rejecting.ReadState(); }, "virtual protection validator was bypassed");
                Require(rejecting.Calls == 1, "virtual protection validator call count changed");
            });
            Case("unknown-state-fields-survive-known-field-update", delegate {
                Dictionary<string, object> fields = Fields();
                fields["Future"] = new Dictionary<string, object> { { "Nested", new object[] { 1, "two", null } }, { "Wide", 9007199254740993L } };
                WriteState(fields); RuntimeState state = context.ReadState(); state.ProtectionTimeoutSeconds = 31; context.WriteState(state);
                Dictionary<string, object> written = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(context.StatePath));
                Dictionary<string, object> extra = (Dictionary<string, object>)written["Future"];
                Require(Convert.ToInt64(extra["Wide"]) == 9007199254740993L && ((System.Collections.IList)extra["Nested"]).Count == 3,
                    "unknown nested state fields changed");
                Require(Convert.ToInt32(written["ProtectionTimeoutSeconds"]) == 31 && !written.ContainsKey("RawFields"),
                    "stale raw value overwrote a known field or bookkeeping leaked into JSON");
            });
            Case("configuration-read-does-not-fill-defaults", delegate {
                WriteConfig("{\"Transport\":\"NativeTls\",\"PhoneIdentity\":\"MODEL_FIXTURE\",\"Future\":{\"Enabled\":true}}");
                string original = File.ReadAllText(configPath);
                Dictionary<string, object> worker = context.ReadConfig();
                ConfigurationDocument setup = new SetupBackend(root).ReadConfigurationDocument();
                Require(worker.Count == 3 && setup.Fields.Count == 3 && !worker.ContainsKey("DisconnectTimeoutSeconds") &&
                    File.ReadAllText(configPath) == original, "configuration read added or rewrote fields");
            });
            Case("configuration-document-preserves-unknown-values", delegate {
                ConfigurationDocument document = ConfigurationDocument.Parse("{\"PhoneIdentity\":\"MODEL_FIXTURE\",\"Future\":{\"Wide\":9007199254740993,\"Null\":null,\"Items\":[1,\"two\"]}}");
                document.ConnectEndpoint = "192.0.2.5:37123";
                ConfigurationDocument loaded = ConfigurationDocument.Parse(document.Serialize());
                Dictionary<string, object> extra = (Dictionary<string, object>)loaded.Fields["Future"];
                Require(loaded.PhoneIdentity == "MODEL_FIXTURE" && loaded.ConnectEndpoint == "192.0.2.5:37123" &&
                    Convert.ToInt64(extra["Wide"]) == 9007199254740993L && extra["Null"] == null && ((System.Collections.IList)extra["Items"]).Count == 2 &&
                    !loaded.Fields.ContainsKey("DisconnectTimeoutSeconds"), "configuration codec changed unknown values or persisted defaults");
                Require(ConfigurationDocument.Parse("null") == null, "null document handling changed");
            });
            Case("worker-timeout-validation-remains-strict", delegate {
                foreach (string value in new string[] { "\"bad\"", "0", "14", "121", "2147483648" })
                {
                    WriteConfig("{\"Transport\":\"NativeTls\",\"PhoneIdentity\":\"MODEL_FIXTURE\",\"DisconnectTimeoutSeconds\":" + value + "}");
                    Reject(delegate { context.ReadConfig(); }, "worker accepted timeout " + value);
                    Require(SetupBackend.IsConfigurationComplete(root), "configuration-complete probe became strict timeout validation");
                }
            });
            Case("setup-index-fallback-and-worker-integer-policy", delegate {
                foreach (string value in new string[] { "bad", "-1", "2147483648" })
                {
                    Dictionary<string, object> fields = new Dictionary<string, object> { { "WifiInterfaceIndex", value } };
                    Require(SetupBackend.GetIndex(fields, "WifiInterfaceIndex") == 0, "setup index fallback changed");
                    if (value != "-1") Reject(delegate { RuntimeContext.GetInt(fields, "WifiInterfaceIndex", 0); }, "worker integer parser became tolerant");
                }
                Require(RuntimeContext.GetInt(null, "missing", 30) == 30, "worker missing integer fallback changed");
            });
            Case("observer-preserves-case-insensitive-members", delegate {
                Dictionary<string, object> fields = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> field in Fields()) fields.Add(field.Key.ToLowerInvariant(), field.Value);
                fields["wifiroutecost"] = "ignored metadata"; WriteState(fields);
                SessionRecord observed = Observe();
                Require(observed.PhoneIdentity == "MODEL_FIXTURE" && observed.ProtectionTimeoutSeconds == 30 && observed.RelayProcessId == 123,
                    "GUI member-name matching changed");
            });
            Console.WriteLine("SharedModelChecks: " + passed + " passed in " + watch.ElapsedMilliseconds + " ms");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
