using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine.UIElements;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal static class RegistryCredentialsSettingsProvider
    {
        private const string SettingsPath = "Project/Package Manager/Registry Credentials";
        private const string MainUxmlPath = "Packages/com.amari-noa.amari-unity-package-registry-manager/Editor/UI/RegistryCredentials.uxml";
        private const string RowUxmlPath = "Packages/com.amari-noa.amari-unity-package-registry-manager/Editor/UI/RegistryCredentialsRow.uxml";
        private const string ScopeRowUxmlPath = "Packages/com.amari-noa.amari-unity-package-registry-manager/Editor/UI/RegistryCredentialsScopeRow.uxml";
        private const string HiddenClass = "preview-hidden";

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RegistryCredentialsSettingsProvider).Assembly);
            var displayName = package?.displayName ?? "Amari Unity Package Registry Manager";
            var state = State.Create();
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Registry Credentials",
                activateHandler = (_, root) => Render(root, displayName, state),
                keywords = new HashSet<string>(new[] { "registry", "credentials", "Package Manager", "認証" })
            };
        }

        private static void Render(VisualElement root, string displayName, State state)
        {
            var mainTemplate = LoadTemplate(MainUxmlPath);
            var rowTemplate = LoadTemplate(RowUxmlPath);
            var scopeRowTemplate = LoadTemplate(ScopeRowUxmlPath);
            root.Clear();
            mainTemplate.CloneTree(root);

            root.Q<Label>("package-display-name").text = displayName;

            BindRegistryPane(root, displayName, state, rowTemplate, scopeRowTemplate);
            BindSettingsFiles(root, displayName, state);
        }

        private static void BindSettingsFiles(VisualElement root, string displayName, State state)
        {
            var import = root.Q<Button>("registry-import-button");
            import.clicked += () =>
            {
                var path = EditorUtility.OpenFilePanel("レジストリ設定をインポート", "", "json");
                if (string.IsNullOrEmpty(path)) return;
                try
                {
                    var entries = ReadSettingsFile(path);
                    if (ImportSettings(state, entries, name => EditorUtility.DisplayDialogComplex(
                        "同じURLのレジストリがあります",
                        $"「{name}」への取り込み方法を選択してください。\nスコープを追加: 既存の名前・スコープを保持します。\n置換: 名前・スコープをファイルの内容にします。",
                        "スコープを追加", "キャンセル", "名前・スコープを置換")))
                        Render(root, displayName, state);
                }
                catch (Exception error) when (IsSettingsFileError(error))
                {
                    EditorUtility.DisplayDialog("インポートできませんでした",
                        error is ArgumentException ? error.Message : "ファイルを読み込めません。ファイルとアクセス権を確認してください。", "閉じる");
                }
            };

            var export = root.Q<Button>("registry-export-button");
            export.SetEnabled(state.SelectedRegistry != null);
            export.clicked += () =>
            {
                try
                {
                    var json = ExportSettings(state.SelectedRegistry);
                    var path = EditorUtility.SaveFilePanel("レジストリ設定をエクスポート", "", "registry-settings.json", "json");
                    if (string.IsNullOrEmpty(path)) return;
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                }
                catch (Exception error) when (IsSettingsFileError(error))
                {
                    EditorUtility.DisplayDialog("エクスポートできませんでした",
                        error is ArgumentException ? error.Message : "ファイルを書き込めません。保存先とアクセス権を確認してください。", "閉じる");
                }
            };
        }

        private static bool IsSettingsFileError(Exception error) => error is ArgumentException ||
            error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException;

        private static RegistrySettingsJson.Entry[] ReadSettingsFile(string path)
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new ArgumentException("設定ファイルが大きすぎます。");
            return RegistrySettingsJson.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }

        private static string ExportSettings(Registry item) => RegistrySettingsJson.Serialize(new[]
        {
            new RegistrySettingsJson.Entry
            {
                name = item.Name, url = item.Url, scopes = item.Scopes.Select(scope => scope.Value).ToArray()
            }
        });

        private static bool ImportSettings(State state, RegistrySettingsJson.Entry[] entries, Func<string, int> chooseConflict)
        {
            entries = RegistrySettingsJson.Validate(entries);
            // Collect all decisions before modifying the catalog, so cancel leaves it unchanged.
            var choices = new int[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var existing = state.Registries.FirstOrDefault(item => string.Equals(item.Url, entry.url, StringComparison.OrdinalIgnoreCase));
                if (existing == null) continue;
                choices[i] = chooseConflict(existing.Name);
                if (choices[i] != 0 && choices[i] != 2) return false;
                if (choices[i] == 2 && existing.Scopes.Any(scope =>
                    (scope.InProject || scope.ReferenceCount > 0) && !entry.scopes.Contains(scope.Value)))
                    throw new ArgumentException("置換で登録中または参照中のスコープが失われます。先にプロジェクトから削除するか、スコープの追加を選択してください。");
            }
            var projected = state.Registries.Select(item =>
                new Registry(item.Id, item.Name, item.Url, new List<string>(), true, false, 0)).ToList();
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var existing = projected.FirstOrDefault(item => string.Equals(item.Url, entry.url, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    projected.Add(new Registry(0, entry.name, entry.url, new List<string>(), true, false, 0));
                else if (choices[i] == 2)
                    existing.Name = entry.name;
            }
            foreach (var item in projected)
                if (!ValidateRegistryIdentity(item.Name, item.Url, projected, item, out var error))
                    throw new ArgumentException(error);

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var existing = state.Registries.FirstOrDefault(item => string.Equals(item.Url, entry.url, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new Registry(state.NextId(), entry.name, entry.url, entry.scopes.ToList(), true, false, 0);
                    state.Registries.Add(existing);
                }
                else if (choices[i] == 2)
                {
                    existing.Name = entry.name;
                    existing.Scopes = entry.scopes.Select(value => existing.Scopes.FirstOrDefault(scope => scope.Value == value)
                        ?? new Scope(value, false, 0)).ToList();
                }
                else
                {
                    foreach (var value in entry.scopes)
                        if (!existing.Scopes.Any(scope => scope.Value == value)) existing.Scopes.Add(new Scope(value, false, 0));
                }
                state.SelectedRegistryId = existing.Id;
            }
            return true;
        }

        private static VisualTreeAsset LoadTemplate(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            if (asset == null)
                throw new InvalidOperationException($"Registry Credentials UI asset is missing: {path}");
            return asset;
        }

        private static void BindRegistryPane(VisualElement root, string displayName, State state, VisualTreeAsset rowTemplate, VisualTreeAsset scopeRowTemplate)
        {
            var list = root.Q<ListView>("registry-list");
            list.itemsSource = state.Registries;
            list.makeItem = () => rowTemplate.CloneTree();
            list.bindItem = (element, index) => BindRegistryRow(element, state.Registries[index]);
            list.selectionType = SelectionType.Single;
            list.selectedIndex = state.Registries.FindIndex(x => x.Id == state.SelectedRegistryId);
            list.selectionChanged += selection =>
            {
                state.SelectedRegistryId = selection.Cast<Registry>().FirstOrDefault()?.Id;

                Render(root, displayName, state);
            };

            list.itemIndexChanged += (_, __) =>
            {
                if (list.selectedItem is Registry current)
                    state.SelectedRegistryId = current.Id;
            };

            var add = list.Q<Button>(BaseListView.footerAddButtonName);
            add.name = "add-registry";
            add.clickable = new Clickable(() =>
            {
                RegistryAddWindow.Open((name, url) =>
                {
                    if (!TryAddRegistry(state, name, url, out var error)) return error;
                    if (root.panel != null) Render(root, displayName, state);
                    return null;
                });
            });

            var selected = state.SelectedRegistry;
            var remove = list.Q<Button>(BaseListView.footerRemoveButtonName);
            remove.name = "delete-registry";
            remove.SetEnabled(selected != null && !selected.InProject && selected.ReferenceCount == 0);
            remove.tooltip = selected == null ? "削除するレジストリを選択してください。" :
                selected.InProject ? "現在のプロジェクトに登録中のため削除できません。" :
                selected.ReferenceCount > 0 ? "参照中パッケージがあるため削除できません。" :
                "選択中のレジストリをPC共通カタログから削除します。";
            remove.clickable = new Clickable(() =>
            {
                var current = state.SelectedRegistry;
                if (current == null) return;
                if (current.InProject || current.ReferenceCount > 0) return;
                state.Registries.Remove(current);
                state.SelectedRegistryId = state.Registries.FirstOrDefault()?.Id;
                Render(root, displayName, state);
            });

            SetVisible(root.Q("registry-details-view"), selected != null);
            if (selected != null) BindRegistryDetails(root, state, selected, scopeRowTemplate);
        }

        private static void BindRegistryRow(VisualElement element, Registry item)
        {
            element.name = "registry-row-" + item.Id;
            var name = element.Q<Label>("row-name");
            name.text = item.Name;
            name.tooltip = item.Name;
            var url = element.Q<Label>("row-url");
            url.text = item.Url;
            url.tooltip = item.Url;
        }

        private static void BindRegistryDetails(VisualElement root, State state, Registry item, VisualTreeAsset scopeRowTemplate)
        {
            var details = root.Q("registry-details-view");
            details.name = "registry-" + item.Id;
            var name = root.Q<TextField>("registry-detail-name");
            var url = root.Q<TextField>("registry-detail-url");
            name.SetValueWithoutNotify(item.Name);
            url.SetValueWithoutNotify(item.Url);
            Action<string, string> updateIdentity = (candidateName, candidateUrl) =>
            {
                var accepted = TryUpdateRegistryIdentity(state, item, candidateName, candidateUrl, out var error);
                name.SetValueWithoutNotify(item.Name);
                url.SetValueWithoutNotify(item.Url);
                if (!accepted)
                {
                    EditorUtility.DisplayDialog("レジストリを更新できませんでした", error, "閉じる");
                    return;
                }
                root.Q<ListView>("registry-list").RefreshItems();
                var connection = root.Q<Button>("test-connection-" + item.Id);
                connection.SetEnabled(!item.AuthRequired || item.CredentialConfigured);
                connection.tooltip = item.AuthRequired && !item.CredentialConfigured
                    ? "トークン取得は未実装です（UIモック）。" : "接続確認は未実装です（UIモック）。";
            };
            name.RegisterValueChangedCallback(change => updateIdentity(change.newValue, item.Url));
            url.RegisterValueChangedCallback(change => updateIdentity(item.Name, change.newValue));
            BindScopeList(root, root.Q<ListView>("registry-detail-scopes"), item, scopeRowTemplate);

            var setupAuth = root.Q<Button>("setup-auth");
            setupAuth.SetEnabled(item.AuthRequired);
            setupAuth.tooltip = "認証設定は未実装です（UIモック）。";

            var test = root.Q<Button>("test-connection");
            test.name = "test-connection-" + item.Id;
            test.SetEnabled(!item.AuthRequired || item.CredentialConfigured);
            test.tooltip = item.AuthRequired && !item.CredentialConfigured
                ? "トークン取得は未実装です（UIモック）。" : "接続確認は未実装です（UIモック）。";
        }

        private static void BindScopeList(VisualElement root, ListView list, Registry item, VisualTreeAsset rowTemplate)
        {
            var remove = list.Q<Button>(BaseListView.footerRemoveButtonName);
            remove.name = "delete-scope";
            Action updateRemove = () =>
            {
                var selected = list.selectedItem as Scope;
                remove.SetEnabled(selected != null && !selected.InProject && selected.ReferenceCount == 0);
                remove.tooltip = selected == null ? "削除するスコープを選択してください。" :
                    selected.InProject ? "先に現在のプロジェクトから削除してください。" :
                    selected.ReferenceCount > 0 ? "参照中パッケージがあるため削除できません。" :
                    "選択中のスコープをカタログから削除します。";
            };

            list.itemsSource = item.Scopes;
            list.makeItem = () => rowTemplate.CloneTree();
            list.bindItem = (element, index) =>
            {
                var scope = item.Scopes[index];
                var field = element.Q<TextField>("scope-value");
                var projectAction = element.Q<Button>("scope-project-action");
                Action updateAction = () =>
                {
                    projectAction.text = scope.InProject ? "プロジェクトから削除" : "プロジェクトに追加";
                    projectAction.SetEnabled(scope.InProject ? scope.ReferenceCount == 0 : !string.IsNullOrWhiteSpace(scope.Value));
                    projectAction.tooltip = scope.InProject && scope.ReferenceCount > 0
                        ? "参照中パッケージがあるため削除できません。"
                        : scope.InProject ? "現在のプロジェクトからダミー削除します。" : "現在のプロジェクトへダミー登録します。";
                };
                if (field.userData is EventCallback<ChangeEvent<string>> previous)
                    field.UnregisterValueChangedCallback(previous);
                field.SetValueWithoutNotify(scope.Value);
                EventCallback<ChangeEvent<string>> changed = change =>
                {
                    scope.Value = change.newValue;
                    updateAction();
                };
                field.userData = changed;
                field.RegisterValueChangedCallback(changed);
                updateAction();
                projectAction.clickable = new Clickable(() =>
                {
                    if (scope.InProject ? scope.ReferenceCount > 0 : string.IsNullOrWhiteSpace(scope.Value)) return;
                    scope.InProject = !scope.InProject;

                    updateAction();
                    updateRemove();
                    root.Q<ListView>("registry-list").RefreshItems();
                    var deleteRegistry = root.Q<Button>("delete-registry");
                    deleteRegistry.SetEnabled(!item.InProject && item.ReferenceCount == 0);
                    deleteRegistry.tooltip = item.InProject ? "現在のプロジェクトに登録中のため削除できません。" :
                        item.ReferenceCount > 0 ? "参照中パッケージがあるため削除できません。" :
                        "選択中のレジストリをPC共通カタログから削除します。";
                });
            };

            var add = list.Q<Button>(BaseListView.footerAddButtonName);
            add.name = "add-scope";
            add.clickable = new Clickable(() =>
            {
                item.Scopes.Add(new Scope(string.Empty, false, 0));
                list.Rebuild();
                list.selectedIndex = item.Scopes.Count - 1;
                updateRemove();
            });

            updateRemove();
            list.selectionChanged += _ => updateRemove();
            list.itemIndexChanged += (_, __) => updateRemove();
            remove.clickable = new Clickable(() =>
            {
                var selected = list.selectedItem as Scope;
                if (selected == null || selected.InProject || selected.ReferenceCount > 0) return;
                var index = item.Scopes.IndexOf(selected);
                item.Scopes.Remove(selected);
                list.Rebuild();
                list.selectedIndex = Math.Min(index, item.Scopes.Count - 1);
                updateRemove();
            });
        }

        private static void SetVisible(VisualElement element, bool visible) => element.EnableInClassList(HiddenClass, !visible);

        private static bool TryAddRegistry(State state, string name, string url, out string error)
        {
            if (!ValidateRegistryIdentity(name, url, state.Registries, null, out error)) return false;
            var added = new Registry(state.NextId(), name.Trim(), NormalizeUrl(url), new List<string>(), true, false, 0);
            state.Registries.Add(added);
            state.SelectedRegistryId = added.Id;
            return true;
        }

        private static bool TryUpdateRegistryIdentity(State state, Registry item, string name, string url, out string error)
        {
            if (!ValidateRegistryIdentity(name, url, state.Registries, item, out error)) return false;
            var normalizedUrl = NormalizeUrl(url);
            if (item.Url != normalizedUrl) item.CredentialConfigured = false;
            item.Name = name.Trim();
            item.Url = normalizedUrl;
            return true;
        }

        private static bool ValidateRegistryIdentity(string name, string url, IEnumerable<Registry> items, Registry editing, out string error)
        {
            if (string.IsNullOrWhiteSpace(name)) { error = "レジストリ名を入力してください。"; return false; }
            if (!ValidateUrl(url, out error)) return false;
            if (items.Any(item => item != editing && string.Equals(item.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            { error = "同じ名前のレジストリが既にあります。"; return false; }
            if (items.Any(item => item != editing && string.Equals(item.Url, NormalizeUrl(url), StringComparison.OrdinalIgnoreCase)))
            { error = "同じURLのレジストリが既にあります。"; return false; }
            error = null;
            return true;
        }

        private static bool ValidateUrl(string value, out string error)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = "http(s)の絶対URLを入力してください。ユーザー情報・クエリ・フラグメントは使用できません。";
                return false;
            }
            error = null;
            return true;
        }

        private static string NormalizeUrl(string value) => value.Trim().TrimEnd('/');
        private sealed class State
        {
            private int _nextId;
            internal List<Registry> Registries { get; private set; }
            internal int? SelectedRegistryId { get; set; }
            internal Registry SelectedRegistry => Registries.FirstOrDefault(x => x.Id == SelectedRegistryId);

            internal static State Create() { var state = new State(); state.Reset(); return state; }
            internal void Reset()
            {
                _nextId = 100;

                Registries = new List<Registry>
                {
                    new Registry(11, "Studio A Packages", "https://packages.studio-a.example", new List<string> { "com.studio-a" }, true, false, 0),
                    new Registry(12, "Avatar Tools", "https://tools.example", new List<string> { "com.avatar-tools", "com.avatar-shared" }, true, true, 2) { CredentialConfigured = true },
                    new Registry(13, "Open Samples", "https://samples.example", new List<string> { "com.open-samples" }, false, true, 0)
                };
                SelectedRegistryId = 11;
            }

            internal int NextId() => _nextId++;
        }

        private sealed class Scope
        {
            internal Scope(string value, bool inProject, int referenceCount)
            { Value = value; InProject = inProject; ReferenceCount = referenceCount; }
            internal string Value { get; set; }
            internal bool InProject { get; set; }
            internal int ReferenceCount { get; }
        }

        private sealed class Registry
        {
            internal Registry(int id, string name, string url, List<string> scopes, bool authRequired, bool inProject, int referenceCount)
            { Id = id; Name = name; Url = url; Scopes = scopes.Select((value, index) => new Scope(value, inProject, index == 0 ? referenceCount : 0)).ToList(); AuthRequired = authRequired; }
            internal int Id { get; }
            internal string Name { get; set; }
            internal string Url { get; set; }
            internal List<Scope> Scopes { get; set; }
            internal bool AuthRequired { get; set; }
            internal bool InProject => Scopes.Any(scope => scope.InProject);
            internal int ReferenceCount => Scopes.Sum(scope => scope.ReferenceCount);
            internal bool CredentialConfigured { get; set; }
        }
    }
}
