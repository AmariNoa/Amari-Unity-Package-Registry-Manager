using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal enum ConnectionOutcome { Reachable, CredentialRequired, Denied, Undetermined }
    internal sealed class ConnectionResult
    {
        internal ConnectionOutcome Outcome;
        internal int Status;
        internal bool LoginRequired;
        internal string Url;
        internal bool Attempted;
        // Authorization scheme actually sent ("Bearer"/"Basic"), or null when no request or no credential was sent.
        internal string SentScheme;
    }
    internal sealed class RegistryService
    {
        internal readonly ProtectedSettings Settings;
        private readonly bool allowTestHttp;
        // Static: services are created per operation, and one browser sign-in per Editor is the supported flow.
        private static int signingIn;
        internal RegistryService(ProtectedSettings settings, bool allowTestHttp = false) { Settings = settings; this.allowTestHttp = allowTestHttp; }
        private void ValidateUrl(string url)
        {
            UrlRules.Registry(url);
            foreach (var entry in Settings.Summaries())
                if (entry.Url != url && UrlRules.Key(entry.Url) == UrlRules.Key(url)) throw new RegistryException("registry_url_alias");
        }
        internal void SaveToken(string url, string token, CredentialKind kind, string expected)
        {
            ValidateUrl(url); Data.ValidateToken(token);
            if (kind != CredentialKind.Pat && kind != CredentialKind.Npm) throw new RegistryException("token_kind");
            using (Settings.Lock("credential:" + UrlRules.Key(url)))
                Settings.Change(url, expected, old => new CredentialRecord { Kind = kind, Access = token, ConfigurationPath = Settings.Paths.ConfigurationFile }, true);
        }
        internal void SaveBasic(string url, string username, string password, string expected)
        {
            ValidateUrl(url);
            string auth = Data.BasicAuth(username, password);
            using (Settings.Lock("credential:" + UrlRules.Key(url)))
                Settings.Change(url, expected, old => new CredentialRecord { Kind = CredentialKind.Basic, Access = auth, Account = username, ConfigurationPath = Settings.Paths.ConfigurationFile }, true);
        }
        internal void Delete(string url, string expected)
        {
            ValidateUrl(url);
            using (Settings.Lock("credential:" + UrlRules.Key(url))) Settings.Change(url, expected, old => null, true);
        }
        internal async Task Login(string url, string proxy, Action<string> browser, CancellationToken cancellation)
        {
            ValidateUrl(url);
            if (Interlocked.CompareExchange(ref signingIn, 1, 0) != 0) throw new RegistryException("login_busy");
            try
            {
                string stamp = Settings.Stamp(url);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var client = new OAuthClient(proxy, allowTestHttp))
                {
                    deadline.CancelAfter(TimeSpan.FromMinutes(5));
                    var configuration = await client.Configuration(deadline.Token);
                    string state = Data.RandomValue(), verifier = Data.RandomValue(), code, redirect;
                    using (var listener = new LoopbackListener(configuration.Callback, configuration.Dynamic, state, deadline.Token))
                    {
                        redirect = listener.Redirect;
                        browser(configuration.AuthorizationUrl(redirect, state, verifier));
                        code = await listener.Wait();
                    }
                    using (Settings.Lock("credential:" + UrlRules.Key(url)))
                    {
                        if (Settings.Stamp(url) != stamp) throw new RegistryException("settings_conflict");
                        var pair = await client.Exchange(configuration, new Dictionary<string, string> { { "grant_type", "authorization_code" }, { "code", code }, { "code_verifier", verifier }, { "redirect_uri", redirect } }, deadline.Token);
                        string account = null;
                        // The user lookup is optional, but cancellation (caller or deadline) must abort before commit.
                        try { account = await client.Account(configuration, pair.Access, deadline.Token); }
                        catch (Exception) { deadline.Token.ThrowIfCancellationRequested(); }
                        deadline.Token.ThrowIfCancellationRequested();
                        var record = new CredentialRecord { Kind = CredentialKind.OAuth, Proxy = proxy, Redirect = redirect, Account = account, ConfigurationPath = Settings.Paths.ConfigurationFile };
                        pair.Apply(record);
                        Settings.Change(url, stamp, old => record, true);
                    }
                }
            }
            finally { Interlocked.Exchange(ref signingIn, 0); }
        }
        internal async Task<int> Check(string url, string package, CancellationToken cancellation)
        {
            ValidateUrl(url);
            if (string.IsNullOrWhiteSpace(package) || package.Length > 214 || !System.Text.RegularExpressions.Regex.IsMatch(package, @"^[a-z0-9][a-z0-9._-]*$")) throw new RegistryException("package_name");
            var response = await Get(url, "/" + Uri.EscapeDataString(package), cancellation);
            if (response.Status < 200 || response.Status >= 300) return response.Status;
            var metadata = Data.Object(response.Body, 2097152);
            if (Data.RequiredString(metadata, "name") != package || metadata["versions"]?.Type != Newtonsoft.Json.Linq.JTokenType.Object) throw new RegistryException("metadata_response");
            return response.Status;
        }
        // Reachability of the registry search API with the credential UPM would send. It does not prove that
        // authentication is unnecessary, that packages install, or that every operation is permitted.
        internal async Task<ConnectionResult> CheckConnection(string url, string scope, CancellationToken cancellation)
        {
            ValidateUrl(url);
            cancellation.ThrowIfCancellationRequested();
            var result = new ConnectionResult { Outcome = ConnectionOutcome.Undetermined, Url = url };
            var record = Settings.Read().Records.FirstOrDefault(r => r.Url == url);
            if (record != null && record.Kind == CredentialKind.OAuth)
            {
                if (record.InFlight || record.NeedsLogin) result.LoginRequired = true;
                else if (RefreshDue(record, Settings.Paths.ConfigurationFile, Data.Now))
                {
                    try { await Refresh(url, cancellation); }
                    catch (Exception)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        result.LoginRequired = true;
                        return result;
                    }
                }
            }
            HttpResponse response;
            try { response = await Get(url, "/-/v1/search?text=" + Uri.EscapeDataString((scope ?? "").Trim()) + "&size=1", cancellation, scheme => { result.Attempted = true; result.SentScheme = scheme; }); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return result; }
            catch (Exception error) when (error is HttpRequestException || error is IOException || error is System.Net.WebException || error is System.Net.Sockets.SocketException || error is System.Security.Authentication.AuthenticationException || (error is RegistryException && ((RegistryException)error).Code == "response_size"))
            {
                cancellation.ThrowIfCancellationRequested();
                return result;
            }
            result.Status = response.Status;
            if (response.Status == 401) result.Outcome = ConnectionOutcome.CredentialRequired;
            else if (response.Status == 403) result.Outcome = ConnectionOutcome.Denied;
            else if (response.Status >= 200 && response.Status < 300 && IsSearchResult(response.Body)) result.Outcome = ConnectionOutcome.Reachable;
            return result;
        }
        private static bool IsSearchResult(string body)
        {
            try { return body != null && Data.Object(body, 2097152)["objects"]?.Type == Newtonsoft.Json.Linq.JTokenType.Array; }
            catch (RegistryException) { return false; }
        }
        internal static bool RefreshDue(CredentialRecord record, string configurationPath, long now)
        {
            return record.Kind == CredentialKind.OAuth && !record.InFlight && !record.NeedsLogin && record.RefreshAt <= now && record.RetryAt <= now &&
                string.Equals(record.ConfigurationPath, configurationPath, StringComparison.OrdinalIgnoreCase);
        }
        // GET with the exact-URL credential from the effective UPM TOML: token as Bearer, else _auth as Basic, else none.
        // Returns the status and, for 2xx only, the UTF-8 body (at most 2 MiB). Redirects are never followed.
        // The credential is read from the TOML at send time, so it is the one UPM would use now, not a cached view.
        private sealed class HttpResponse
        {
            internal int Status;
            internal string Body;
        }
        private async Task<HttpResponse> Get(string url, string target, CancellationToken cancellation, Action<string> sent = null)
        {
            var document = new TomlDocument(PrivateFiles.Decode(PrivateFiles.Read(Settings.Paths.ConfigurationFile)));
            string token = document.Token(url), basic = token == null ? document.Basic(url) : null;
            sent?.Invoke(token != null ? "Bearer" : basic != null ? "Basic" : null);
            using (var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) })
            using (var request = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + target))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                if (token != null) { Data.ValidateToken(token); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); }
                else if (basic != null) { Data.ValidateToken(basic); request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic); }
                deadline.CancelAfter(TimeSpan.FromSeconds(40));
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
                {
                    int status = (int)response.StatusCode;
                    if (status < 200 || status >= 300) return new HttpResponse { Status = status };
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var memory = new MemoryStream())
                    {
                        var buffer = new byte[8192]; int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, deadline.Token)) > 0)
                        {
                            if (memory.Length + count > 2097152) throw new RegistryException("response_size");
                            memory.Write(buffer, 0, count);
                        }
                        string body;
                        try { body = Data.Utf8.GetString(memory.ToArray()); }
                        catch (System.Text.DecoderFallbackException) { body = null; }
                        return new HttpResponse { Status = status, Body = body };
                    }
                }
            }
        }
        internal async Task Refresh(string url, CancellationToken cancellation)
        {
            ValidateUrl(url);
            using (Settings.Lock("credential:" + UrlRules.Key(url)))
            {
                var record = Settings.Read().Records.FirstOrDefault(r => r.Url == url);
                if (record == null || record.Kind != CredentialKind.OAuth) throw new RegistryException("oauth_missing");
                if (!string.Equals(record.ConfigurationPath, Settings.Paths.ConfigurationFile, StringComparison.OrdinalIgnoreCase)) throw new RegistryException("configuration_path_changed");
                if (record.InFlight || record.NeedsLogin) throw new RegistryException("oauth_login_required");
                if (Data.Now < record.RetryAt) throw new RegistryException("oauth_retry_later");
                using (var client = new OAuthClient(record.Proxy, allowTestHttp))
                {
                    string stamp = Settings.Stamp(url);
                    Settings.Change(url, stamp, old => { old.InFlight = true; old.RetryAt = Data.Now + 30; return old; }, false);
                    stamp = Settings.Stamp(url);
                    try
                    {
                        var configuration = await client.Configuration(cancellation);
                        var pair = await client.Exchange(configuration, new Dictionary<string, string> { { "grant_type", "refresh_token" }, { "refresh_token", record.Refresh }, { "redirect_uri", record.Redirect } }, cancellation);
                        Settings.Change(url, stamp, old => { pair.Apply(old); return old; }, true);
                    }
                    catch (Exception)
                    {
                        try { Settings.Change(url, stamp, old => { old.NeedsLogin = true; return old; }, false); } catch (Exception) { }
                        throw new RegistryException("oauth_login_required");
                    }
                }
            }
        }
    }
    // Single construction point so UI and scheduler tests can redirect every path away from the user's real store/TOML.
    internal static class CredentialServices
    {
        internal static Func<RegistryService> Factory;
        internal static RegistryService Create()
        {
            if (Factory != null) return Factory();
            return new RegistryService(new ProtectedSettings(UserPaths.Default(Path.GetDirectoryName(UnityEngine.Application.dataPath))));
        }
        // Cheap change marker for the store and effective TOML; no decryption.
        internal static string ChangeMarker(RegistryService service)
        {
            return Time(service.Settings.StorePath) + "|" + Time(service.Settings.Paths.ConfigurationFile) + "|" + service.Settings.Paths.ConfigurationFile;
        }
        private static long Time(string path) { try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0; } catch (Exception) { return -1; } }
    }
}
