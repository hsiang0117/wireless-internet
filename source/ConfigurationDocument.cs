using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace WirelessInternet
{
    // A view over the existing JSON dictionary. Reading never inserts defaults,
    // validates unrelated settings, or discards fields owned by another version.
    public sealed class ConfigurationDocument
    {
        public static class Keys
        {
            public const string Transport = "Transport", PhoneIdentity = "PhoneIdentity", ConnectEndpoint = "ConnectEndpoint";
            public const string DisconnectTimeoutSeconds = "DisconnectTimeoutSeconds";
            public const string WifiInterfaceIndex = "WifiInterfaceIndex", EthernetInterfaceIndex = "EthernetInterfaceIndex";
            public const string WifiInterfaceAlias = "WifiInterfaceAlias", EthernetInterfaceAlias = "EthernetInterfaceAlias";
        }

        public readonly Dictionary<string, object> Fields;
        public ConfigurationDocument() : this(new Dictionary<string, object>()) { }
        public ConfigurationDocument(Dictionary<string, object> fields)
        {
            if (fields == null) throw new ArgumentNullException("fields");
            Fields = fields;
        }

        public static ConfigurationDocument Parse(string text)
        {
            Dictionary<string, object> fields = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            return fields == null ? null : new ConfigurationDocument(fields);
        }
        public string Serialize() { return new JavaScriptSerializer().Serialize(Fields); }

        public string Transport { get { return ReadString(Fields, Keys.Transport); } set { Fields[Keys.Transport] = value; } }
        public string PhoneIdentity { get { return ReadString(Fields, Keys.PhoneIdentity); } set { Fields[Keys.PhoneIdentity] = value; } }
        public string ConnectEndpoint { get { return ReadString(Fields, Keys.ConnectEndpoint); } set { Fields[Keys.ConnectEndpoint] = value; } }
        public int DisconnectTimeoutSeconds
        {
            get { return ReadInt32(Fields, Keys.DisconnectTimeoutSeconds, 30); }
            set { Fields[Keys.DisconnectTimeoutSeconds] = value; }
        }
        // The binding form tolerates old/invalid selections. Network validation
        // still consumes Fields through NativeNetwork's own stricter key rules.
        public int WifiInterfaceIndex
        {
            get { return ReadIndex(Fields, Keys.WifiInterfaceIndex); }
            set { Fields[Keys.WifiInterfaceIndex] = value; }
        }
        public int EthernetInterfaceIndex
        {
            get { return ReadIndex(Fields, Keys.EthernetInterfaceIndex); }
            set { Fields[Keys.EthernetInterfaceIndex] = value; }
        }

        public static string ReadString(IDictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : String.Empty;
        }
        public static int ReadInt32(IDictionary<string, object> data, string key, int fallback)
        {
            string text = ReadString(data, key); int value;
            if (text.Length == 0) return fallback;
            if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new IOException("配置中的 " + key + " 无效。");
            return value;
        }
        public static int ReadIndex(IDictionary<string, object> data, string key)
        {
            int value;
            return Int32.TryParse(ReadString(data, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0 ? value : 0;
        }
    }
}
