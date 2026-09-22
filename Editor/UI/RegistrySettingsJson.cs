using System;
using System.Collections.Generic;
using UnityEngine;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal static class RegistrySettingsJson
    {
        private const int MaximumJsonLength = 1024 * 1024;
        private const string InvalidSettingsMessage = "レジストリ設定のJSONが不正です。名前・http(s)のURL・スコープ配列を確認してください。";

        [Serializable]
        internal sealed class Entry
        {
            public string name;
            public string url;
            public string[] scopes;
        }

        [Serializable]
        private sealed class Document
        {
            public Entry[] scopedRegistries;
        }

        internal static string Serialize(Entry[] entries)
        {
            var document = new Document { scopedRegistries = Validate(entries) };
            var json = JsonUtility.ToJson(document, true);
            if (json.Length > MaximumJsonLength) throw InvalidSettings();
            return json;
        }

        internal static Entry[] Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonLength)
                throw InvalidSettings();

            var trimmed = json.Trim();
            if (trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}')
                throw InvalidSettings();

            Document document;
            try
            {
                // Only the fields in Document/Entry are consumed; unknown fields are discarded.
                document = JsonUtility.FromJson<Document>(trimmed);
            }
            catch (ArgumentException)
            {
                // Do not expose parser diagnostics, which may contain imported secrets.
                throw InvalidSettings();
            }
            return Validate(document?.scopedRegistries);
        }

        internal static Entry[] Validate(Entry[] entries)
        {
            if (entries == null || entries.Length == 0) throw InvalidSettings();

            var result = new Entry[entries.Length];
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry == null || string.IsNullOrWhiteSpace(entry.name) ||
                    string.IsNullOrWhiteSpace(entry.url) || entry.scopes == null)
                    throw InvalidSettings();

                var url = entry.url.Trim().TrimEnd('/');
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                    string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                    !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                    !urls.Add(url))
                    throw InvalidSettings();

                var scopes = new List<string>();
                var seenScopes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scope in entry.scopes)
                {
                    if (string.IsNullOrWhiteSpace(scope)) throw InvalidSettings();
                    var normalized = scope.Trim();
                    if (seenScopes.Add(normalized)) scopes.Add(normalized);
                }

                result[index] = new Entry { name = entry.name.Trim(), url = url, scopes = scopes.ToArray() };
            }
            return result;
        }

        private static ArgumentException InvalidSettings() => new ArgumentException(InvalidSettingsMessage);
    }
}
