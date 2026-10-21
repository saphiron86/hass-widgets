using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HassWidgets
{
    struct Sample
    {
        public DateTime T;   // UTC
        public double V;
    }

    class EntityState
    {
        public string State = "";
        public string Unit = "";
        public string FriendlyName = "";
    }

    class EntityInfo
    {
        public string Id = "";
        public string DeviceId = "";    // blank = the entity has no device
        public string DeviceName = "";
    }

    class HassException : Exception
    {
        public HassException(string message) : base(message) { }
    }

    // Minimal Home Assistant REST client (long-lived access token).
    static class Hass
    {
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public static string BaseUrl = "";
        public static string Token = "";
        public static bool IgnoreCertErrors;

        static Hass()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            ServicePointManager.ServerCertificateValidationCallback =
                (sender, cert, chain, errors) => errors == SslPolicyErrors.None || IgnoreCertErrors;
        }

        public static void Configure(AppConfig cfg)
        {
            BaseUrl = NormalizeUrl(cfg.BaseUrl);
            Token = (cfg.Token ?? "").Trim();
            IgnoreCertErrors = cfg.IgnoreCertErrors;
        }

        public static bool IsConfigured
        {
            get { return BaseUrl.Length > 0 && Token.Length > 0; }
        }

        public static string NormalizeUrl(string url)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            if (url.Length > 0 && url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;
            return url;
        }

        public static async Task<string> Send(HttpMethod method, string baseUrl, string token, string path, string body)
        {
            using (var req = new HttpRequestMessage(method, baseUrl + path))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using (HttpResponseMessage resp = await http.SendAsync(req).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new HassException("Unauthorized - check the access token");
                    if (resp.StatusCode == HttpStatusCode.NotFound) throw new HassException("Not found");
                    if (!resp.IsSuccessStatusCode) throw new HassException("HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase);
                    return text;
                }
            }
        }

        public static async Task<EntityState> GetState(string entityId)
        {
            string json;
            try
            {
                json = await Send(HttpMethod.Get, BaseUrl, Token, "/api/states/" + Uri.EscapeDataString(entityId), null).ConfigureAwait(false);
            }
            catch (HassException ex)
            {
                if (ex.Message == "Not found") throw new HassException("Entity not found: " + entityId);
                throw;
            }
            var d = NewSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (d == null) throw new HassException("Unexpected response");
            var st = new EntityState();
            st.State = Str(d, "state");
            var attrs = Get(d, "attributes") as Dictionary<string, object>;
            if (attrs != null)
            {
                st.Unit = Str(attrs, "unit_of_measurement");
                st.FriendlyName = Str(attrs, "friendly_name");
            }
            return st;
        }

        // Numeric history from the recorder, oldest first.
        public static async Task<List<Sample>> GetHistory(string entityId, double hours)
        {
            string start = DateTime.UtcNow.AddHours(-hours).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            string path = "/api/history/period/" + start + "?filter_entity_id=" + Uri.EscapeDataString(entityId) + "&minimal_response&no_attributes";
            string json = await Send(HttpMethod.Get, BaseUrl, Token, path, null).ConfigureAwait(false);

            var result = new List<Sample>();
            var outer = NewSerializer().DeserializeObject(json) as object[];
            if (outer == null || outer.Length == 0) return result;
            var rows = outer[0] as object[];
            if (rows == null) return result;
            foreach (object row in rows)
            {
                var d = row as Dictionary<string, object>;
                if (d == null) continue;
                double v;
                DateTimeOffset t;
                if (!TryValue(Str(d, "state"), out v)) continue;
                if (!DateTimeOffset.TryParse(Str(d, "last_changed"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t)) continue;
                result.Add(new Sample { T = t.UtcDateTime, V = v });
            }
            result.Sort((a, b) => a.T.CompareTo(b.T));
            return result;
        }

        // Home Assistant caps template output at 262144 characters, so the entity
        // lists are fetched a page at a time.
        const int PageSize = 500;

        // A device without a name of its own (e.g. a scanned network client) is
        // labelled with the name of its entity.
        const string CatalogTemplate =
            "{% set ns = namespace(r=[]) %}" +
            "{% for s in (states | list)[@FROM:@TO] %}" +
            "{% set d = device_id(s.entity_id) %}" +
            "{% set n = (device_attr(d, 'name_by_user') or device_attr(d, 'name') or s.name or '') if d else '' %}" +
            "{% set ns.r = ns.r + [[s.entity_id, d or '', n]] %}" +
            "{% endfor %}{{ ns.r | to_json }}";

        // Every entity with the device it belongs to, sorted by entity id. The
        // REST API has no device endpoint, so a template asks the device registry.
        // If that template is rejected, the entities come back without devices.
        public static async Task<List<EntityInfo>> GetEntityCatalog()
        {
            var result = new List<EntityInfo>();
            try
            {
                for (int from = 0; ; from += PageSize)
                {
                    string text = await RenderPage(CatalogTemplate, from).ConfigureAwait(false);
                    var rows = NewSerializer().DeserializeObject(text) as object[];
                    if (rows == null) throw new HassException("Unexpected response");
                    foreach (object row in rows)
                    {
                        var cells = row as object[];
                        if (cells == null || cells.Length < 3) continue;
                        result.Add(new EntityInfo
                        {
                            Id = Convert.ToString(cells[0], CultureInfo.InvariantCulture),
                            DeviceId = Convert.ToString(cells[1], CultureInfo.InvariantCulture) ?? "",
                            DeviceName = (Convert.ToString(cells[2], CultureInfo.InvariantCulture) ?? "").Trim()
                        });
                    }
                    if (rows.Length < PageSize) break;
                }
            }
            catch (HassException) { result.Clear(); }
            catch (ArgumentException) { result.Clear(); }          // not valid JSON
            catch (InvalidOperationException) { result.Clear(); }

            if (result.Count == 0)
            {
                foreach (string id in await GetEntityIds().ConfigureAwait(false))
                    result.Add(new EntityInfo { Id = id, DeviceId = "", DeviceName = "" });
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return result;
        }

        // Rendered server-side so only the ids travel, not every entity's full state.
        public static async Task<string[]> GetEntityIds()
        {
            var ids = new List<string>();
            for (int from = 0; ; from += PageSize)
            {
                string text = await RenderPage("{{ (states | map(attribute='entity_id') | list)[@FROM:@TO] | join(',') }}", from).ConfigureAwait(false);
                string[] page = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string id in page) ids.Add(id.Trim());
                if (page.Length < PageSize) break;
            }
            ids.Sort(StringComparer.Ordinal);
            return ids.ToArray();
        }

        static Task<string> RenderPage(string template, int from)
        {
            var body = new Dictionary<string, object>();
            body["template"] = template
                .Replace("@FROM", from.ToString(CultureInfo.InvariantCulture))
                .Replace("@TO", (from + PageSize).ToString(CultureInfo.InvariantCulture));
            return Send(HttpMethod.Post, BaseUrl, Token, "/api/template", NewSerializer().Serialize(body));
        }

        // Numbers parse as-is; on/off map to 1/0 so binary entities can be graphed.
        public static bool TryValue(string state, out double value)
        {
            if (double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return !double.IsNaN(value) && !double.IsInfinity(value);
            if (state == "on") { value = 1; return true; }
            if (state == "off") { value = 0; return true; }
            value = 0;
            return false;
        }

        public static string Describe(Exception ex)
        {
            if (ex is TaskCanceledException) return "Timed out";
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex.Message;
        }

        static JavaScriptSerializer NewSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object o;
            return d.TryGetValue(key, out o) ? o : null;
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object o = Get(d, key);
            return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture);
        }
    }
}
