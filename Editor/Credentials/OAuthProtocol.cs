using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal sealed class LoopbackAddress
    {
        internal string Original, Path, Query, BeforePort, AfterPort;
        internal int Port;
        private static Match Syntax(string value, out int port)
        {
            if (value == null || value.Length > 2048 || value.Any(c => c < 33 || c > 126 || c == '\\' || c == '#')) throw new RegistryException("callback_uri");
            var m = Regex.Match(value, @"^http://127\.0\.0\.1:([1-9][0-9]{0,4})(/[^?]*)(?:\?(.*))?$");
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out port) || port > 65535) throw new RegistryException("callback_uri");
            for (int i = 0; i < value.Length; i++) if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))) throw new RegistryException("callback_uri");
            return m;
        }
        internal static void ValidateDynamicCandidate(string value)
        {
            int port;
            var match = Syntax(value, out port);
            string path = match.Groups[2].Value;
            // The protocol disallows pathname normalization; callback support is checked separately.
            if (path.IndexOfAny(new[] { '"', '<', '>', '`', '{', '}' }) >= 0) throw new RegistryException("callback_uri");
            foreach (string segment in path.Split('/'))
            {
                string decoded = Uri.UnescapeDataString(segment);
                if (decoded == "." || decoded == "..") throw new RegistryException("callback_uri");
            }
        }
        internal static LoopbackAddress Parse(string value)
        {
            int port;
            var m = Syntax(value, out port);
            string path = m.Groups[2].Value, query = m.Groups[3].Value;
            foreach (string segment in path.Split('/'))
            {
                string decoded = Uri.UnescapeDataString(segment);
                if (decoded == "." || decoded == ".." || decoded.Any(c => c < 32 || c == 127 || c == '\\' || c == '/' || c == '?')) throw new RegistryException("callback_uri");
            }
            foreach (string part in query.Split('&'))
            {
                string key = Decode(part.Split('=')[0]);
                if (new[] { "code", "state", "error", "error_description" }.Contains(key, StringComparer.Ordinal)) throw new RegistryException("callback_uri");
            }
            return new LoopbackAddress { Original = value, Path = path, Query = query, Port = port, BeforePort = value.Substring(0, m.Groups[1].Index), AfterPort = value.Substring(m.Groups[1].Index + m.Groups[1].Length) };
        }
        internal string WithPort(int port) { return BeforePort + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + AfterPort; }
        internal static string Decode(string value)
        {
            for (int i = 0; i < value.Length; i++) if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))) throw new RegistryException("callback_encoding");
            return Uri.UnescapeDataString(value.Replace("+", " "));
        }
        internal Dictionary<string, string> Parameters(string target, string state)
        {
            int question = target.IndexOf('?');
            if (question < 0 || target.Substring(0, question) != Path) return null;
            string query = target.Substring(question + 1);
            if (Query.Length > 0)
            {
                if (!query.StartsWith(Query + "&", StringComparison.Ordinal)) return null;
                query = query.Substring(Query.Length + 1);
            }
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in query.Split('&'))
            {
                int equal = part.IndexOf('='); if (equal <= 0) return null;
                string key = Decode(part.Substring(0, equal)), value = Decode(part.Substring(equal + 1));
                if (result.ContainsKey(key) || !new[] { "code", "state", "error", "error_description" }.Contains(key) || value.Length == 0 || value.Any(c => c < 32 || c == 127)) return null;
                result.Add(key, value);
            }
            string incoming;
            if (!result.TryGetValue("state", out incoming) || !Same(incoming, state)) return null;
            bool code = result.ContainsKey("code"), error = result.ContainsKey("error");
            if (code == error || (code && result.Count != 2) || (error && result.Count != (result.ContainsKey("error_description") ? 3 : 2))) return null;
            return result;
        }
        private static bool Same(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int different = 0; for (int i = 0; i < a.Length; i++) different |= a[i] ^ b[i]; return different == 0;
        }
    }
    internal sealed class OAuthConfiguration
    {
        internal Uri Authorize, Token, User;
        internal LoopbackAddress Callback;
        internal bool Dynamic;
        internal static OAuthConfiguration Parse(string text, Uri proxy)
        {
            var root = Data.Object(text, 65536);
            if (root["protocolVersion"] == null || root["protocolVersion"].Type != JTokenType.Integer || (long)root["protocolVersion"] != 1) throw new RegistryException("oauth_protocol");
            var oauth = root["oauth"] as JObject;
            if (oauth == null || oauth["enabled"] == null || oauth["enabled"].Type != JTokenType.Boolean || !(bool)oauth["enabled"]) throw new RegistryException("oauth_disabled");
            if (!Strings(oauth["grantTypes"]).Contains("authorization_code") || !Strings(oauth["grantTypes"]).Contains("refresh_token") || !Strings(oauth["scopes"]).Contains("read_api") || !Strings(oauth["codeChallengeMethods"]).Contains("S256") || (string)oauth["stateVerifiedBy"] != "client" || oauth["clientIsShared"]?.Type != JTokenType.Boolean || !(bool)oauth["clientIsShared"]) throw new RegistryException("oauth_capabilities");
            Data.RequiredString(oauth, "clientId");
            var endpoints = oauth["endpoints"] as JObject; if (endpoints == null) throw new RegistryException("oauth_endpoints");
            var config = new OAuthConfiguration { Authorize = Endpoint(endpoints, "authorize", proxy), Token = Endpoint(endpoints, "token", proxy), User = Endpoint(endpoints, "user", proxy) };
            var redirects = Strings(oauth["redirectUris"]);
            var dynamic = oauth["loopbackDynamicPortRedirectUris"] as JArray;
            if (dynamic != null && dynamic.Count > 0)
            {
                try
                {
                    var candidates = Strings(dynamic);
                    if (candidates.Any(v => !redirects.Contains(v, StringComparer.Ordinal)) || candidates.Distinct().Count() != candidates.Length) throw new RegistryException("callback_uri");
                    foreach (string candidate in candidates) LoopbackAddress.ValidateDynamicCandidate(candidate);
                    foreach (string candidate in candidates)
                    {
                        try { config.Callback = LoopbackAddress.Parse(candidate); config.Dynamic = true; break; }
                        catch (RegistryException) { }
                    }
                }
                catch (RegistryException) { config.Callback = null; }
            }
            if (config.Callback == null)
                foreach (string redirect in redirects)
                    try { config.Callback = LoopbackAddress.Parse(redirect); break; } catch (RegistryException) { }
            if (config.Callback == null) throw new RegistryException("callback_unavailable");
            return config;
        }
        private static string[] Strings(JToken token)
        {
            var array = token as JArray;
            if (array == null || array.Count > 128 || array.Any(v => v.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)v))) throw new RegistryException("oauth_capabilities");
            return array.Values<string>().ToArray();
        }
        private static Uri Endpoint(JObject endpoints, string name, Uri proxy)
        {
            string path = Data.RequiredString(endpoints, name);
            string expected = proxy.AbsolutePath.TrimEnd('/') + "/auth/" + name;
            if (path != expected || !path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Any(c => c < 33 || c == '\\' || c == '?' || c == '#')) throw new RegistryException("oauth_endpoints");
            var uri = new Uri(new Uri(proxy.GetLeftPart(UriPartial.Authority)), path);
            if (uri.GetLeftPart(UriPartial.Authority) != proxy.GetLeftPart(UriPartial.Authority)) throw new RegistryException("oauth_endpoints");
            return uri;
        }
        internal string AuthorizationUrl(string redirect, string state, string verifier)
        {
            string challenge; using (var hash = SHA256.Create()) challenge = Data.Base64Url(hash.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            string query = OAuthClient.Form(new Dictionary<string, string> { { "redirect_uri", redirect }, { "state", state }, { "code_challenge", challenge }, { "code_challenge_method", "S256" }, { "scope", "read_api" }, { "response_type", "code" } });
            if (Encoding.UTF8.GetByteCount(query) > 4096) throw new RegistryException("oauth_request_size");
            return Authorize.AbsoluteUri + "?" + query;
        }
    }
    internal sealed class TokenPair
    {
        internal string Access, Refresh;
        internal long ExpiresAt, RefreshAt;
        internal static TokenPair Parse(string text, long now)
        {
            var value = Data.Object(text, 65536);
            string access = Data.RequiredString(value, "access_token"), refresh = Data.RequiredString(value, "refresh_token");
            Data.ValidateToken(access); Data.ValidateToken(refresh);
            if (!string.Equals(Data.RequiredString(value, "token_type"), "bearer", StringComparison.OrdinalIgnoreCase)) throw new RegistryException("token_response");
            var expires = value["expires_in"];
            if (expires == null || (expires.Type != JTokenType.Integer && expires.Type != JTokenType.Float)) throw new RegistryException("token_response");
            double seconds = (double)expires;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0 || seconds > 253402300799L - now) throw new RegistryException("token_response");
            long issued = now;
            if (value["created_at"] != null)
            {
                if (value["created_at"].Type != JTokenType.Integer || (long)value["created_at"] < 0) throw new RegistryException("token_response");
                issued = Math.Min(now, (long)value["created_at"]);
            }
            long duration = Math.Max(1, (long)Math.Floor(seconds));
            if (issued + duration <= now) throw new RegistryException("token_response");
            return new TokenPair { Access = access, Refresh = refresh, ExpiresAt = issued + duration, RefreshAt = Math.Max(now + 30, issued + Math.Max(duration / 2, duration - 60)) };
        }
        internal void Apply(CredentialRecord record) { record.Access = Access; record.Refresh = Refresh; record.ExpiresAt = ExpiresAt; record.RefreshAt = RefreshAt; record.InFlight = false; record.NeedsLogin = false; record.RetryAt = 0; }
    }
    internal sealed class OAuthClient : IDisposable
    {
        private readonly HttpClient client;
        internal readonly Uri Proxy;
        internal OAuthClient(string proxy, bool allowTestHttp = false)
        {
            Proxy = UrlRules.Proxy(proxy, allowTestHttp);
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) { Timeout = TimeSpan.FromSeconds(40) };
        }
        internal async Task<OAuthConfiguration> Configuration(CancellationToken cancellation)
        {
            return OAuthConfiguration.Parse(await Send(new HttpRequestMessage(HttpMethod.Get, Proxy.AbsoluteUri.TrimEnd('/') + "/auth/config"), cancellation), Proxy);
        }
        internal async Task<TokenPair> Exchange(OAuthConfiguration configuration, Dictionary<string, string> values, CancellationToken cancellation)
        {
            string form = Form(values);
            if (Encoding.UTF8.GetByteCount(form) > 8192) throw new RegistryException("oauth_request_size");
            var request = new HttpRequestMessage(HttpMethod.Post, configuration.Token) { Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded") };
            return TokenPair.Parse(await Send(request, cancellation), Data.Now);
        }
        internal async Task<string> Account(OAuthConfiguration configuration, string access, CancellationToken cancellation)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, configuration.User);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            var user = Data.Object(await Send(request, cancellation), 65536);
            if (user["id"] == null || user["id"].Type != JTokenType.Integer || (long)user["id"] <= 0) throw new RegistryException("user_response");
            string name = Data.RequiredString(user, "username");
            if (name.Length > 256 || name.Any(c => c < 32 || c == 127)) throw new RegistryException("user_response");
            return name;
        }
        private async Task<string> Send(HttpRequestMessage request, CancellationToken cancellation)
        {
            using (request)
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(40));
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var memory = new MemoryStream())
                {
                    byte[] buffer = new byte[4096]; int count;
                    while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token)) != 0)
                    {
                        if (memory.Length + count > 65536) throw new RegistryException("response_size"); memory.Write(buffer, 0, count);
                    }
                    string body = Data.Utf8.GetString(memory.ToArray());
                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden) throw new RegistryException("permission_denied");
                        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400) throw new RegistryException("redirect_rejected");
                        throw new RegistryException("oauth_http_" + (int)response.StatusCode);
                    }
                    return body;
                }
            }
        }
        internal static string Form(IEnumerable<KeyValuePair<string, string>> values) { return string.Join("&", values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))); }
        public void Dispose() { client.Dispose(); }
    }
}
