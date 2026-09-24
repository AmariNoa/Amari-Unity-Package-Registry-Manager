using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal static class RegistryCatalog
    {
        internal const string Missing = "missing";
        internal static string PathOverride;

        internal sealed class Snapshot
        {
            internal string Stamp;
            internal DateTime WriteTimeUtc;
            internal RegistrySettingsJson.Entry[] Entries;
        }

        internal static string FilePath => string.IsNullOrEmpty(PathOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amari", "PackageRegistryManager", "catalog.json")
            : PathOverride;

        internal static DateTime CurrentWriteTime()
        {
            var path = FilePath;
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default(DateTime);
        }

        internal static bool ChangedSince(DateTime knownUtc) => CurrentWriteTime() != knownUtc;

        internal static Snapshot Load()
        {
            var path = FilePath;
            if (!File.Exists(path)) return Empty();
            if (new FileInfo(path).Length > 1024 * 1024 + 3) throw new ArgumentException("catalog_size");
            var bytes = File.ReadAllBytes(path);
            return new Snapshot
            {
                Stamp = StampOf(bytes),
                WriteTimeUtc = File.GetLastWriteTimeUtc(path),
                Entries = RegistrySettingsJson.ReadCatalog(Decode(bytes))
            };
        }

        // beforeCommit runs under the lock after the stamp check and staging, just before the file is replaced;
        // throwing from it leaves the catalog unchanged. The replace itself can still fail after it has run.
        internal static Snapshot Save(string expectedStamp, RegistrySettingsJson.Entry[] entries, Action beforeCommit = null)
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("catalog_path");
            Directory.CreateDirectory(directory);
            var bytes = new UTF8Encoding(false).GetBytes(RegistrySettingsJson.WriteCatalog(entries ?? new RegistrySettingsJson.Entry[0]));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (Acquire(path + ".lock"))
            {
                var current = File.Exists(path) ? StampOf(File.ReadAllBytes(path)) : Missing;
                if (current != expectedStamp) throw new InvalidOperationException("catalog_conflict");
                try
                {
                    File.WriteAllBytes(temporary, bytes);
                    beforeCommit?.Invoke();
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                return new Snapshot { Stamp = StampOf(bytes), WriteTimeUtc = File.GetLastWriteTimeUtc(path), Entries = RegistrySettingsJson.ReadCatalog(Decode(bytes)) };
            }
        }

        private static Snapshot Empty() => new Snapshot { Stamp = Missing, WriteTimeUtc = default(DateTime), Entries = new RegistrySettingsJson.Entry[0] };

        private static FileStream Acquire(string lockPath)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xFFFF) == 32 || (error.HResult & 0xFFFF) == 33)
            {
                throw new InvalidOperationException("catalog_busy");
            }
        }

        private static string StampOf(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        private static string Decode(byte[] bytes)
        {
            var start = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
            return new UTF8Encoding(false, true).GetString(bytes, start, bytes.Length - start);
        }
    }
}
