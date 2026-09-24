using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor.PackageManager;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal static class ProjectRegistryEdit
    {
        internal enum Result { Unchanged, Updated, Blocked, Invalid, Conflict }

        internal static string ManifestPath;
        internal static string LockPath;
        internal static string BackupDirectory;
        internal static Action Resolve;
        internal static Action BeforeCommit;
        internal static string[] PackageNamesOverride;
        // Replaces the Package Manager query in tests; throwing simulates an unavailable query.
        internal static Func<IEnumerable<RuntimePackage>> RuntimePackagesOverride;
        static bool resolving;

        static string ReferencedMessage => RegistryText.T("project.referenced");
        static string UnknownMessage => RegistryText.T("project.unknown");

        internal enum Origin { Registry, Excluded, Unknown }

        internal sealed class Reference
        {
            internal string Name;
            internal Origin Origin;
            internal string Url;
        }

        internal sealed class RuntimePackage
        {
            internal string Name;
            internal PackageSource Source;
            internal string RegistryUrl;
        }

        internal sealed class View
        {
            internal bool Ok;
            // Kept as a key so a cached view shows the language current at display time.
            internal string ErrorKey;
            internal string Error => ErrorKey == null ? null : RegistryText.T(ErrorKey);
            internal DateTime ManifestTime, LockTime;
            internal readonly List<Entry> Registries = new List<Entry>();
            // Collected once per observation; the final removal guard applies the same predicate to a fresh collection.
            internal readonly List<Reference> References = new List<Reference>();
            internal bool SourcesFailed;
            internal int ReferencesOf(string scope) => References.Count(item => item.Origin != Origin.Excluded && UsesScope(item.Name, scope));
            internal int ReferencesOf(string url, string scope) => CountReferences(References, url, scope);
            internal bool UnresolvedOf(string scope) => IsUnresolved(References, SourcesFailed, scope);
            internal bool Contains(string url, string scope) => Registries.Any(entry => SameUrl(entry.Url, url) && entry.Scopes.Any(item => SameScope(item, scope)));
        }

        internal sealed class Entry
        {
            internal string Url;
            internal readonly List<string> Scopes = new List<string>();
        }

        internal static string ActiveManifest() => string.IsNullOrEmpty(ManifestPath) ? DefaultManifest() : ManifestPath;
        internal static string ActiveLock() => string.IsNullOrEmpty(LockPath) ? DefaultLock() : LockPath;

        internal static bool ChangedSince(DateTime manifestTime, DateTime lockTime) =>
            FileTime(ActiveManifest()) != manifestTime || FileTime(ActiveLock()) != lockTime;

        internal static View Observe()
        {
            var view = new View { ManifestTime = FileTime(ActiveManifest()), LockTime = FileTime(ActiveLock()) };
            if (!File.Exists(ActiveManifest())) { view.ErrorKey = "project.manifestMissingView"; return view; }
            try
            {
                var root = Parse(Decode(File.ReadAllBytes(ActiveManifest())));
                view.Registries.AddRange(ReadRegistries(root));
                view.References.AddRange(CollectReferences(out view.SourcesFailed));
                view.Ok = true;
            }
            catch (Exception)
            {
                view.ErrorKey = "project.manifestUnreadableView";
            }
            return view;
        }

        internal static Result Apply(string registryName, string registryUrl, string scope, bool add, out string error)
        {
            error = null;
            scope = scope == null ? "" : scope.Trim();
            if (scope.Length == 0) { error = RegistryText.T("project.scopeRequired"); return Result.Invalid; }
            var path = ActiveManifest();
            if (!File.Exists(path)) { error = RegistryText.T("project.manifestMissing"); return Result.Invalid; }
            byte[] snapshot;
            string text;
            try { snapshot = File.ReadAllBytes(path); text = Decode(snapshot); }
            catch (Exception) { error = RegistryText.T("project.manifestUnreadable"); return Result.Invalid; }
            string updated;
            try
            {
                if (!TryMutate(text, registryName, registryUrl, scope, add, out updated, out error))
                    return error == null ? Result.Unchanged : Result.Invalid;
            }
            catch (Exception) { error = RegistryText.T("project.manifestUnreadable"); return Result.Invalid; }
            if (updated == null) return Result.Unchanged;
            if (!add && !RemovalAllowed(registryUrl, scope, out error)) return Result.Blocked;
            var bytes = Encode(updated, snapshot);
            try
            {
                if (BeforeCommit != null) BeforeCommit();
                if (!File.Exists(path) || !Same(snapshot, File.ReadAllBytes(path)))
                { error = RegistryText.T("project.manifestConflict"); return Result.Conflict; }
                WriteBackup(snapshot);
                Replace(path, bytes, snapshot);
            }
            catch (IOException exception) when (exception.Message == "manifest_conflict")
            { error = RegistryText.T("project.manifestConflict"); return Result.Conflict; }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException)
            { error = RegistryText.T("project.saveFailed"); return Result.Invalid; }
            try { RequestResolve(); }
            catch (Exception) { error = RegistryText.T("project.resolveFailed"); }
            return Result.Updated;
        }

        internal static bool SameUrl(string left, string right) =>
            string.Equals(NormalizeUrl(left), NormalizeUrl(right), StringComparison.OrdinalIgnoreCase);

        internal static bool UsesScope(string packageName, string scope)
        {
            if (string.IsNullOrEmpty(packageName) || string.IsNullOrEmpty(scope)) return false;
            return packageName.Equals(scope, StringComparison.Ordinal) || packageName.StartsWith(scope + ".", StringComparison.Ordinal);
        }

        // Registry URLs are equal when scheme/host match case-insensitively and the path matches exactly, ignoring a trailing slash.
        internal static bool SameRegistry(string left, string right)
        {
            try { return UrlRules.Key(left) == UrlRules.Key(right); }
            catch (RegistryException) { return false; }
        }

        static bool RemovalAllowed(string registryUrl, string scope, out string error)
        {
            if (PackageNamesOverride != null)
            {
                error = PackageNamesOverride.Any(name => UsesScope(name, scope)) ? ReferencedMessage : null;
                return error == null;
            }
            var references = CollectReferences(out var failed);
            error = CountReferences(references, registryUrl, scope) > 0 ? ReferencedMessage
                : IsUnresolved(references, failed, scope) ? UnknownMessage : null;
            return error == null;
        }

        // Only packages installed from this registry count; local, embedded, git and built-in packages never block.
        static int CountReferences(IEnumerable<Reference> references, string url, string scope) =>
            references.Count(item => item.Origin == Origin.Registry && UsesScope(item.Name, scope) && SameRegistry(item.Url, url));

        // An unreadable source or a matching package whose origin is not known yet keeps the scope conservatively.
        static bool IsUnresolved(IEnumerable<Reference> references, bool failed, string scope) =>
            !string.IsNullOrEmpty(scope) && (failed || references.Any(item => item.Origin == Origin.Unknown && UsesScope(item.Name, scope)));

        static List<Reference> CollectReferences(out bool failed)
        {
            failed = false;
            if (PackageNamesOverride != null)
                return PackageNamesOverride.Distinct(StringComparer.Ordinal)
                    .Select(name => new Reference { Name = name, Origin = Origin.Unknown }).ToList();
            var manifest = new Dictionary<string, string>(StringComparer.Ordinal);
            var locked = new Dictionary<string, Reference>(StringComparer.Ordinal);
            var runtime = new Dictionary<string, RuntimePackage>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(ActiveManifest()))
                {
                    var dependencies = Prop(Parse(Decode(File.ReadAllBytes(ActiveManifest()))), "dependencies");
                    if (dependencies != null && dependencies.Props != null)
                        foreach (var item in dependencies.Props) manifest[item.Key] = item.Value.Text;
                }
            }
            catch (Exception) { failed = true; }
            try
            {
                // Top-level lock entries already list transitive packages; nested maps are version requirements only.
                if (File.Exists(ActiveLock()))
                {
                    var dependencies = Prop(Parse(Decode(File.ReadAllBytes(ActiveLock()))), "dependencies");
                    if (dependencies != null && dependencies.Props != null)
                        foreach (var item in dependencies.Props) locked[item.Key] = LockReference(item.Key, item.Value);
                }
            }
            catch (Exception) { failed = true; }
            try
            {
                var packages = RuntimePackagesOverride != null ? RuntimePackagesOverride()
                    : PackageInfo.GetAllRegisteredPackages().Select(package => new RuntimePackage
                        { Name = package.name, Source = package.source, RegistryUrl = package.registry?.url });
                foreach (var package in packages)
                    if (package != null && !string.IsNullOrEmpty(package.Name)) runtime[package.Name] = package;
            }
            catch (Exception) { failed = true; }
            return manifest.Keys.Union(locked.Keys).Union(runtime.Keys).Select(name =>
            {
                runtime.TryGetValue(name, out var installed);
                locked.TryGetValue(name, out var fromLock);
                var inManifest = manifest.TryGetValue(name, out var spec);
                return Classify(name, installed, fromLock, inManifest, spec);
            }).ToList();
        }

        // The loaded package is authoritative when known; the lock only fills in when it is missing or has no registry URL.
        static Reference Classify(string name, RuntimePackage runtime, Reference locked, bool inManifest, string spec)
        {
            if (runtime != null && runtime.Source != PackageSource.Unknown)
            {
                if (runtime.Source != PackageSource.Registry) return new Reference { Name = name, Origin = Origin.Excluded };
                if (!string.IsNullOrWhiteSpace(runtime.RegistryUrl)) return RegistryReference(name, runtime.RegistryUrl);
                return locked != null && locked.Origin == Origin.Registry ? locked : new Reference { Name = name, Origin = Origin.Unknown };
            }
            if (locked != null) return locked;
            if (inManifest && LocalSpec(spec)) return new Reference { Name = name, Origin = Origin.Excluded };
            return new Reference { Name = name, Origin = Origin.Unknown };
        }

        static Reference LockReference(string name, Node entry)
        {
            var source = entry.Props == null ? null : StringProp(entry, "source");
            switch (source)
            {
                case "registry":
                    var url = StringProp(entry, "url");
                    return url.Length == 0 ? new Reference { Name = name, Origin = Origin.Unknown } : RegistryReference(name, url);
                case "builtin":
                case "embedded":
                case "local":
                case "local-tarball":
                case "git":
                    return new Reference { Name = name, Origin = Origin.Excluded };
                default:
                    return new Reference { Name = name, Origin = Origin.Unknown };
            }
        }

        // A registry URL that cannot be compared stays unknown instead of silently matching nothing.
        static Reference RegistryReference(string name, string url)
        {
            try { UrlRules.Key(url); }
            catch (RegistryException) { return new Reference { Name = name, Origin = Origin.Unknown }; }
            return new Reference { Name = name, Origin = Origin.Registry, Url = url };
        }

        // Direct file/path/git dependencies are never installed from a scoped registry.
        static bool LocalSpec(string spec) => spec != null && (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            spec.Contains("://") || spec.StartsWith("git", StringComparison.OrdinalIgnoreCase) || spec.EndsWith(".git", StringComparison.OrdinalIgnoreCase));

        static void RequestResolve()
        {
            if (resolving) return;
            resolving = true;
            try { if (Resolve != null) Resolve(); else Client.Resolve(); }
            finally { resolving = false; }
        }

        static bool TryMutate(string text, string registryName, string registryUrl, string scope, bool add, out string updated, out string error)
        {
            updated = null;
            error = null;
            var root = Parse(text);
            if (root.Props == null) { error = RegistryText.T("project.manifestUnreadable"); return false; }
            var registries = Prop(root, "scopedRegistries");
            if (registries != null && registries.Items == null) { error = RegistryText.T("project.manifestUnreadable"); return false; }
            var matches = new List<Node>();
            if (registries != null)
                foreach (var item in registries.Items)
                    if (item.Props != null && SameUrl(StringProp(item, "url"), registryUrl)) matches.Add(item);
            if (matches.Count > 1) { error = RegistryText.T("project.duplicateUrl"); return false; }
            if (matches.Count == 0)
            {
                if (!add) return true;
                updated = registries == null
                    ? InsertProperty(text, root, "\"scopedRegistries\":[" + RegistryJson(registryName, registryUrl, scope) + "]")
                    : text.Insert(registries.End - 1, (registries.Items.Count == 0 ? "" : ",") + RegistryJson(registryName, registryUrl, scope));
                return true;
            }
            var scopes = Prop(matches[0], "scopes");
            if (scopes == null)
            {
                if (!add) return true;
                updated = InsertProperty(text, matches[0], "\"scopes\":[" + Quote(scope) + "]");
                return true;
            }
            if (scopes.Items == null || scopes.Items.Any(item => item.Text == null)) { error = RegistryText.T("project.manifestUnreadable"); return false; }
            var found = new List<int>();
            for (var index = 0; index < scopes.Items.Count; index++)
                if (SameScope(scopes.Items[index].Text, scope)) found.Add(index);
            if (add)
            {
                if (found.Count > 0) return true;
                updated = text.Insert(scopes.End - 1, (scopes.Items.Count == 0 ? "" : ",") + Quote(scope));
                return true;
            }
            if (found.Count == 0) return true;
            // UPM rejects an empty scopes array, so removing the last scope removes the whole registry object.
            if (found.Count == scopes.Items.Count)
            {
                var items = registries.Items;
                var at = items.IndexOf(matches[0]);
                int start, end;
                if (items.Count == 1) { start = registries.Start + 1; end = registries.End - 1; }
                else if (at == 0) { start = items[0].Start; end = items[1].Start; }
                else { start = items[at - 1].End; end = items[at].End; }
                updated = text.Remove(start, end - start);
                return true;
            }
            var remaining = scopes.Items.Where((item, index) => !found.Contains(index))
                .Select(item => text.Substring(item.Start, item.End - item.Start));
            updated = text.Remove(scopes.Start, scopes.End - scopes.Start)
                .Insert(scopes.Start, "[" + string.Join(",", remaining) + "]");
            return true;
        }

        static string InsertProperty(string text, Node obj, string property)
        {
            return text.Insert(obj.End - 1, (obj.Props.Count == 0 ? "" : ",") + property);
        }

        static List<Entry> ReadRegistries(Node root)
        {
            var result = new List<Entry>();
            var registries = Prop(root, "scopedRegistries");
            if (registries == null) return result;
            if (registries.Items == null) throw new InvalidOperationException("manifest_invalid");
            foreach (var item in registries.Items)
            {
                if (item.Props == null) throw new InvalidOperationException("manifest_invalid");
                var entry = new Entry { Url = StringProp(item, "url") };
                var scopes = Prop(item, "scopes");
                if (scopes != null)
                {
                    if (scopes.Items == null) throw new InvalidOperationException("manifest_invalid");
                    foreach (var scope in scopes.Items)
                    {
                        if (scope.Text == null) throw new InvalidOperationException("manifest_invalid");
                        entry.Scopes.Add(scope.Text.Trim());
                    }
                }
                result.Add(entry);
            }
            return result;
        }

        static string StringProp(Node obj, string key)
        {
            var value = Prop(obj, key);
            return value == null ? "" : value.Text ?? "";
        }

        static Node Prop(Node obj, string key)
        {
            if (obj.Props == null) return null;
            foreach (var item in obj.Props) if (item.Key == key) return item.Value;
            return null;
        }

        sealed class Node
        {
            internal int Start, End;
            internal string Text;
            internal List<Node> Items;
            internal List<KeyValuePair<string, Node>> Props;
        }

        static Node Parse(string text)
        {
            var index = 0;
            var node = Read(text, ref index, 0);
            Skip(text, ref index);
            if (index != text.Length || node.Props == null) throw new InvalidOperationException("manifest_invalid");
            return node;
        }

        static Node Read(string text, ref int index, int depth)
        {
            if (depth > 32) throw new InvalidOperationException("manifest_invalid");
            Skip(text, ref index);
            if (index >= text.Length) throw new InvalidOperationException("manifest_invalid");
            var node = new Node { Start = index };
            var kind = text[index];
            if (kind == '{' || kind == '[')
            {
                index++;
                var close = kind == '{' ? '}' : ']';
                if (kind == '{') node.Props = new List<KeyValuePair<string, Node>>();
                else node.Items = new List<Node>();
                Skip(text, ref index);
                while (index < text.Length && text[index] != close)
                {
                    if (kind == '{')
                    {
                        var key = ReadString(text, ref index);
                        Skip(text, ref index);
                        if (index >= text.Length || text[index++] != ':') throw new InvalidOperationException("manifest_invalid");
                        if (node.Props.Any(item => item.Key == key)) throw new InvalidOperationException("manifest_invalid");
                        node.Props.Add(new KeyValuePair<string, Node>(key, Read(text, ref index, depth + 1)));
                    }
                    else node.Items.Add(Read(text, ref index, depth + 1));
                    Skip(text, ref index);
                    if (index < text.Length && text[index] == close) break;
                    if (index >= text.Length || text[index++] != ',') throw new InvalidOperationException("manifest_invalid");
                    Skip(text, ref index);
                    if (index < text.Length && text[index] == close) throw new InvalidOperationException("manifest_invalid");
                }
                if (index >= text.Length || text[index++] != close) throw new InvalidOperationException("manifest_invalid");
            }
            else if (kind == '"') node.Text = ReadString(text, ref index);
            else
            {
                while (index < text.Length && !char.IsWhiteSpace(text[index]) && ",]}".IndexOf(text[index]) < 0) index++;
                if (index == node.Start) throw new InvalidOperationException("manifest_invalid");
                var literal = text.Substring(node.Start, index - node.Start);
                if (literal != "true" && literal != "false" && literal != "null" &&
                    !Regex.IsMatch(literal, @"\A-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?\z"))
                    throw new InvalidOperationException("manifest_invalid");
            }
            node.End = index;
            return node;
        }

        static string ReadString(string text, ref int index)
        {
            if (index >= text.Length || text[index++] != '"') throw new InvalidOperationException("manifest_invalid");
            var value = new StringBuilder();
            while (index < text.Length)
            {
                var c = text[index++];
                if (c == '"') return value.ToString();
                if (c == '\\')
                {
                    if (index >= text.Length) break;
                    c = text[index++];
                    if (c == '"' || c == '\\' || c == '/') value.Append(c == '/' ? '/' : c);
                    else if (c == 'b') value.Append('\b');
                    else if (c == 'f') value.Append('\f');
                    else if (c == 'n') value.Append('\n');
                    else if (c == 'r') value.Append('\r');
                    else if (c == 't') value.Append('\t');
                    else if (c == 'u')
                    {
                        if (index + 4 > text.Length) throw new InvalidOperationException("manifest_invalid");
                        value.Append((char)Convert.ToInt32(text.Substring(index, 4), 16));
                        index += 4;
                    }
                    else throw new InvalidOperationException("manifest_invalid");
                }
                else if (c < 32) throw new InvalidOperationException("manifest_invalid");
                else value.Append(c);
            }
            throw new InvalidOperationException("manifest_invalid");
        }

        static void Skip(string text, ref int index)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        }

        static string RegistryJson(string name, string url, string scope) =>
            "{\"name\":" + Quote(name == null ? "" : name.Trim()) + ",\"url\":" + Quote(NormalizeUrl(url)) + ",\"scopes\":[" + Quote(scope) + "]}";

        static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            foreach (var c in value ?? "")
            {
                if (c == '\\' || c == '"') result.Append('\\').Append(c);
                else if (c == '\n') result.Append("\\n");
                else if (c == '\r') result.Append("\\r");
                else if (c == '\t') result.Append("\\t");
                else if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4"));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }

        static string NormalizeUrl(string value) => (value ?? "").Trim().TrimEnd('/');
        static bool SameScope(string left, string right) => string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.Ordinal);

        static void WriteBackup(byte[] snapshot)
        {
            var backup = BackupFile();
            Directory.CreateDirectory(Path.GetDirectoryName(backup));
            var temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, snapshot);
            if (File.Exists(backup)) File.Replace(temporary, backup, null);
            else File.Move(temporary, backup);
        }

        static void Replace(string path, byte[] bytes, byte[] snapshot)
        {
            if (!Same(File.ReadAllBytes(path), snapshot)) throw new IOException("manifest_conflict");
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            try
            {
                if (!Same(File.ReadAllBytes(path), snapshot)) throw new IOException("manifest_conflict");
                File.Replace(temporary, path, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        static string BackupFile()
        {
            if (!string.IsNullOrEmpty(BackupDirectory)) return Path.Combine(BackupDirectory, "manifest.json");
            var manifest = ActiveManifest();
            var folder = Path.GetDirectoryName(manifest);
            var root = string.Equals(Path.GetFileName(folder), "Packages", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(folder) : folder;
            return Path.Combine(root, "Library", "PackageRegistryManager", "manifest.json");
        }

        static string DefaultManifest() => Path.Combine(ProjectRoot(), "Packages", "manifest.json");
        static string DefaultLock() => Path.Combine(ProjectRoot(), "Packages", "packages-lock.json");
        static string ProjectRoot() => Directory.GetParent(UnityEngine.Application.dataPath).FullName;
        static DateTime FileTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default(DateTime);

        static string Decode(byte[] bytes)
        {
            var start = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
        }

        static byte[] Encode(string text, byte[] original)
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            if (original.Length >= 3 && original[0] == 239 && original[1] == 187 && original[2] == 191)
            {
                var with = new byte[bytes.Length + 3];
                with[0] = 239; with[1] = 187; with[2] = 191;
                Buffer.BlockCopy(bytes, 0, with, 3, bytes.Length);
                return with;
            }
            return bytes;
        }

        static bool Same(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++) if (left[index] != right[index]) return false;
            return true;
        }
    }
}
