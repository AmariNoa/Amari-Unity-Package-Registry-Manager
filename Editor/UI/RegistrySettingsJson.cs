using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal static class RegistrySettingsJson
    {
        private const int MaximumJsonLength = 1024 * 1024;

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

        internal static string WriteCatalog(Entry[] entries)
        {
            if (entries == null) throw InvalidSettings();
            var stored = new Entry[entries.Length];
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (entry == null || string.IsNullOrWhiteSpace(entry.name) || string.IsNullOrWhiteSpace(entry.url))
                    throw InvalidSettings();
                var name = entry.name.Trim();
                var url = entry.url.Trim().TrimEnd('/');
                if (!names.Add(name) || !urls.Add(url) || !AcceptUrl(url)) throw InvalidSettings();
                var scopes = entry.scopes == null ? new string[0] : entry.scopes.Select(scope => scope ?? string.Empty).ToArray();
                stored[index] = new Entry { name = name, url = url, scopes = scopes };
            }
            var json = JsonUtility.ToJson(new Document { scopedRegistries = stored }, true);
            if (json.Length > MaximumJsonLength) throw InvalidSettings();
            return json;
        }

        internal static Entry[] ReadCatalog(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumJsonLength) throw InvalidSettings();
            var trimmed = json.Trim();
            if (trimmed.Length > 0 && trimmed[0] == '\uFEFF') trimmed = trimmed.TrimStart('\uFEFF').Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{' || trimmed[trimmed.Length - 1] != '}') throw InvalidSettings();
            Document document;
            try { document = JsonUtility.FromJson<Document>(trimmed); }
            catch (ArgumentException) { throw InvalidSettings(); }
            if (document?.scopedRegistries == null) throw InvalidSettings();
            if (document.scopedRegistries.Length == 0) return new Entry[0];
            WriteCatalog(document.scopedRegistries);
            return document.scopedRegistries.Select(entry => new Entry
            {
                name = entry.name.Trim(),
                url = entry.url.Trim().TrimEnd('/'),
                scopes = entry.scopes == null ? new string[0] : entry.scopes.Select(scope => scope ?? string.Empty).ToArray()
            }).ToArray();
        }

        private static bool AcceptUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
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

        private static ArgumentException InvalidSettings() => new RegistryText.Error("json.invalid");
    }
}
