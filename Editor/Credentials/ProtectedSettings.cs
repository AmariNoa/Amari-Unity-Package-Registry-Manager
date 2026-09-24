using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal sealed class UserPaths
    {
        // ManifestFile is kept for constructor compatibility only; nothing in this assembly writes it.
        internal readonly string StoreDirectory, ConfigurationFile, ManifestFile;
        internal UserPaths(string manifest, string config, string store)
        {
            if (!Path.IsPathRooted(manifest) || !Path.IsPathRooted(config) || !Path.IsPathRooted(store)) throw new RegistryException("settings_path");
            ManifestFile = Path.GetFullPath(manifest); ConfigurationFile = Path.GetFullPath(config); StoreDirectory = Path.GetFullPath(store);
        }
        internal static UserPaths Default(string projectDirectory)
        {
            string config = Environment.GetEnvironmentVariable("UPM_USER_CONFIG_FILE");
            if (string.IsNullOrEmpty(config)) config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".upmconfig.toml");
            // Private subdirectory: the parent also holds catalog.json and keeps its ACL.
            // Legacy credentials.bin/pending.bin directly in the parent are intentionally never read or migrated.
            return new UserPaths(Path.Combine(projectDirectory, "Packages", "manifest.json"), config,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amari", "PackageRegistryManager", "credentials"));
        }
    }
    internal static class PrivateFiles
    {
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, IntPtr buffer, int length, out int required);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private static SecurityIdentifier Identity
        {
            get
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), 8, out token)) throw new RegistryException("windows_identity");
                try
                {
                    int length; GetTokenInformation(token, 1, IntPtr.Zero, 0, out length);
                    if (length <= 0 || length > 65536) throw new RegistryException("windows_identity");
                    IntPtr buffer = Marshal.AllocHGlobal(length);
                    try
                    {
                        if (!GetTokenInformation(token, 1, buffer, length, out length)) throw new RegistryException("windows_identity");
                        return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { CloseHandle(token); }
            }
        }
        internal static void CheckPath(string path)
        {
            var cursor = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new RegistryException("settings_reparse_point");
                cursor = Path.GetDirectoryName(cursor);
            }
        }
        internal static void CheckAcl(string path, bool directory)
        {
            CheckPath(path);
            FileSystemSecurity acl = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var user = Identity;
            if (!user.Equals(acl.GetOwner(typeof(SecurityIdentifier)))) throw new RegistryException("settings_owner");
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (rule.AccessControlType == AccessControlType.Allow && !sid.Equals(user) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && !sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)) throw new RegistryException("settings_acl");
            }
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetNamedSecurityInfoW(string name, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorOwner(IntPtr descriptor, out IntPtr owner, out bool defaulted);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, out bool present, out IntPtr dacl, out bool defaulted);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetAce(IntPtr acl, int index, out IntPtr ace);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr descriptor, uint revision, uint info, out IntPtr text, out uint length);
        // Read/list, read EA/attributes, execute/traverse, READ_CONTROL, SYNCHRONIZE, GENERIC_READ/EXECUTE. Any other bit is a mutation or unknown.
        private const uint ReadOnlyRights = 0xA01200A9;
        private const uint OwnerAndDacl = 5;
        // The user-managed UPM configuration and its folder keep their existing ACL: other principals may only read/traverse,
        // unlike the private store. Native reading avoids Mono's managed ACL mapping. Returns owner+DACL SDDL for replace checks.
        // The file must be owned by the current user (a replace could not keep another owner); the folder may also be SYSTEM/Administrators-owned.
        internal static string CheckShared(string path, bool directory)
        {
            CheckPath(path);
            IntPtr owner, group, dacl, sacl, descriptor;
            if (GetNamedSecurityInfoW(path, 1, OwnerAndDacl, out owner, out group, out dacl, out sacl, out descriptor) != 0) throw new RegistryException("settings_acl");
            try { return CheckShared(descriptor, directory); }
            finally { LocalFree(descriptor); }
        }
        internal static string CheckShared(IntPtr descriptor, bool directory)
        {
            var user = Identity;
            Func<IntPtr, bool> trusted = pointer =>
            {
                var sid = new SecurityIdentifier(pointer);
                return sid.Equals(user) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
            };
            IntPtr owner, dacl; bool present, defaulted;
            if (!GetSecurityDescriptorOwner(descriptor, out owner, out defaulted) || owner == IntPtr.Zero || !(directory ? trusted(owner) : user.Equals(new SecurityIdentifier(owner)))) throw new RegistryException("settings_owner");
            if (!GetSecurityDescriptorDacl(descriptor, out present, out dacl, out defaulted) || !present || dacl == IntPtr.Zero) throw new RegistryException("settings_acl");
            int count = (ushort)Marshal.ReadInt16(dacl, 4);
            for (int i = 0; i < count; i++)
            {
                IntPtr ace;
                if (!GetAce(dacl, i, out ace)) throw new RegistryException("settings_acl");
                byte type = Marshal.ReadByte(ace), flags = Marshal.ReadByte(ace, 1);
                if (type == 1) continue; // a deny only narrows access
                if (type != 0) throw new RegistryException("settings_acl"); // object/callback/unknown allow: fail closed
                // Inherit-only is no grant here; inherited writers show up on the file itself, and new files get a private protected DACL.
                if ((flags & 8) != 0 || trusted(ace + 8)) continue;
                if (((uint)Marshal.ReadInt32(ace, 4) & ~ReadOnlyRights) != 0) throw new RegistryException("settings_acl");
            }
            IntPtr text; uint length;
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, OwnerAndDacl, out text, out length)) throw new RegistryException("settings_acl");
            try { return Marshal.PtrToStringUni(text); }
            finally { LocalFree(text); }
        }
        // Folder first, then the file if present; null when the configuration does not exist yet.
        internal static string CheckConfiguration(string path)
        {
            CheckShared(Path.GetDirectoryName(Path.GetFullPath(path)), true);
            return File.Exists(path) ? CheckShared(path, false) : null;
        }
        // ReplaceFileW re-applies the replaced file's DACL and may mark it auto-inherited (D:P -> D:PAI). A protected DACL takes
        // nothing from its parent, so that flag alone changes neither access nor inheritance and is ignored there. Owner, the
        // protection flag and every ACE are still compared exactly; on an unprotected DACL AI is compared as well.
        internal static string Canonical(string sddl)
        {
            int start = sddl.IndexOf("D:", StringComparison.Ordinal);
            if (start < 0) return sddl;
            int ace = sddl.IndexOf('(', start);
            int end = ace < 0 ? sddl.Length : ace;
            string flags = sddl.Substring(start + 2, end - start - 2);
            return flags.Contains("P") ? sddl.Substring(0, start + 2) + flags.Replace("AI", "") + sddl.Substring(end) : sddl;
        }
        // Existing configuration is replaced and must keep its owner and ACL (ReplaceFileW preserves the DACL); verified afterwards,
        // not assumed. A new configuration keeps the private temp ACL. The store keeps AtomicWrite(secret: true).
        internal static void WriteConfiguration(string path, byte[] bytes)
        {
            string before = CheckConfiguration(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = Create(temporary)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (CheckConfiguration(path) != before) throw new RegistryException("settings_acl");
                if (before != null) File.Replace(temporary, path, null); else File.Move(temporary, path);
                if (before == null) CheckAcl(path, false);
                else if (Canonical(CheckShared(path, false)) != Canonical(before)) throw new RegistryException("settings_acl");
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes
        {
            internal int Length;
            internal IntPtr Descriptor;
            [MarshalAs(UnmanagedType.Bool)] internal bool Inherit;
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string descriptor, uint revision, out IntPtr security, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFileW(string path, uint access, uint share, ref SecurityAttributes attributes, uint mode, uint flags, IntPtr template);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
        private static SecurityAttributes Attributes(bool directory)
        {
            IntPtr descriptor; uint size;
            string sid = Identity.Value;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("O:" + sid + "D:P(A;" + (directory ? "OICI" : "") + ";FA;;;" + sid + ")", 1, out descriptor, out size)) throw new RegistryException("settings_acl");
            return new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Descriptor = descriptor, Inherit = false };
        }
        internal static void EnsureDirectory(string path)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new RegistryException("windows_required");
            CheckPath(path);
            if (!Directory.Exists(path))
            {
                // Only the leaf gets the private ACL; missing ancestors keep inherited defaults.
                string parent = Path.GetDirectoryName(path);
                if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                var attributes = Attributes(true);
                try { if (!CreateDirectoryW(path, ref attributes) && Marshal.GetLastWin32Error() != 183) throw new RegistryException("settings_create_directory"); }
                finally { LocalFree(attributes.Descriptor); }
            }
            CheckAcl(path, true);
        }
        internal static FileStream Create(string path)
        {
            CheckPath(path); var attributes = Attributes(false);
            try
            {
                var handle = CreateFileW(path, 0xC0000000, 0, ref attributes, 1, 0x80000000, IntPtr.Zero);
                if (handle == new IntPtr(-1)) throw new IOException("settings_create_file");
                CloseHandle(handle);
                CheckAcl(path, false);
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally { LocalFree(attributes.Descriptor); }
        }
        internal static byte[] Read(string path)
        {
            CheckPath(path);
            if (!File.Exists(path)) return new byte[0];
            if (new FileInfo(path).Length > 4194304) throw new RegistryException("settings_size");
            return File.ReadAllBytes(path);
        }
        internal static void AtomicWrite(string path, byte[] bytes, bool secret)
        {
            CheckPath(path);
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent)) { if (secret) EnsureDirectory(parent); else Directory.CreateDirectory(parent); }
            if (secret) { CheckAcl(parent, true); if (File.Exists(path)) CheckAcl(path, false); }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = secret ? Create(temporary) : new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                CheckPath(path);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                if (secret) CheckAcl(path, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static string Decode(byte[] bytes)
        {
            int start = bytes.Length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191 ? 3 : 0;
            try { return Data.Utf8.GetString(bytes, start, bytes.Length - start); }
            catch (DecoderFallbackException) { throw new RegistryException("settings_encoding"); }
        }
        internal static byte[] EncodeLike(string text, byte[] old)
        {
            var bytes = Data.Utf8.GetBytes(text);
            return old.Length >= 3 && old[0] == 239 && old[1] == 187 && old[2] == 191 ? new byte[] { 239, 187, 191 }.Concat(bytes).ToArray() : bytes;
        }
    }
    internal sealed class StoreState
    {
        public int Version = 1;
        public List<CredentialRecord> Records = new List<CredentialRecord>();
    }
    internal sealed class PendingWrite
    {
        public int Version = 1;
        public string ConfigurationPath;
        public string BeforeStore;
        public string BeforeConfig;
        public byte[] Store;
        public byte[] Config;
    }
    internal sealed class ProtectedSettings
    {
        internal const string StoreFileName = "credentials.bin";
        internal readonly UserPaths Paths;
        internal string StorePath { get { return Path.Combine(Paths.StoreDirectory, StoreFileName); } }
        private string JournalPath { get { return Path.Combine(Paths.StoreDirectory, "pending.bin"); } }
        internal Action<string> Fault;
        internal ProtectedSettings(UserPaths paths) { Paths = paths; }
        internal IDisposable Lock(string purpose)
        {
            PrivateFiles.EnsureDirectory(Paths.StoreDirectory);
            string name = Data.Hash(purpose).Replace('/', '_').Replace('+', '-');
            string path = Path.Combine(Paths.StoreDirectory, name + ".lock");
            try
            {
                if (!File.Exists(path)) { using (PrivateFiles.Create(path)) { } }
                PrivateFiles.CheckAcl(path, false);
                return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { throw new RegistryException("settings_busy"); }
        }
        [StructLayout(LayoutKind.Sequential)] private struct DataBlob
        {
            internal int Length;
            internal IntPtr Pointer;
        }
        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
        [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
        private static byte[] Transform(byte[] bytes, bool protect)
        {
            var input = new DataBlob { Length = bytes.Length, Pointer = Marshal.AllocHGlobal(bytes.Length) };
            var output = new DataBlob();
            try
            {
                Marshal.Copy(bytes, 0, input.Pointer, bytes.Length);
                bool success = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!success || output.Length < 0 || output.Length > 8388608) throw new RegistryException(protect ? "store_protect_failed" : "store_unreadable");
                var result = new byte[output.Length]; Marshal.Copy(output.Pointer, result, 0, result.Length); return result;
            }
            finally
            {
                for (int i = 0; i < input.Length; i++) Marshal.WriteByte(input.Pointer, i, 0);
                Marshal.FreeHGlobal(input.Pointer);
                if (output.Pointer != IntPtr.Zero)
                {
                    if (output.Length >= 0 && output.Length <= 8388608) for (int i = 0; i < output.Length; i++) Marshal.WriteByte(output.Pointer, i, 0);
                    LocalFree(output.Pointer);
                }
            }
        }
        private static byte[] Protect(byte[] bytes) { return Transform(bytes, true); }
        private static byte[] Unprotect(byte[] bytes) { return Transform(bytes, false); }
        private StoreState LoadUnlocked()
        {
            var bytes = PrivateFiles.Read(StorePath);
            if (bytes.Length == 0) return new StoreState();
            var obj = Data.Object(PrivateFiles.Decode(Unprotect(bytes)));
            if (obj["Version"] == null || obj["Version"].Type != Newtonsoft.Json.Linq.JTokenType.Integer || (int)obj["Version"] != 1) throw new RegistryException("store_version");
            var state = obj.ToObject<StoreState>();
            if (state == null || state.Records == null || state.Records.Any(r => r == null || r.Url == null || r.Generation == null) || state.Records.Select(r => r.Url).Distinct(StringComparer.Ordinal).Count() != state.Records.Count) throw new RegistryException("store_invalid");
            return state;
        }
        private void RecoverUnlocked()
        {
            if (!File.Exists(JournalPath)) return;
            var pending = Data.Object(PrivateFiles.Decode(Unprotect(PrivateFiles.Read(JournalPath)))).ToObject<PendingWrite>();
            if (pending == null || pending.Version != 1 || pending.Store == null || pending.Config == null || !Path.IsPathRooted(pending.ConfigurationPath)) throw new RegistryException("recovery_invalid");
            using (Lock("config:" + pending.ConfigurationPath.ToUpperInvariant()))
            {
                string currentStore = Data.Hash(PrivateFiles.Read(StorePath)); string currentConfig = Data.Hash(PrivateFiles.Read(pending.ConfigurationPath));
                if ((currentStore != pending.BeforeStore && currentStore != Data.Hash(pending.Store)) || (currentConfig != pending.BeforeConfig && currentConfig != Data.Hash(pending.Config))) throw new RegistryException("recovery_conflict");
                // Always, even when the target already holds the pending bytes: an unsafe target fails before the store moves forward or the journal is dropped.
                PrivateFiles.CheckConfiguration(pending.ConfigurationPath);
                if (currentStore != Data.Hash(pending.Store)) PrivateFiles.AtomicWrite(StorePath, pending.Store, true);
                if (currentConfig != Data.Hash(pending.Config)) PrivateFiles.WriteConfiguration(pending.ConfigurationPath, pending.Config);
                File.Delete(JournalPath);
            }
        }
        internal void Recover()
        {
            using (Lock("store")) RecoverUnlocked();
        }
        internal StoreState Read()
        {
            using (Lock("store")) { RecoverUnlocked(); return LoadUnlocked(); }
        }
        internal string Stamp(string url)
        {
            using (Lock("store"))
            {
                RecoverUnlocked(); var state = LoadUnlocked(); var r = state.Records.FirstOrDefault(x => x.Url == url);
                var doc = new TomlDocument(PrivateFiles.Decode(PrivateFiles.Read(Paths.ConfigurationFile)));
                return (r == null ? "absent" : r.Generation) + ":" + doc.Fingerprint(url);
            }
        }
        internal void Change(string url, string expected, Func<CredentialRecord, CredentialRecord> change, bool writeConfiguration)
        {
            using (Lock("store"))
            {
                RecoverUnlocked();
                using (Lock("config:" + Paths.ConfigurationFile.ToUpperInvariant()))
                {
                    byte[] beforeStore = PrivateFiles.Read(StorePath), beforeConfig = PrivateFiles.Read(Paths.ConfigurationFile);
                    var state = LoadUnlocked(); var previous = state.Records.FirstOrDefault(x => x.Url == url);
                    var doc = new TomlDocument(PrivateFiles.Decode(beforeConfig));
                    string current = (previous == null ? "absent" : previous.Generation) + ":" + doc.Fingerprint(url);
                    if (expected != null && expected != current) throw new RegistryException("settings_conflict");
                    var next = change(previous == null ? null : previous.Copy());
                    if (next != null) { next.Url = url; next.Generation = Guid.NewGuid().ToString("N"); }
                    state.Records.RemoveAll(x => x.Url == url); if (next != null) state.Records.Add(next);
                    var newStore = Protect(Data.Utf8.GetBytes(JsonConvert.SerializeObject(state)));
                    var newConfig = writeConfiguration ? PrivateFiles.EncodeLike(doc.Set(url, next == null ? null : next.Access, next != null && next.Kind == CredentialKind.Basic), beforeConfig) : beforeConfig;
                    if (Data.Hash(PrivateFiles.Read(StorePath)) != Data.Hash(beforeStore) || Data.Hash(PrivateFiles.Read(Paths.ConfigurationFile)) != Data.Hash(beforeConfig)) throw new RegistryException("settings_conflict");
                    if (!writeConfiguration)
                    {
                        PrivateFiles.AtomicWrite(StorePath, newStore, true); return;
                    }
                    PrivateFiles.CheckConfiguration(Paths.ConfigurationFile);
                    var pending = new PendingWrite { ConfigurationPath = Paths.ConfigurationFile, BeforeStore = Data.Hash(beforeStore), BeforeConfig = Data.Hash(beforeConfig), Store = newStore, Config = newConfig };
                    PrivateFiles.AtomicWrite(JournalPath, Protect(Data.Utf8.GetBytes(JsonConvert.SerializeObject(pending))), true);
                    Hit("journal"); PrivateFiles.AtomicWrite(StorePath, newStore, true); Hit("store");
                    if (Data.Hash(PrivateFiles.Read(Paths.ConfigurationFile)) != Data.Hash(beforeConfig)) throw new RegistryException("recovery_conflict");
                    PrivateFiles.WriteConfiguration(Paths.ConfigurationFile, newConfig); Hit("config"); File.Delete(JournalPath);
                }
            }
        }
        private void Hit(string stage) { if (Fault != null) Fault(stage); }
        internal List<CredentialSummary> Summaries()
        {
            using (Lock("store"))
            {
                RecoverUnlocked(); var state = LoadUnlocked(); var doc = new TomlDocument(PrivateFiles.Decode(PrivateFiles.Read(Paths.ConfigurationFile)));
                return state.Records.Select(r => r.Url).Union(doc.Urls, StringComparer.Ordinal).Select(url =>
                {
                    var record = state.Records.FirstOrDefault(r => r.Url == url);
                    return new CredentialSummary { Url = url, Kind = record == null ? "External" : record.Kind.ToString(), Account = record == null ? "" : record.Account, ExpiresAt = record == null ? 0 : record.ExpiresAt, NeedsLogin = record != null && record.NeedsLogin, InFlight = record != null && record.InFlight, Configured = doc.HasCredential(url), External = record == null, ConfigurationPath = record == null ? Paths.ConfigurationFile : record.ConfigurationPath };
                }).ToList();
            }
        }
    }
}
