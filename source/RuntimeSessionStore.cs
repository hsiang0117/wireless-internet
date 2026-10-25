using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace WirelessInternet
{
    // Persistence only. The caller retains the existing core mutex protocol and
    // supplies validation/read callbacks so RuntimeContext overrides still apply.
    public sealed class RuntimeSessionStore
    {
        readonly string root, statePath;
        public RuntimeSessionStore(string root, string statePath)
        { this.root = root; this.statePath = statePath; }

        public static string GetString(IDictionary<string, object> data, string key)
        {
            object value; return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
        }
        public static int GetInt(IDictionary<string, object> data, string key, int fallback)
        {
            string text = GetString(data, key); int value;
            if (text.Length == 0) return fallback;
            if (!Int32.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) throw new IOException("配置中的 " + key + " 无效。");
            return value;
        }
        public Dictionary<string, object> ReadConfig()
        {
            var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(ReadShared(Path.Combine(root, "wireless-config.json")));
            if (data == null || GetString(data, "Transport") != "NativeTls") throw new IOException("请先完成原生无线调试的手机配置。");
            string identity = GetString(data, "PhoneIdentity");
            if (!Regex.IsMatch(identity, "^[A-Za-z0-9_-]{1,100}$")) throw new IOException("请点击“更换/绑定手机”，先配对、检测并保存手机。");
            int timeout = GetInt(data, "DisconnectTimeoutSeconds", 30);
            if (timeout < 15 || timeout > 120) throw new IOException("失联保护时间必须为 15–120 秒。");
            return data;
        }
        public RuntimeState ReadState(Func<string, bool> validSerial, Func<RuntimeState, bool> protectionValid)
        {
            string text;
            try { text = ReadShared(statePath); } catch (FileNotFoundException) { return null; } catch (DirectoryNotFoundException) { return null; }
            JavaScriptSerializer json = new JavaScriptSerializer();
            RuntimeState state = json.Deserialize<RuntimeState>(text);
            if (state == null || state.Transport != "NativeTls" || !Regex.IsMatch(state.PhoneIdentity ?? "", "^[A-Za-z0-9_-]{1,100}$") ||
                !validSerial(state.WirelessSerial) || !protectionValid(state)) throw new IOException("运行会话格式无效，未修改进程或手机状态。");
            state.RawFields = json.Deserialize<Dictionary<string, object>>(text);
            return state;
        }
        public void WriteState(RuntimeState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            JavaScriptSerializer json = new JavaScriptSerializer();
            var data = json.Deserialize<Dictionary<string, object>>(json.Serialize(state));
            if (state.RawFields != null) foreach (var entry in state.RawFields) if (!data.ContainsKey(entry.Key)) data.Add(entry.Key, entry.Value);
            string temporary = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json.Serialize(data), new UTF8Encoding(false));
                if (File.Exists(statePath)) File.Replace(temporary, statePath, null); else File.Move(temporary, statePath);
            }
            finally { try { File.Delete(temporary); } catch { } }
        }
        public void DeleteStateIfToken(string token, Func<RuntimeState> readState)
        {
            RuntimeState current = readState();
            if (current != null && String.Equals(current.ProtectionSessionId, token, StringComparison.Ordinal)) File.Delete(statePath);
        }
        static string ReadShared(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                if (stream.Length > 262144) throw new IOException("运行配置或会话文件过大。");
                return reader.ReadToEnd();
            }
        }
    }
}
