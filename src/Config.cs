using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace HassWidgets
{
    public class WidgetConfig
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 8);
        public string EntityId = "";        // first (or only) entity
        public List<string> ExtraEntities = new List<string>(); // further entities shown in the same widget
        public Dictionary<string, string> Labels = new Dictionary<string, string>(); // entity id -> display name
        public string DeviceName = "";      // device the entities were picked from; trimmed off their names
        public string Title = "";         // blank = friendly name from Home Assistant
        public string Mode = "Number";      // one of DisplayModes.All
        public int X = 100;
        public int Y = 100;
        public int W = 220;
        public int H = 110;
        public string BackColor = "#1C1F26";
        public string TitleColor = "";      // blank = a dimmed TextColor (configs from before this setting)
        public string EntityColor = "";     // entity names in a multi-entity widget; blank = TitleColor
        public string TextColor = "#F2F4F8"; // value text
        public string AccentColor = "#18BCF2";
        public int Opacity = 95;            // percent
        public int PollSeconds = 30;
        public double HistoryHours = 1;   // time span shown by Line / Bar
        public int Decimals = 1;            // -1 = show the state exactly as reported
        public double? Min;                 // null = automatic
        public double? Max;
        public string Unit = "";            // blank = unit from Home Assistant
        public bool ShowUnit = true;
        public bool ShowTitle = true;
        public bool TopMost = false;
        public bool Locked = false;

        public WidgetConfig Clone()
        {
            var copy = (WidgetConfig)MemberwiseClone();
            copy.ExtraEntities = new List<string>(ExtraEntities ?? new List<string>());
            copy.Labels = new Dictionary<string, string>(Labels ?? new Dictionary<string, string>());
            return copy;
        }

        // Every entity of the widget, in display order, without blanks or repeats.
        public List<string> AllEntities()
        {
            var all = new List<string>();
            if (!string.IsNullOrEmpty(EntityId)) all.Add(EntityId);
            if (ExtraEntities != null)
                foreach (string id in ExtraEntities)
                    if (!string.IsNullOrEmpty(id) && !all.Contains(id)) all.Add(id);
            return all;
        }
    }

    public class AppConfig
    {
        public string BaseUrl = "http://homeassistant.local:8123";
        [ScriptIgnore]
        public string Token = "";           // in memory only; the file holds TokenEncrypted
        public string TokenEncrypted = "";  // DPAPI, bound to this Windows account on this PC
        public bool IgnoreCertErrors = false;
        public List<WidgetConfig> Widgets = new List<WidgetConfig>();
    }

    static class DisplayModes
    {
        public const string Number = "Number";
        public const string Line = "Line";
        public const string Bar = "Bar";
        public const string Gauge = "Gauge";
        public static readonly string[] All = { Number, Line, Bar, Gauge };
        public static readonly string[] Labels = { "Number", "Line graph", "Bar chart", "Gauge" };
    }

    static class ConfigStore
    {
        public static readonly string Dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
        public static readonly string FilePath = Path.Combine(Dir, "settings.json");

        // Returns a fresh config when the file is missing. An unreadable file is
        // kept as settings.json.bad and reported through 'problem'.
        public static AppConfig Load(out string problem)
        {
            problem = null;
            if (!File.Exists(FilePath)) return new AppConfig();
            try
            {
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                AppConfig cfg = new JavaScriptSerializer().Deserialize<AppConfig>(json);
                if (cfg == null) cfg = new AppConfig();
                if (cfg.Widgets == null) cfg.Widgets = new List<WidgetConfig>();
                cfg.Widgets.RemoveAll(w => w == null);

                if (!string.IsNullOrEmpty(cfg.TokenEncrypted))
                {
                    try
                    {
                        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(cfg.TokenEncrypted), null, DataProtectionScope.CurrentUser);
                        cfg.Token = Encoding.UTF8.GetString(plain);
                    }
                    catch (Exception)
                    {
                        problem = "The saved access token was encrypted on a different PC or Windows account and cannot be read here.\n\nPlease enter the token again.";
                    }
                }
                else
                {
                    // Older versions stored the token as plain text: adopt it and rewrite the file encrypted.
                    var raw = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                    object legacy;
                    if (raw != null && raw.TryGetValue("Token", out legacy) && legacy is string && ((string)legacy).Length > 0)
                    {
                        cfg.Token = (string)legacy;
                        Save(cfg);
                    }
                }
                return cfg;
            }
            catch (Exception ex)
            {
                string bad = FilePath + ".bad";
                try { File.Copy(FilePath, bad, true); } catch (Exception) { }
                problem = "config\\settings.json could not be read and was copied to settings.json.bad.\n\n" + ex.Message;
                return new AppConfig();
            }
        }

        public static void Save(AppConfig cfg)
        {
            Directory.CreateDirectory(Dir);
            cfg.TokenEncrypted = string.IsNullOrEmpty(cfg.Token)
                ? ""
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(cfg.Token), null, DataProtectionScope.CurrentUser));
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, Pretty(new JavaScriptSerializer().Serialize(cfg)), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        static string Pretty(string json)
        {
            var sb = new StringBuilder(json.Length * 2);
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\') sb.Append(json[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                        {
                            sb.Append(c).Append(json[++i]);
                            break;
                        }
                        sb.Append(c);
                        NewLine(sb, ++depth);
                        break;
                    case '}':
                    case ']':
                        NewLine(sb, --depth);
                        sb.Append(c);
                        break;
                    case ',':
                        sb.Append(c);
                        NewLine(sb, depth);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static void NewLine(StringBuilder sb, int depth)
        {
            sb.Append("\r\n").Append(' ', depth * 2);
        }
    }

    static class Colors
    {
        public static Color Parse(string hex, Color fallback)
        {
            try
            {
                if (!string.IsNullOrEmpty(hex)) return ColorTranslator.FromHtml(hex);
            }
            catch (Exception) { }
            return fallback;
        }

        // Title colour when none is set: the value colour faded towards the background.
        public static Color DefaultTitle(Color text, Color back)
        {
            const int A = 170;
            return Color.FromArgb(
                (text.R * A + back.R * (255 - A)) / 255,
                (text.G * A + back.G * (255 - A)) / 255,
                (text.B * A + back.B * (255 - A)) / 255);
        }

        public static string ToHex(Color c)
        {
            return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B);
        }
    }
}
