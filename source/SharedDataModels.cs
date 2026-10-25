using System;
using System.Collections.Generic;
using System.Reflection;
using System.Web.Script.Serialization;

namespace WirelessInternet
{
    // A field shared with the original read-only GUI projection. Additional
    // network/worker fields must not silently tighten the observer's decoder.
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class SessionObservationFieldAttribute : Attribute { }

    // Pure persisted network data; network discovery remains in NativeNetwork.
    public class NetworkLink
    {
        public int WifiInterfaceIndex, EthernetInterfaceIndex;
        public string WifiInterfaceGuid, EthernetInterfaceGuid, WifiInterfaceAlias, EthernetInterfaceAlias;
        public string PhoneGateway;
        [SessionObservationField] public string WirelessSerial;
        public long WifiRouteCost;
        public long? EthernetRouteCost;
    }

    // Shared persisted session fields. Defaults deliberately remain zero/null:
    // only RuntimeState gives newly created worker sessions a 30-second timeout.
    public class SessionRecord : NetworkLink
    {
        [SessionObservationField] public string ProtectionSessionId, PhoneIdentity, RelayStartTimeUtcTicks, RelayPath;
        [SessionObservationField] public string WatchdogStartTimeUtcTicks, MonitorHeartbeatQpcTicks, WatchdogPath, RuntimeEngine;
        [SessionObservationField] public int RelayProcessId, WatchdogProcessId, ProtectionTimeoutSeconds;
        [SessionObservationField] public string RelayLogPipe, LogMode;
    }

    internal static class SessionRecordJson
    {
        static readonly HashSet<string> ObservationFields = FindObservationFields();

        static HashSet<string> FindObservationFields()
        {
            // Preserve JavaScriptSerializer's member-name handling, including
            // differently cased input keys, without maintaining another DTO.
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldInfo field in typeof(SessionRecord).GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (field.IsDefined(typeof(SessionObservationFieldAttribute), false)) names.Add(field.Name);
            return names;
        }

        internal static SessionRecord ReadObservation(string text)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            Dictionary<string, object> raw = json.Deserialize<Dictionary<string, object>>(text);
            if (raw == null) return null;
            Dictionary<string, object> projection = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> field in raw)
                if (ObservationFields.Contains(field.Key)) projection.Add(field.Key, field.Value);
            return json.ConvertToType<SessionRecord>(projection);
        }
    }
}
