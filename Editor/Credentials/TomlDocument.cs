using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal sealed class TomlEntry
    {
        internal int Start, End, ValueStart, ValueEnd;
        internal string Key, StringValue;
    }
    internal sealed class TomlSection
    {
        internal string Url;
        // Start: the header line; End: after the header line or the last entry line. Trailing standalone comments are outside.
        internal int Start, End;
        internal readonly Dictionary<string, TomlEntry> Entries = new Dictionary<string, TomlEntry>(StringComparer.Ordinal);
    }
    internal sealed class TomlDocument
    {
        private readonly string text;
        private int position;
        private readonly Dictionary<string, TomlSection> sections = new Dictionary<string, TomlSection>(StringComparer.Ordinal);
        private readonly HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> tables = new HashSet<string>(StringComparer.Ordinal);
        internal TomlDocument(string text)
        {
            if (text == null || text.Length > 1048576 || text.IndexOf('\0') >= 0) throw new RegistryException("toml_invalid");
            this.text = text; Parse();
        }
        internal IEnumerable<string> Urls { get { return sections.Keys; } }
        internal bool HasCredential(string url)
        {
            TomlSection s; return sections.TryGetValue(url, out s) && (s.Entries.ContainsKey("token") || s.Entries.ContainsKey("_auth"));
        }
        internal string Token(string url)
        {
            TomlSection s; TomlEntry e;
            return sections.TryGetValue(url, out s) && s.Entries.TryGetValue("token", out e) ? e.StringValue : null;
        }
        internal string Basic(string url)
        {
            TomlSection s; TomlEntry e;
            return sections.TryGetValue(url, out s) && s.Entries.TryGetValue("_auth", out e) ? e.StringValue : null;
        }
        internal string Fingerprint(string url)
        {
            TomlSection s;
            if (!sections.TryGetValue(url, out s)) return "absent";
            return Data.Hash(string.Join("\n", s.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + text.Substring(e.Value.ValueStart, e.Value.ValueEnd - e.Value.ValueStart))));
        }
        internal string Set(string url, string token) { return Set(url, token, false); }
        // basic=true writes the value as _auth; either way the competing token/_auth entry is removed.
        internal string Set(string url, string token, bool basic)
        {
            UrlRules.Registry(url); if (token != null) Data.ValidateToken(token);
            string field = basic ? "_auth" : "token", competing = basic ? "token" : "_auth";
            TomlSection section; string result = text; string newline = text.Contains("\r\n") ? "\r\n" : "\n";
            if (!sections.TryGetValue(url, out section))
            {
                if (token == null) return text;
                result = text + (text.EndsWith("\n", StringComparison.Ordinal) || text.Length == 0 ? "" : newline) + newline + "[npmAuth." + Data.Quote(url) + "]" + newline + field + " = " + Data.Quote(token) + newline + "alwaysAuth = true" + newline;
            }
            else
            {
                // Deletion removes the whole target table: header through its last entry, with comments between them.
                // Standalone comments and blank lines after the last entry stay; they may introduce the next table.
                if (token == null)
                {
                    result = text.Remove(section.Start, section.End - section.Start);
                    new TomlDocument(result); return result;
                }
                var edits = new List<Tuple<int,int,string>>();
                TomlEntry old;
                if (section.Entries.TryGetValue(competing, out old)) edits.Add(Tuple.Create(old.Start, old.End - old.Start, ""));
                if (section.Entries.TryGetValue(field, out old)) edits.Add(Tuple.Create(old.ValueStart, old.ValueEnd - old.ValueStart, Data.Quote(token)));
                // Inserted right after the last entry, so a trailing comment of the next table stays outside this table.
                else edits.Add(Tuple.Create(section.End, 0, (section.End > 0 && text[section.End - 1] != '\n' ? newline : "") + field + " = " + Data.Quote(token) + newline));
                foreach (var edit in edits.OrderByDescending(e => e.Item1)) result = result.Remove(edit.Item1, edit.Item2).Insert(edit.Item1, edit.Item3);
            }
            new TomlDocument(result); return result;
        }
        private void Parse()
        {
            var table = new List<string>(); TomlSection current = null;
            while (position < text.Length)
            {
                int lineStart = position; Horizontal();
                if (position == text.Length) break;
                if (text[position] == '#' || text[position] == '\r' || text[position] == '\n') { EndLine(); continue; }
                if (text[position] == '[')
                {
                    position++;
                    if (position < text.Length && text[position] == '[') throw new RegistryException("toml_array_table_unsupported");
                    table = KeyPath(']'); Require(']');
                    string name = PathKey(table);
                    if (!tables.Add(name)) throw new RegistryException("toml_duplicate_table");
                    current = null;
                    if (table.Count > 0 && table[0] == "npmAuth")
                    {
                        if (table.Count != 2) { if (table.Count > 2) throw new RegistryException("toml_auth_structure"); }
                        else
                        {
                            UrlRules.Registry(table[1]);
                            current = new TomlSection { Url = table[1], Start = lineStart }; sections.Add(table[1], current);
                        }
                    }
                    EndLine(); if (current != null) current.End = position; continue;
                }
                var key = KeyPath('='); Require('='); Horizontal(); int valueStart = position;
                string stringValue = Value(0); int valueEnd = position;
                var absolute = table.Concat(key).ToList();
                string path = PathKey(absolute);
                if (!keys.Add(path)) throw new RegistryException("toml_duplicate_key");
                if (absolute.Count > 0 && absolute[0] == "npmAuth" && (current == null || key.Count != 1)) throw new RegistryException("toml_auth_structure");
                EndLine();
                if (current != null)
                {
                    string field = key[0];
                    if ((field == "token" || field == "_auth" || field == "email") && stringValue == null) throw new RegistryException("toml_auth_type");
                    if (field == "alwaysAuth" && text.Substring(valueStart, valueEnd - valueStart) != "true" && text.Substring(valueStart, valueEnd - valueStart) != "false") throw new RegistryException("toml_auth_type");
                    current.Entries.Add(field, new TomlEntry { Start = lineStart, End = position, ValueStart = valueStart, ValueEnd = valueEnd, StringValue = stringValue, Key = field });
                    current.End = position;
                }
            }
            foreach (string key in keys)
                if (tables.Contains(key) || keys.Any(other => other != key && other.StartsWith(key + "\0", StringComparison.Ordinal))) throw new RegistryException("toml_key_conflict");
        }
        private static string PathKey(IEnumerable<string> parts) { return string.Join("\0", parts); }
        private List<string> KeyPath(char end)
        {
            var result = new List<string>();
            while (true)
            {
                Horizontal(); if (position >= text.Length) throw new RegistryException("toml_syntax");
                string key;
                if (text[position] == '"' || text[position] == '\'') key = String(false);
                else
                {
                    int start = position;
                    while (position < text.Length && (char.IsLetterOrDigit(text[position]) && text[position] < 128 || text[position] == '_' || text[position] == '-')) position++;
                    if (position == start) throw new RegistryException("toml_key"); key = text.Substring(start, position - start);
                }
                if (key.IndexOf('\0') >= 0) throw new RegistryException("toml_key"); result.Add(key); Horizontal();
                if (position < text.Length && text[position] == '.') { position++; continue; }
                if (position >= text.Length || text[position] != end) throw new RegistryException("toml_syntax");
                return result;
            }
        }
        private string Value(int depth)
        {
            if (depth > 24 || position >= text.Length) throw new RegistryException("toml_value");
            if (text[position] == '"' || text[position] == '\'') return String(true);
            if (text[position] == '[')
            {
                position++; SpaceAndComments();
                while (position < text.Length && text[position] != ']')
                {
                    Value(depth + 1); SpaceAndComments();
                    if (position < text.Length && text[position] == ']') break;
                    Require(','); SpaceAndComments();
                }
                Require(']'); return null;
            }
            if (text[position] == '{')
            {
                position++; Horizontal(); var local = new HashSet<string>();
                while (position < text.Length && text[position] != '}')
                {
                    var key = KeyPath('='); if (!local.Add(PathKey(key))) throw new RegistryException("toml_duplicate_key"); Require('='); Horizontal(); Value(depth + 1); Horizontal();
                    if (position < text.Length && text[position] == '}') break;
                    Require(','); Horizontal(); if (position < text.Length && text[position] == '}') throw new RegistryException("toml_syntax");
                }
                Require('}'); return null;
            }
            int start = position;
            while (position < text.Length && "\r\n#]},".IndexOf(text[position]) < 0) position++;
            while (position > start && (text[position - 1] == ' ' || text[position - 1] == '\t')) position--;
            string value = text.Substring(start, position - start);
            if (value == "true" || value == "false" || Regex.IsMatch(value, @"^[+-]?(?:inf|nan)$")) return null;
            if (Regex.IsMatch(value, @"^[+-]?(?:0|[1-9](?:_?[0-9])*)(?:\.[0-9](?:_?[0-9])*)?(?:[eE][+-]?[0-9](?:_?[0-9])*)?$")) return null;
            if (Regex.IsMatch(value, @"^(?:0x[0-9A-Fa-f](?:_?[0-9A-Fa-f])*|0o[0-7](?:_?[0-7])*|0b[01](?:_?[01])*)$")) return null;
            DateTimeOffset date;
            if (Regex.IsMatch(value, @"^(?:\d{4}-\d{2}-\d{2}(?:[Tt ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:[Zz]|[+-]\d{2}:\d{2})?)?|\d{2}:\d{2}:\d{2}(?:\.\d+)?)$") && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return null;
            throw new RegistryException("toml_value");
        }
        private string String(bool allowMultiline)
        {
            char quote = text[position++]; bool multi = position + 1 < text.Length && text[position] == quote && text[position + 1] == quote;
            if (multi) { if (!allowMultiline) throw new RegistryException("toml_key"); position += 2; if (position < text.Length && text[position] == '\r') position++; if (position < text.Length && text[position] == '\n') position++; }
            var result = new StringBuilder();
            while (position < text.Length)
            {
                char c = text[position++];
                if (c == quote)
                {
                    if (!multi) return result.ToString();
                    if (position + 1 < text.Length && text[position] == quote && text[position + 1] == quote)
                    {
                        position += 2;
                        for (int i = 0; i < 2 && position < text.Length && text[position] == quote; i++) { result.Append(quote); position++; }
                        return result.ToString();
                    }
                }
                if ((!multi && (c == '\r' || c == '\n')) || c < 32 && c != '\t' && c != '\r' && c != '\n' || c == 127) throw new RegistryException("toml_string");
                if (quote == '"' && c == '\\')
                {
                    if (position >= text.Length) break;
                    c = text[position++];
                    if (multi && char.IsWhiteSpace(c))
                    {
                        bool newline = c == '\r' || c == '\n';
                        while (position < text.Length && char.IsWhiteSpace(text[position])) { newline |= text[position] == '\r' || text[position] == '\n'; position++; }
                        if (!newline) throw new RegistryException("toml_string"); continue;
                    }
                    switch (c)
                    {
                        case 'b': result.Append('\b'); break; case 't': result.Append('\t'); break; case 'n': result.Append('\n'); break; case 'f': result.Append('\f'); break; case 'r': result.Append('\r'); break;
                        case '"': result.Append('"'); break; case '\\': result.Append('\\'); break;
                        case 'u': case 'U':
                            int count = c == 'u' ? 4 : 8; int code;
                            if (position + count > text.Length || !int.TryParse(text.Substring(position, count), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code) || code < 0 || code > 0x10ffff || code >= 0xd800 && code <= 0xdfff) throw new RegistryException("toml_string");
                            result.Append(char.ConvertFromUtf32(code)); position += count; break;
                        default: throw new RegistryException("toml_string");
                    }
                }
                else result.Append(c);
            }
            throw new RegistryException("toml_string");
        }
        private void Horizontal() { while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++; }
        private void SpaceAndComments()
        {
            while (position < text.Length)
            {
                if (char.IsWhiteSpace(text[position])) position++;
                else if (text[position] == '#') { while (position < text.Length && text[position] != '\n') position++; }
                else break;
            }
        }
        private void EndLine()
        {
            Horizontal(); if (position < text.Length && text[position] == '#') while (position < text.Length && text[position] != '\r' && text[position] != '\n') position++;
            if (position == text.Length) return;
            if (text[position] == '\r') position++;
            if (position < text.Length && text[position] == '\n') { position++; return; }
            if (position != text.Length) throw new RegistryException("toml_syntax");
        }
        private void Require(char value) { if (position >= text.Length || text[position++] != value) throw new RegistryException("toml_syntax"); }
    }
}
