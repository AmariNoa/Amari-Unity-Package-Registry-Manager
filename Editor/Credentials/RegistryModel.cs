using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("com.amari-noa.amari-unity-package-registry-manager.tests.editor")]
namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal sealed class RegistryException : Exception
    {
        internal string Code { get; private set; }
        internal RegistryException(string code) : base(code) { Code = code; }
    }
    // Append only: stored records serialize the numeric value.
    internal enum CredentialKind { Pat, Npm, OAuth, Basic }
    internal sealed class CredentialRecord
    {
        public string Url;
        public CredentialKind Kind;
        public string Access;
        public string Refresh;
        public string Proxy;
        public string Redirect;
        public string Account;
        public string Generation;
        public long ExpiresAt;
        public long RefreshAt;
        public bool InFlight;
        public bool NeedsLogin;
        public long RetryAt;
        public string ConfigurationPath;
        internal CredentialRecord Copy() { return (CredentialRecord)MemberwiseClone(); }
    }
    internal sealed class CredentialSummary
    {
        internal string Url;
        internal string Kind;
        internal string Account;
        internal long ExpiresAt;
        internal bool NeedsLogin;
        internal bool InFlight;
        internal bool Configured;
        internal bool External;
        internal string ConfigurationPath;
    }
    internal static class UrlRules
    {
        internal static Uri Registry(string value)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(c => c <= 32 || c == '\\') || !Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new RegistryException("registry_url");
            return uri;
        }
        internal static Uri Proxy(string value, bool allowTestHttp)
        {
            var uri = Registry(value);
            if (uri.Scheme != "https" && !(allowTestHttp && uri.Host == "127.0.0.1")) throw new RegistryException("proxy_https_required");
            return uri;
        }
        internal static string Key(string value)
        {
            var uri = Registry(value);
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/').Insert(0, "/");
        }
    }
    internal static class Data
    {
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static string Hash(byte[] value)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(value));
        }
        internal static string Hash(string value) { return Hash(Utf8.GetBytes(value)); }
        internal static string RandomValue()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }
        internal static string Base64Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        internal static long Now { get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } }
        internal static JObject Object(string text, int max = 1048576)
        {
            if (text == null || text.Length > max) throw new RegistryException("json_size");
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
                {
                    var result = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error, CommentHandling = CommentHandling.Ignore });
                    while (reader.Read()) if (reader.TokenType != JsonToken.Comment) throw new RegistryException("json_trailing");
                    return result;
                }
            }
            catch (JsonException) { throw new RegistryException("json_invalid"); }
        }
        internal static string RequiredString(JObject value, string key)
        {
            var token = value[key];
            if (token == null || token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)token)) throw new RegistryException("response_invalid");
            return (string)token;
        }
        internal static string Quote(string value) { return JsonConvert.SerializeObject(value); }
        internal static void ValidateToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 8192 || value.Any(c => c < 32 || c == 127)) throw new RegistryException("token_invalid");
        }
        // npm _auth value: Base64(UTF-8 "username:password"). Base64 is encoding, not protection.
        internal static string BasicAuth(string username, string password)
        {
            if (string.IsNullOrEmpty(username) || username.Length > 1024 || username.Any(c => c < 32 || c == 127 || c == ':')) throw new RegistryException("basic_username");
            if (string.IsNullOrEmpty(password) || password.Length > 4096 || password.Any(c => c < 32 || c == 127)) throw new RegistryException("basic_password");
            try { return Convert.ToBase64String(Utf8.GetBytes(username + ":" + password)); }
            catch (EncoderFallbackException) { throw new RegistryException("basic_password"); }
        }
        internal static string SafeError(Exception error)
        {
            var known = error as RegistryException;
            if (known != null) return known.Code;
            if (error is OperationCanceledException) return "cancelled";
            if (error is System.Net.Http.HttpRequestException) return "network_error";
            if (error is UnauthorizedAccessException) return "file_access";
            if (error is IOException) return "file_io";
            return "operation_failed";
        }
    }
}
