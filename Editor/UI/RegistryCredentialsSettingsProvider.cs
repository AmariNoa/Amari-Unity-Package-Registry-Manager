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

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RegistryCredentialsSettingsProvider).Assembly);
            var displayName = package?.displayName ?? "Amari Unity Package Registry Manager";
            var state = State.CreateShell();
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Registry Credentials",
                activateHandler = (_, root) =>
                {
                    state.Reload();
                    state.ReloadCredentials();
                    Render(root, displayName, state);
                    Watch(root, displayName, state);
                    if (state.LoadError != null) EditorUtility.DisplayDialog(RegistryText.T("catalog.title"), state.LoadError, RegistryText.T("button.close"));
                },
                deactivateHandler = () =>
                {
                    state.Unwatch?.Invoke();
                    state.CancelChecks();
                },
                keywords = new HashSet<string>(new[] { "registry", "credentials", "Package Manager", "認証" })
            };
        }

        private static string UntitledName => RegistryText.T("registry.untitled");
        private static string UnresolvedTip => RegistryText.T("tip.unresolved");
        private static string AmbiguousTip => RegistryText.T("tip.ambiguous");
        private static string DisplayName(Registry item) => string.IsNullOrWhiteSpace(item.Name) ? UntitledName : item.Name;

        private static void Render(VisualElement root, string displayName, State state)
        {
            state.RefreshProject();
            state.RefreshOrphans();
            var mainTemplate = LoadTemplate(MainUxmlPath);
            var rowTemplate = LoadTemplate(RowUxmlPath);
            var scopeRowTemplate = LoadTemplate(ScopeRowUxmlPath);
            root.Clear();
            mainTemplate.CloneTree(root);

            root.Q<Label>("package-display-name").text = displayName;
            // Before binding renames elements; binders add their own texts to Relabel.
            state.Relabel = RegistryText.Localize(root);
            BindLanguage(root, state);

            BindRegistryPane(root, displayName, state, rowTemplate, scopeRowTemplate);
            BindSettingsFiles(root, displayName, state);
        }

        // Renders once the current event has finished, so an edit handler never rebuilds the page it runs in.
        // Skipped when the page has been closed or reopened meanwhile.
        private static void RenderLater(VisualElement root, string displayName, State state)
        {
            var lifetime = state.Lifetime;
            EditorApplication.delayCall += () =>
            {
                if (root.panel != null && lifetime == state.Lifetime) Render(root, displayName, state);
            };
        }

        private static void BindLanguage(VisualElement root, State state)
        {
            var language = root.Q<DropdownField>("editor-ui-language");
            language.choices = RegistryText.Choices.ToList();
            Action show = () => language.SetValueWithoutNotify(RegistryText.Choices[RegistryText.ChoiceIndex]);
            show();
            state.Relabel += show;
            language.RegisterValueChangedCallback(change => { if (IsOwnChange(language, change)) RegistryText.Select(language.index); });
        }

        // A field's child texts (its label, its input text) also send ChangeEvent<string>, and UI Toolkit retargets them to
        // the field, so the target cannot tell them apart. The field's own change carries the value it now holds; a relabel
        // carries the label text. A child text that happens to equal the value is caught by the handlers' no-op checks.
        private static bool IsOwnChange(BaseField<string> field, ChangeEvent<string> change) =>
            change.target == field && change.newValue == field.value;

        private static void BindSettingsFiles(VisualElement root, string displayName, State state)
        {
            var import = root.Q<Button>("registry-import-button");
            import.clicked += () =>
            {
                var path = EditorUtility.OpenFilePanel(RegistryText.T("import.panelTitle"), "", "json");
                if (string.IsNullOrEmpty(path)) return;
                try
                {
                    var entries = ReadSettingsFile(path);
                    if (ImportSettings(state, entries, name => RegistryDialogs.ChooseImport(
                        RegistryText.T("import.conflict.title"), RegistryText.F("import.conflict.message", name))) && Finish(root, displayName, state))
                    {
                        Render(root, displayName, state);
                        EditorUtility.DisplayDialog(RegistryText.T("settings.title"), RegistryText.T("import.done"), RegistryText.T("button.ok"));
                    }
                }
                catch (Exception error) when (IsSettingsFileError(error))
                {
                    EditorUtility.DisplayDialog(RegistryText.T("import.failed.title"),
                        error is ArgumentException ? error.Message : RegistryText.T("import.failed.read"), RegistryText.T("button.close"));
                }
            };

            var export = root.Q<Button>("registry-export-button");
            export.SetEnabled(state.SelectedRegistry != null);
            export.clicked += () =>
            {
                try
                {
                    var json = ExportSettings(state.SelectedRegistry);
                    var path = EditorUtility.SaveFilePanel(RegistryText.T("export.panelTitle"), "", ExportFileName(state.SelectedRegistry), "json");
                    if (string.IsNullOrEmpty(path)) return;
                    File.WriteAllText(path, json, new UTF8Encoding(false));
                    EditorUtility.DisplayDialog(RegistryText.T("settings.title"), RegistryText.T("export.done"), RegistryText.T("button.ok"));
                }
                catch (Exception error) when (IsSettingsFileError(error))
                {
                    EditorUtility.DisplayDialog(RegistryText.T("export.failed.title"),
                        error is ArgumentException ? error.Message : RegistryText.T("export.failed.write"), RegistryText.T("button.close"));
                }
            };
        }

        private static bool IsSettingsFileError(Exception error) => error is ArgumentException ||
            error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException;

        private static RegistrySettingsJson.Entry[] ReadSettingsFile(string path)
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
                throw new RegistryText.Error("import.tooLarge");
            return RegistrySettingsJson.Parse(File.ReadAllText(path, new UTF8Encoding(false, true)));
        }

        // registry-settings-<host>.json; path, port, query and user info are left out. Characters invalid in file names
        // (such as IPv6 colons) become '_'; DNS dots stay, trailing dots are trimmed.
        private static string ExportFileName(Registry item)
        {
            if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri)) return "registry-settings.json";
            var invalid = Path.GetInvalidFileNameChars();
            var host = new string(uri.Host.Select(c => c == ':' || invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.');
            return host.Length == 0 ? "registry-settings.json" : "registry-settings-" + host + ".json";
        }

        // The settings file keeps its strict format: a partial catalog entry fails clearly instead of being exported.
        private static string ExportSettings(Registry item)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Url) ||
                item.Scopes.Any(scope => string.IsNullOrWhiteSpace(scope.Value)))
                throw new RegistryText.Error("export.incomplete");
            return RegistrySettingsJson.Serialize(new[]
            {
                new RegistrySettingsJson.Entry
                {
                    name = item.Name, url = item.Url, scopes = item.Scopes.Select(scope => scope.Value).ToArray()
                }
            });
        }

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
                choices[i] = chooseConflict(DisplayName(existing));
                if (choices[i] != 0 && choices[i] != 2) return false;
                if (choices[i] == 2 && existing.Scopes.Any(scope =>
                    (scope.InProject || scope.Blocked) && !entry.scopes.Contains(scope.Value)))
                    throw new RegistryText.Error("import.replaceLosesScopes");
            }
            var projected = state.Registries.Select(item =>
                new Registry(item.Id, item.Name, item.Url, new List<string>(), false, 0)).ToList();
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var existing = projected.FirstOrDefault(item => string.Equals(item.Url, entry.url, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                    projected.Add(new Registry(0, entry.name, entry.url, new List<string>(), false, 0));
                else if (choices[i] == 2)
                    existing.Name = entry.name;
            }
            foreach (var item in projected)
                // Catalog rules only: existing entries may be partial, and imported ones are complete already.
                if (!ValidateCatalogIdentity(item.Name, item.Url, projected, item, out var error))
                    throw new ArgumentException(error);

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                var existing = state.Registries.FirstOrDefault(item => string.Equals(item.Url, entry.url, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    existing = new Registry(state.NextId(), entry.name, entry.url, entry.scopes.ToList(), false, 0);
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
            var selected = state.SelectedItem;
            list.itemsSource = state.Items;
            list.makeItem = () => rowTemplate.CloneTree();
            list.bindItem = (element, index) => BindRegistryRow(element, state.Items[index]);
            list.selectionType = SelectionType.Single;
            list.selectedIndex = state.Items.IndexOf(selected);
            list.selectionChanged += selection =>
            {
                // A notification for the row already shown (e.g. delivered after this page was rebuilt) is not a new selection.
                var next = selection.Cast<Registry>().FirstOrDefault();
                if (next == state.SelectedItem) return;
                state.Select(next);
                Render(root, displayName, state);
            };

            list.itemIndexChanged += (_, __) =>
            {
                // Only catalog rows are saved; moving a transient row never writes, and it returns to the end.
                var order = state.Items.Where(item => !item.Transient).ToList();
                if (!order.SequenceEqual(state.Registries))
                {
                    state.Reorder(order);
                    if (!Finish(root, displayName, state)) return;
                }
                if (state.Items.SkipWhile(item => !item.Transient).Any(item => !item.Transient)) Render(root, displayName, state);
            };

            var add = list.Q<Button>(BaseListView.footerAddButtonName);
            add.name = "add-registry";
            add.clickable = new Clickable(() =>
            {
                RegistryAddWindow.Open((name, url) =>
                {
                    if (!TryAddRegistry(state, name, url, out var error)) return error;
                    if (!PersistCatalog(state, out error))
                    {
                        if (root.panel != null) Render(root, displayName, state);
                        return error;
                    }
                    if (root.panel != null) Render(root, displayName, state);
                    return null;
                });
            });

            var remove = list.Q<Button>(BaseListView.footerRemoveButtonName);
            remove.name = "delete-registry";
            var deleteBlock = selected == null ? null : selected.Transient ? CredentialDeleteBlock(state, selected) : RegistryDeleteBlock(state, selected);
            remove.SetEnabled(selected != null && deleteBlock == null);
            Action label = () =>
            {
                var block = selected == null ? null : selected.Transient ? CredentialDeleteBlock(state, selected) : RegistryDeleteBlock(state, selected);
                remove.tooltip = selected == null ? RegistryText.T("tip.registry.selectToDelete") :
                    block ?? RegistryText.T(selected.Transient ? "tip.registry.deleteCredential" : "tip.registry.delete");
            };
            label();
            state.Relabel += label;
            state.Relabel += list.RefreshItems;
            remove.clickable = new Clickable(() =>
            {
                if (state.SelectedItem != selected) return;
                // An unnamed row has no catalog entry: minus deletes its shared credential, never catalog or manifest data.
                if (state.SelectedItem is Registry draft && draft.Transient)
                {
                    DeleteDraftCredential(root, displayName, state, draft);
                    return;
                }
                DeleteRegistry(root, displayName, state, selected);
            });

            root.Q("registry-details-view").SetEnabled(selected != null);
            if (selected != null) BindRegistryDetails(root, displayName, state, selected, scopeRowTemplate);
        }

        private static void BindRegistryRow(VisualElement element, Registry item)
        {
            element.name = "registry-row-" + item.Id;
            element.EnableInClassList("registry-row-transient", item.Transient);
            var name = element.Q<Label>("row-name");
            name.text = DisplayName(item);
            name.tooltip = item.Transient ? RegistryText.T("tip.row.transient") : DisplayName(item);
            var url = element.Q<Label>("row-url");
            url.text = item.Url;
            url.tooltip = item.Url;
        }

        private static void BindRegistryDetails(VisualElement root, string displayName, State state, Registry item, VisualTreeAsset scopeRowTemplate)
        {
            var details = root.Q("registry-details-view");
            details.name = "registry-" + item.Id;
            var name = root.Q<TextField>("registry-detail-name");
            var url = root.Q<TextField>("registry-detail-url");
            name.SetValueWithoutNotify(item.Name);
            url.SetValueWithoutNotify(item.Url);
            BindScopeList(root, displayName, state, root.Q<ListView>("registry-detail-scopes"), item, scopeRowTemplate);
            BindCredentialActions(root, displayName, state, item);
            if (item.Transient)
            {
                // A draft's credential URL is fixed; its name and scopes are saved as they are edited.
                url.SetEnabled(false);
                Action urlTip = () => url.tooltip = RegistryText.T("tip.url.transient");
                urlTip();
                state.Relabel += urlTip;
            }

            Action<string, string> updateIdentity = (candidateName, candidateUrl) =>
            {
                // A draft's first edit promotes it into the catalog as this same object, so later edits take the path below.
                if (item.Transient)
                {
                    var draftName = (candidateName ?? "").Trim();
                    name.SetValueWithoutNotify(draftName);
                    if (draftName == item.Name) return;
                    item.Name = draftName;
                    SaveEdit(root, displayName, state, item);
                    return;
                }
                // Nothing to save or re-render when the committed text normalizes to the current identity.
                if (candidateName?.Trim() == item.Name && NormalizeUrl(candidateUrl) == item.Url)
                {
                    name.SetValueWithoutNotify(item.Name);
                    url.SetValueWithoutNotify(item.Url);
                    return;
                }
                var accepted = TryUpdateRegistryIdentity(state, item, candidateName, candidateUrl, out var error);
                name.SetValueWithoutNotify(item.Name);
                url.SetValueWithoutNotify(item.Url);
                if (!accepted)
                {
                    Notify(RegistryText.T("registry.updateFailed.title"), error);
                    return;
                }
                if (!Finish(root, displayName, state)) return;
                // Re-render after this event: a URL change can hide or reveal a transient row and change which credential this entry uses.
                RenderLater(root, displayName, state);
            };
            name.RegisterValueChangedCallback(change => { if (IsOwnChange(name, change)) updateIdentity(change.newValue, item.Url); });
            url.RegisterValueChangedCallback(change => { if (IsOwnChange(url, change)) updateIdentity(item.Name, change.newValue); });
        }

        // Autosave for an edit of this row, called from its event handlers. A draft is promoted into the catalog first;
        // the page is re-rendered only after the event, never inside it.
        private static bool SaveEdit(VisualElement root, string displayName, State state, Registry item)
        {
            if (!item.Transient) return Finish(root, displayName, state);
            var promoted = TryPromote(state, item, out var error);
            if (!promoted) Notify(RegistryText.T("catalog.title"), error);
            RenderLater(root, displayName, state);
            return promoted;
        }

        // Moves a current draft into the catalog as the same object, so rows and callbacks bound to it stay valid. Only a
        // draft with exactly one readable credential URL qualifies, so no equivalent credential is picked on the user's
        // behalf; the credential itself is never changed. On failure the draft keeps its input for another try.
        private static bool TryPromote(State state, Registry draft, out string error)
        {
            error = CredentialDeleteBlock(state, draft);
            if (error != null) return false;
            if (state.Stamp == null) { error = RegistryText.T("catalog.cannotSave"); return false; }
            var url = NormalizeUrl(draft.CredentialUrl);
            if (!ValidateCatalogIdentity(draft.Name, url, state.Registries, null, out error)) return false;
            var draftId = draft.Id;
            var draftUrl = draft.Url;
            var selected = state.SelectedItem == draft;
            draft.Id = state.NextId();
            draft.Url = url;
            draft.Transient = false;
            state.Registries.Add(draft);
            if (!PersistCatalog(state, out error))
            {
                // A conflict has already reloaded the catalog.
                state.Registries.Remove(draft);
                draft.Id = draftId;
                draft.Url = draftUrl;
                draft.Transient = true;
                return false;
            }
            state.Drafts.Remove(draft.DraftKey);
            state.Orphans.Remove(draft);
            draft.DraftKey = null;
            draft.CredentialUrl = null;
            if (selected) state.Select(draft);
            return true;
        }

        // Null when the draft is current and names exactly one readable credential URL; shared by promotion and deletion.
        private static string CredentialDeleteBlock(State state, Registry draft)
        {
            if (!draft.Transient || !state.Orphans.Contains(draft) || !state.Drafts.TryGetValue(draft.DraftKey, out var current) || current != draft)
                return RegistryText.T("block.draftStale");
            if (state.CredentialError != null) return CredentialMessages.Describe(state.CredentialError);
            if (draft.CredentialUrl == null || !state.Credentials.ContainsKey(draft.CredentialUrl)) return AmbiguousTip;
            return null;
        }

        // Deletes only this exact URL's shared credential through the service, with the stamp captured before confirming,
        // so a credential replaced meanwhile (another Editor or during the dialog) is rejected as settings_conflict.
        private static void DeleteDraftCredential(VisualElement root, string displayName, State state, Registry draft)
        {
            var title = RegistryText.T("deleteCredential.failed.title");
            var error = CredentialDeleteBlock(state, draft);
            if (error != null)
            {
                Notify(title, error);
                return;
            }
            var url = draft.CredentialUrl;
            RegistryService service;
            string stamp;
            try
            {
                service = CredentialServices.Create();
                stamp = service.Settings.Stamp(url);
            }
            catch (Exception failure)
            {
                Notify(title, RegistryText.F("common.targetUrl", url) + "\n\n" + CredentialMessages.Describe(failure));
                return;
            }
            if (!Confirm(RegistryText.T("deleteCredential.confirm.title"), RegistryText.F("common.targetUrl", url) + "\n\n" + RegistryText.T("deleteCredential.confirm.message")))
                return;
            try { service.Delete(url, stamp); }
            catch (Exception failure)
            {
                Notify(title, RegistryText.F("common.targetUrl", url) + "\n\n" + CredentialMessages.Describe(failure));
                state.ReloadCredentials();
                Render(root, displayName, state);
                return;
            }
            state.ReloadCredentials();
            state.Select(state.Registries.FirstOrDefault());
            Render(root, displayName, state);
        }

        // Null when this catalog entry and the shared credential its URL resolves to may be deleted together.
        private static string RegistryDeleteBlock(State state, Registry item)
        {
            if (!state.Registries.Contains(item) || state.SelectedRegistry != item) return RegistryText.T("block.registryStale");
            if (state.Stamp == null) return RegistryText.T("catalog.cannotSave");
            // Without a URL the entry is in no project and has no credential of its own.
            if (string.IsNullOrWhiteSpace(item.Url)) return null;
            if (state.ManifestError != null) return state.ManifestError;
            if (item.InProject) return RegistryText.T("block.inProject");
            if (item.ReferenceCount > 0) return RegistryText.T("block.referenced");
            if (item.Blocked) return UnresolvedTip;
            if (state.CredentialError != null) return CredentialMessages.Describe(state.CredentialError);
            state.CredentialUrlOf(item, out var ambiguous);
            return ambiguous ? AmbiguousTip : null;
        }

        // Deletes the catalog entry and its URL's shared credential in one operation. The credential is deleted inside the
        // catalog save, after the catalog stamp check and staging and before the replace: a stale or unwritable catalog
        // leaves the credential alone, and a credential failure leaves the entry. Not atomic: if the final replace fails
        // after the credential is gone, the entry stays and the failure is reported for a retry.
        private static void DeleteRegistry(VisualElement root, string displayName, State state, Registry item)
        {
            var title = RegistryText.T("deleteRegistry.failed.title");
            // Fresh project and credential state, so neither a stale page nor a missed credential spelling is trusted.
            state.RefreshProject();
            state.ReloadCredentials();
            var error = RegistryDeleteBlock(state, item);
            if (error != null)
            {
                Notify(title, error);
                Render(root, displayName, state);
                return;
            }
            var target = state.CredentialUrlOf(item, out _);
            var hasCredential = target != null && state.Credentials.ContainsKey(target);
            var catalogStamp = state.Stamp;
            RegistryService service;
            string credentialStamp = null;
            try
            {
                service = CredentialServices.Create();
                if (hasCredential) credentialStamp = service.Settings.Stamp(target);
            }
            catch (Exception failure)
            {
                Notify(title, RegistryText.F("common.targetUrl", item.Url) + "\n\n" + CredentialMessages.Describe(failure));
                return;
            }
            // An entry without a URL names no target URL.
            var targetText = string.IsNullOrWhiteSpace(item.Url) ? "" : RegistryText.F("common.targetUrl", item.Url) +
                (target != item.Url && hasCredential ? "\n" + RegistryText.F("deleteRegistry.credentialUrl", target) : "") + "\n\n";
            if (!Confirm(RegistryText.T("deleteRegistry.confirm.title"), targetText +
                    RegistryText.F(hasCredential ? "deleteRegistry.confirm.withCredential" : "deleteRegistry.confirm.withoutCredential", DisplayName(item))))
                return;

            // The dialog may have outlived this row or the project state it was checked against.
            state.RefreshProject();
            error = RegistryDeleteBlock(state, item);
            if (error != null)
            {
                Notify(title, error);
                Render(root, displayName, state);
                return;
            }
            var stage = 0; // 0 catalog only, 1 credential re-check, 2 credential deletion, 3 catalog replace after deletion
            Action deleteCredential = () =>
            {
                // An entry without a URL has no credential to check or delete.
                if (target == null) return;
                stage = 1;
                // Credentials that appeared or changed spelling during the dialog are neither missed nor guessed.
                var urls = service.Settings.Summaries().Select(summary => summary.Url).ToList();
                var now = State.ResolveCredential(urls, item.Url, out var unclear);
                if (unclear || urls.Contains(now) != hasCredential || (hasCredential && now != target)) throw new RegistryException("settings_conflict");
                // Nothing to delete: a failed replace is then a plain catalog failure.
                if (!hasCredential) { stage = 0; return; }
                stage = 2;
                service.Delete(target, credentialStamp);
                stage = 3;
            };
            RegistryCatalog.Snapshot saved;
            try { saved = RegistryCatalog.Save(catalogStamp, Entries(state.Registries.Where(entry => entry != item)), deleteCredential); }
            catch (Exception failure)
            {
                // Delete may fail after its journal is written; only a matching fresh stamp proves nothing was applied.
                var untouched = stage < 2 || IsRejectedBeforeWrite(failure);
                if (stage == 2 && !untouched)
                    try { untouched = service.Settings.Stamp(target) == credentialStamp; } catch (Exception) { }
                Notify(title, RegistryText.F("common.targetUrl", item.Url) + "\n\n" + DeleteFailure(stage, untouched, failure));
                state.Reload();
                state.ReloadCredentials();
                Render(root, displayName, state);
                return;
            }
            state.Registries.Remove(item);
            state.Stamp = saved.Stamp;
            state.WriteTimeUtc = saved.WriteTimeUtc;
            state.Select(state.Registries.FirstOrDefault());
            state.ReloadCredentials();
            Render(root, displayName, state);
        }

        // Codes the service raises only before it writes anything.
        private static bool IsRejectedBeforeWrite(Exception failure) => failure is RegistryException known &&
            (known.Code == "settings_conflict" || known.Code == "settings_busy" || known.Code == "registry_url_alias");

        private static string DeleteFailure(int stage, bool untouched, Exception failure)
        {
            var kept = "\n\n" + RegistryText.T("deleteRegistry.credentialKept");
            var code = (failure as InvalidOperationException)?.Message;
            if (stage == 0)
                return RegistryText.T(code == "catalog_conflict" ? "deleteRegistry.catalogConflict" :
                    code == "catalog_busy" ? "catalog.busy" : "catalog.cannotSaveAccess") + kept;
            if (stage == 3)
                return RegistryText.T("deleteRegistry.catalogAfterCredential");
            return RegistryText.T("deleteRegistry.credentialFailed") + "\n\n" + CredentialMessages.Describe(failure) +
                (untouched ? kept : "\n\n" + RegistryText.T("deleteRegistry.credentialMaybeChanged"));
        }

        // Add to Project is where the entry must be complete: missing fields are listed together, then the identity is
        // checked and the entry saved (a draft promoted first) before the manifest is touched. Any rejection leaves the
        // manifest, Package Manager and credentials alone.
        private static void AddToProject(VisualElement root, string displayName, State state, Registry item, Scope scope)
        {
            var title = RegistryText.T("project.addFailed.title");
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(item.Name)) missing.Add(RegistryText.T("project.missing.name"));
            if (string.IsNullOrWhiteSpace(item.Url)) missing.Add(RegistryText.T("project.missing.url"));
            if (string.IsNullOrWhiteSpace(scope.Value)) missing.Add(RegistryText.T("project.missing.scope"));
            if (missing.Count > 0)
            {
                Notify(title, RegistryText.T("project.missing") + "\n\n" + string.Join("\n", missing));
                return;
            }
            if (!item.Scopes.Contains(scope) || (!item.Transient && !state.Registries.Contains(item)))
            {
                Notify(title, RegistryText.T("block.registryStale"));
                return;
            }
            // The save runs the catalog stamp check, so a stale or unsaved catalog stops here.
            if (!ValidateRegistryIdentity(item.Name, item.Url, state.Registries, item, out var error) ||
                !(item.Transient ? TryPromote(state, item, out error) : PersistCatalog(state, out error)))
            {
                Notify(title, error);
                if (root.panel != null) Render(root, displayName, state);
                return;
            }
            ProjectRegistryEdit.Apply(item.Name, item.Url, scope.Value, true, out error);
            if (!string.IsNullOrEmpty(error)) Notify(RegistryText.T("project.title"), error);
            if (root.panel != null) Render(root, displayName, state);
        }

        // Credentials are keyed by the URL at click time; editing the catalog URL never moves or deletes them.
        private static void BindCredentialActions(VisualElement root, string displayName, State state, Registry item)
        {
            // A partial entry without a URL has no credential key, so both actions stay disabled.
            var usable = state.CredentialUrlOf(item, out var ambiguous) != null;
            var setupAuth = root.Q<Button>("setup-auth");
            setupAuth.SetEnabled(state.CredentialError == null && usable);
            var test = root.Q<Button>("test-connection");
            Action tips = () =>
            {
                setupAuth.tooltip = state.CredentialError != null ? CredentialMessages.Describe(state.CredentialError)
                    : ambiguous ? AmbiguousTip : !usable ? RegistryText.T("tip.urlRequired") : RegistryText.T("tip.setupAuth");
                test.tooltip = ambiguous ? AmbiguousTip : !usable ? RegistryText.T("tip.urlRequired") : RegistryText.T("tip.testConnection");
            };
            tips();
            state.Relabel += tips;
            setupAuth.clicked += () =>
            {
                var target = state.CredentialUrlOf(item, out var unclear);
                if (unclear || target == null) return;
                RegistryAuthWindow.Open(target, () =>
                {
                    if (root.panel == null) return;
                    state.ReloadCredentials();
                    Render(root, displayName, state);
                });
            };

            test.name = "test-connection-" + item.Id;
            test.SetEnabled(!state.CheckRunning && usable);
            test.clicked += () =>
            {
                var target = state.CredentialUrlOf(item, out var unclear);
                if (!unclear && target != null) CheckConnection(root, state, item, target);
            };
        }

        // Replaceable by tests; defaults use ordered confirmations and native notices.
        internal static Func<string, string, bool> Confirm = RegistryDialogs.Confirm;
        internal static Action<string, string> Notify = (title, message) => EditorUtility.DisplayDialog(title, message, RegistryText.T("button.close"));

        // Any explicit HTTP check may carry whatever credential the TOML holds at send time, so always warn.
        internal static bool NeedsHttpWarning(string url) => url != null && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

        // url is the credential key resolved at click time; later URL edits or selection changes do not retarget this check.
        private static async void CheckConnection(VisualElement root, State state, Registry item, string url)
        {
            if (state.CheckRunning) return;
            var scope = item.Scopes.Select(value => value.Value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";
            if (NeedsHttpWarning(url) && !Confirm(RegistryText.T("http.confirm.title"),
                    RegistryText.F("common.targetUrl", url) + "\n\n" + RegistryText.T("http.confirm.check")))
                return;
            state.CheckRunning = true;
            SetCheckButtons(root, false);
            var lifetime = state.Lifetime;
            string message;
            bool cancelled;
            try
            {
                var result = await CredentialServices.Create().CheckConnection(url, scope, lifetime.Token);
                message = CredentialMessages.Describe(result);
            }
            catch (Exception error)
            {
                message = RegistryText.F("common.targetUrl", url) + "\n\n" + CredentialMessages.Describe(error);
            }
            finally
            {
                // A cancelled lifetime belongs to a deactivated page: CancelChecks already reset CheckRunning,
                // and this check is its last user, so dispose it here and stay silent.
                cancelled = lifetime.IsCancellationRequested;
                if (cancelled) lifetime.Dispose();
                else state.CheckRunning = false;
            }
            if (cancelled || root.panel == null) return;
            SetCheckButtons(root, true);
            Notify(RegistryText.T("check.title"), message);
        }

        // The page may have been re-rendered during the check, so look the button up again.
        private static void SetCheckButtons(VisualElement root, bool enabled) =>
            root.Query<Button>().Where(button => button.name.StartsWith("test-connection", StringComparison.Ordinal)).ForEach(button => button.SetEnabled(enabled));

        private static void BindScopeList(VisualElement root, string displayName, State state, ListView list, Registry item, VisualTreeAsset rowTemplate)
        {
            // Every scope edit is saved; a draft's first one promotes it into the catalog.
            Func<bool> save = () => SaveEdit(root, displayName, state, item);
            var remove = list.Q<Button>(BaseListView.footerRemoveButtonName);
            remove.name = "delete-scope";
            Action updateRemove = () =>
            {
                var selected = list.selectedItem as Scope;
                remove.SetEnabled(selected != null && !selected.InProject && !selected.Blocked);
                remove.tooltip = selected == null ? RegistryText.T("scope.tip.selectToDelete") :
                    selected.InProject ? RegistryText.T("scope.tip.removeFromProjectFirst") :
                    selected.ReferenceCount > 0 ? RegistryText.T("block.referenced") :
                    selected.Blocked ? UnresolvedTip :
                    RegistryText.T("scope.tip.delete");
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
                    projectAction.text = RegistryText.T(scope.InProject ? "scope.removeFromProject" : "scope.addToProject");
                    // Adding stays clickable with missing fields; the click lists what is missing.
                    projectAction.SetEnabled(state.ManifestError == null && (!scope.InProject || !scope.Blocked));
                    projectAction.tooltip = state.ManifestError ?? (scope.InProject && scope.ReferenceCount > 0
                        ? RegistryText.T("project.referenced")
                        : scope.InProject && scope.Blocked ? UnresolvedTip
                        : RegistryText.T(scope.InProject ? "scope.tip.remove" : "scope.tip.add"));
                };
                if (field.userData is EventCallback<ChangeEvent<string>> previous)
                    field.UnregisterValueChangedCallback(previous);
                field.SetValueWithoutNotify(scope.Value);
                field.SetEnabled(item.Transient || (state.ManifestError == null && !scope.InProject));
                EventCallback<ChangeEvent<string>> changed = change =>
                {
                    if (!IsOwnChange(field, change) || change.newValue == scope.Value || scope.InProject) return;
                    scope.Value = change.newValue;
                    if (!save()) return;
                    updateAction();
                };
                field.userData = changed;
                field.RegisterValueChangedCallback(changed);
                // Replaced on every bind, so a recycled row always relabels for the scope it currently shows.
                projectAction.userData = updateAction;
                updateAction();
                projectAction.clickable = new Clickable(() =>
                {
                    if (!scope.InProject)
                    {
                        AddToProject(root, displayName, state, item, scope);
                        return;
                    }
                    if (item.Transient) return;
                    ProjectRegistryEdit.Apply(item.Name, item.Url, scope.Value, false, out var error);
                    if (!string.IsNullOrEmpty(error)) Notify(RegistryText.T("project.title"), error);
                    if (root.panel != null) Render(root, displayName, state);
                });
            };

            var add = list.Q<Button>(BaseListView.footerAddButtonName);
            add.name = "add-scope";
            add.clickable = new Clickable(() =>
            {
                item.Scopes.Add(new Scope(string.Empty, false, 0));
                if (!save()) return;
                list.Rebuild();
                list.selectedIndex = item.Scopes.Count - 1;
                updateRemove();
            });

            updateRemove();
            state.Relabel += updateRemove;
            // Only the rows currently shown; their fields, selection and caret are left alone. Unbound rows relabel when bound.
            state.Relabel += () => list.Query<Button>("scope-project-action").ForEach(button => (button.userData as Action)?.Invoke());
            list.selectionChanged += _ => updateRemove();
            list.itemIndexChanged += (_, __) =>
            {
                updateRemove();
                save();
            };
            remove.clickable = new Clickable(() =>
            {
                var selected = list.selectedItem as Scope;
                if (selected == null || selected.InProject || selected.Blocked) return;
                var index = item.Scopes.IndexOf(selected);
                item.Scopes.Remove(selected);
                if (!save()) return;
                list.Rebuild();
                list.selectedIndex = Math.Min(index, item.Scopes.Count - 1);
                updateRemove();
            });
        }

        private static void Watch(VisualElement root, string displayName, State state)
        {
            state.Unwatch?.Invoke();
            EditorApplication.CallbackFunction tick = null;
            double next = 0;
            tick = () =>
            {
                if (root.panel == null) { EditorApplication.update -= tick; return; }
                if (EditorApplication.timeSinceStartup < next) return;
                next = EditorApplication.timeSinceStartup + 0.5;
                if (root.focusController?.focusedElement is TextField) return;
                var catalogChanged = state.Stamp != null && RegistryCatalog.ChangedSince(state.WriteTimeUtc);
                var projectChanged = ProjectRegistryEdit.ChangedSince(state.ManifestTime, state.LockTime);
                var credentialsChanged = state.CredentialsChanged();
                if (!catalogChanged && !projectChanged && !credentialsChanged) return;
                if (catalogChanged) state.Reload();
                if (credentialsChanged) state.ReloadCredentials();
                Render(root, displayName, state);
            };
            EditorApplication.update += tick;
            // Stop in-flight connection checks before reload/quit; the page itself is rebuilt afterwards.
            AssemblyReloadEvents.AssemblyReloadCallback beforeReload = state.CancelChecks;
            AssemblyReloadEvents.beforeAssemblyReload += beforeReload;
            EditorApplication.quitting += state.CancelChecks;
            // Relabels the current page in place: no re-render, reload or credential refresh.
            Action<string> relabel = _ => state.Relabel?.Invoke();
            RegistryText.Changed += relabel;
            state.Unwatch = () =>
            {
                EditorApplication.update -= tick;
                AssemblyReloadEvents.beforeAssemblyReload -= beforeReload;
                EditorApplication.quitting -= state.CancelChecks;
                RegistryText.Changed -= relabel;
            };
        }

        private static bool Finish(VisualElement root, string displayName, State state)
        {
            if (PersistCatalog(state, out var error)) return true;
            Notify(RegistryText.T("catalog.title"), error);
            // After the event that saved: the reloaded catalog replaces the rows bound on this page.
            RenderLater(root, displayName, state);
            return false;
        }

        private static bool PersistCatalog(State state, out string error)
        {
            if (state.Stamp == null)
            {
                error = RegistryText.T("catalog.cannotSave");
                return false;
            }
            try
            {
                var saved = RegistryCatalog.Save(state.Stamp, Entries(state.Registries));
                state.Stamp = saved.Stamp;
                state.WriteTimeUtc = saved.WriteTimeUtc;
                error = null;
                return true;
            }
            catch (InvalidOperationException failure) when (failure.Message == "catalog_conflict")
            {
                state.Reload();
                error = RegistryText.T("catalog.conflict");
                return false;
            }
            catch (InvalidOperationException failure) when (failure.Message == "catalog_busy")
            {
                state.Reload();
                error = RegistryText.T("catalog.busy");
                return false;
            }
            catch (Exception)
            {
                try { state.Reload(); } catch (Exception) { state.Stamp = null; }
                error = RegistryText.T("catalog.cannotSaveAccess");
                return false;
            }
        }

        private static RegistrySettingsJson.Entry[] Entries(IEnumerable<Registry> registries) => registries.Select(item => new RegistrySettingsJson.Entry
        {
            name = item.Name,
            url = item.Url,
            scopes = item.Scopes.Select(scope => scope.Value).ToArray()
        }).ToArray();

        // A name only or a URL only is enough; the dialog's prefilled scheme alone counts as no URL.
        private static bool TryAddRegistry(State state, string name, string url, out string error)
        {
            var typedUrl = (url ?? "").Trim();
            if (string.Equals(typedUrl, "https://", StringComparison.OrdinalIgnoreCase) || string.Equals(typedUrl, "http://", StringComparison.OrdinalIgnoreCase))
                url = "";
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(url)) { error = RegistryText.T("add.empty"); return false; }
            if (!ValidateCatalogIdentity(name, url, state.Registries, null, out error)) return false;
            var added = new Registry(state.NextId(), (name ?? "").Trim(), NormalizeUrl(url), new List<string>(), false, 0);
            state.Registries.Add(added);
            state.SelectedRegistryId = added.Id;
            return true;
        }

        private static bool TryUpdateRegistryIdentity(State state, Registry item, string name, string url, out string error)
        {
            if (!ValidateCatalogIdentity(name, url, state.Registries, item, out error)) return false;
            item.Name = (name ?? "").Trim();
            item.Url = NormalizeUrl(url);
            return true;
        }

        // Catalog storage: any field may be blank. A non-empty URL must be valid, and non-empty names and URLs unique;
        // blank ones never conflict with each other.
        private static bool ValidateCatalogIdentity(string name, string url, IEnumerable<Registry> items, Registry editing, out string error)
        {
            var trimmedName = (name ?? "").Trim();
            var normalizedUrl = NormalizeUrl(url);
            error = null;
            if (normalizedUrl.Length > 0 && !ValidateUrl(normalizedUrl, out error)) return false;
            if (trimmedName.Length > 0 && items.Any(item => item != editing && string.Equals(item.Name, trimmedName, StringComparison.OrdinalIgnoreCase)))
            { error = RegistryText.T("registry.duplicateName"); return false; }
            if (normalizedUrl.Length > 0 && items.Any(item => item != editing && string.Equals(item.Url, normalizedUrl, StringComparison.OrdinalIgnoreCase)))
            { error = RegistryText.T("registry.duplicateUrl"); return false; }
            return true;
        }

        // Adding to the project also needs a name and a valid URL.
        private static bool ValidateRegistryIdentity(string name, string url, IEnumerable<Registry> items, Registry editing, out string error)
        {
            if (string.IsNullOrWhiteSpace(name)) { error = RegistryText.T("registry.nameRequired"); return false; }
            if (!ValidateUrl(url, out error)) return false;
            return ValidateCatalogIdentity(name, url, items, editing, out error);
        }

        private static bool ValidateUrl(string value, out string error)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                error = RegistryText.T("registry.invalidUrl");
                return false;
            }
            error = null;
            return true;
        }

        private static string NormalizeUrl(string value) => (value ?? "").Trim().TrimEnd('/');
        private sealed class State
        {
            private int _nextId;
            internal List<Registry> Registries { get; private set; }
            internal int? SelectedRegistryId { get; set; }
            internal string Stamp;
            internal DateTime WriteTimeUtc;
            // Cached errors keep their keys and are translated when shown.
            internal string LoadErrorKey;
            internal string ManifestErrorKey;
            internal string LoadError => LoadErrorKey == null ? null : RegistryText.T(LoadErrorKey);
            internal string ManifestError => ManifestErrorKey == null ? null : RegistryText.T(ManifestErrorKey);
            internal DateTime ManifestTime, LockTime;
            internal Action Unwatch;
            // Reapplies the rendered page's texts after a language change; reset by every Render.
            internal Action Relabel;
            internal Dictionary<string, CredentialSummary> Credentials = new Dictionary<string, CredentialSummary>(StringComparer.Ordinal);
            internal string CredentialError;
            internal string CredentialMarker;
            internal bool CheckRunning;
            internal System.Threading.CancellationTokenSource Lifetime = new System.Threading.CancellationTokenSource();
            // Catalog entries only: export, deletion and saving never see transient rows.
            internal Registry SelectedRegistry => Registries.FirstOrDefault(x => x.Id == SelectedRegistryId);
            // Configured credential URLs missing from the catalog; never part of Registries or the saved catalog.
            internal List<Registry> Orphans = new List<Registry>();
            internal List<Registry> Items = new List<Registry>();
            // Drafts by canonical URL so input survives re-rendering and credential reloads.
            internal readonly Dictionary<string, Registry> Drafts = new Dictionary<string, Registry>(StringComparer.Ordinal);
            internal string SelectedOrphanKey;
            private int _nextDraftId = -1;
            internal Registry SelectedItem => SelectedRegistry ?? Orphans.FirstOrDefault(x => x.DraftKey == SelectedOrphanKey);

            internal void Select(Registry item)
            {
                SelectedRegistryId = item != null && !item.Transient ? item.Id : (int?)null;
                SelectedOrphanKey = item != null && item.Transient ? item.DraftKey : null;
            }

            internal void Reorder(List<Registry> order) => Registries = order;

            internal static string CredentialKey(string url)
            {
                if (string.IsNullOrWhiteSpace(url)) return null;
                try { return UrlRules.Key(url); }
                catch (RegistryException) { return null; }
            }

            // URLs that fail the registry URL rules (e.g. with user information or a query) are never shown.
            internal void RefreshOrphans()
            {
                var catalog = new HashSet<string>(Registries.Select(item => CredentialKey(item.Url)).Where(key => key != null), StringComparer.Ordinal);
                Orphans = new List<Registry>();
                foreach (var group in Credentials.Values.Where(summary => summary.Configured)
                    .Select(summary => new { summary.Url, Key = CredentialKey(summary.Url) })
                    .Where(entry => entry.Key != null && !catalog.Contains(entry.Key))
                    .GroupBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    var urls = group.Select(entry => entry.Url).OrderBy(value => value, StringComparer.Ordinal).ToList();
                    if (!Drafts.TryGetValue(group.Key, out var draft))
                    {
                        draft = new Registry(_nextDraftId--, "", urls[0], new List<string>(), false, 0) { Transient = true, DraftKey = group.Key };
                        Drafts[group.Key] = draft;
                    }
                    draft.Url = urls[0];
                    // Several spellings of one registry: no credential is picked on the user's behalf.
                    draft.CredentialUrl = urls.Count == 1 ? urls[0] : null;
                    Orphans.Add(draft);
                }
                foreach (var stale in Drafts.Keys.Where(key => Orphans.All(item => item.DraftKey != key)).ToList())
                    Drafts.Remove(stale);
                Items = Registries.Concat(Orphans).ToList();
            }

            // The exact credential URL first, then a unique equivalent spelling; ambiguous when several would match.
            internal string CredentialUrlOf(Registry item, out bool ambiguous)
            {
                if (item.Transient)
                {
                    ambiguous = item.CredentialUrl == null;
                    return item.CredentialUrl;
                }
                // A partial entry without a URL has no credential.
                if (string.IsNullOrWhiteSpace(item.Url)) { ambiguous = false; return null; }
                return ResolveCredential(Credentials.Keys, item.Url, out ambiguous);
            }

            // Returns url itself when no credential URL matches.
            internal static string ResolveCredential(ICollection<string> credentialUrls, string url, out bool ambiguous)
            {
                ambiguous = false;
                if (credentialUrls.Contains(url)) return url;
                var key = CredentialKey(url);
                var matches = key == null ? new List<string>() : credentialUrls.Where(candidate => CredentialKey(candidate) == key).ToList();
                if (matches.Count > 1) { ambiguous = true; return null; }
                return matches.Count == 1 ? matches[0] : url;
            }

            // Summaries only: secrets are not kept on the page.
            internal void ReloadCredentials()
            {
                try
                {
                    var service = CredentialServices.Create();
                    CredentialMarker = CredentialServices.ChangeMarker(service);
                    Credentials = service.Settings.Summaries().ToDictionary(item => item.Url, StringComparer.Ordinal);
                    CredentialError = null;
                }
                catch (Exception error)
                {
                    Credentials = new Dictionary<string, CredentialSummary>(StringComparer.Ordinal);
                    CredentialError = Data.SafeError(error);
                }
            }

            internal bool CredentialsChanged()
            {
                try { return CredentialServices.ChangeMarker(CredentialServices.Create()) != CredentialMarker; }
                catch (Exception) { return false; }
            }

            // A running check keeps the old source and disposes it when it finishes; otherwise dispose now.
            internal void CancelChecks()
            {
                var old = Lifetime;
                Lifetime = new System.Threading.CancellationTokenSource();
                old.Cancel();
                if (!CheckRunning) old.Dispose();
                CheckRunning = false;
            }

            private State()
            {
                Registries = new List<Registry>();
                Stamp = RegistryCatalog.Missing;
            }

            internal static State CreateShell() => new State();
            internal static State Create() { var state = new State(); state.Reload(); return state; }

            internal void Reload()
            {
                var selected = SelectedRegistry;
                var selectedIndex = selected == null ? -1 : Registries.IndexOf(selected);
                try
                {
                    var snap = RegistryCatalog.Load();
                    _nextId = 1;
                    Registries = snap.Entries.Select(entry => new Registry(_nextId++, entry.name, entry.url, new List<string>(entry.scopes), false, 0)).ToList();
                    Stamp = snap.Stamp;
                    WriteTimeUtc = snap.WriteTimeUtc;
                    LoadErrorKey = null;
                    // Partial entries may lack a URL or a name, so the URL, then the name, then the position finds the row again.
                    var match = selected == null ? null :
                        Registries.FirstOrDefault(item => item.Url.Length > 0 && string.Equals(item.Url, selected.Url, StringComparison.OrdinalIgnoreCase)) ??
                        Registries.FirstOrDefault(item => item.Name.Length > 0 && string.Equals(item.Name, selected.Name, StringComparison.OrdinalIgnoreCase)) ??
                        (selectedIndex >= 0 && selectedIndex < Registries.Count ? Registries[selectedIndex] : null);
                    SelectedRegistryId = match?.Id ?? (SelectedOrphanKey == null ? Registries.FirstOrDefault()?.Id : null);
                }
                catch (Exception)
                {
                    LoadErrorKey = "catalog.loadError";
                    Stamp = null;
                    WriteTimeUtc = RegistryCatalog.CurrentWriteTime();
                    if (Registries == null) Registries = new List<Registry>();
                }
            }

            internal int NextId() => _nextId++;

            internal void RefreshProject()
            {
                var view = ProjectRegistryEdit.Observe();
                ManifestTime = view.ManifestTime;
                LockTime = view.LockTime;
                ManifestErrorKey = view.ErrorKey;
                if (!view.Ok || Registries == null) return;
                foreach (var registry in Registries)
                    foreach (var scope in registry.Scopes)
                    {
                        var value = scope.Value?.Trim();
                        // An entry without a URL cannot be in the project.
                        var listed = !string.IsNullOrWhiteSpace(registry.Url);
                        scope.InProject = listed && view.Contains(registry.Url, value);
                        scope.ReferenceCount = listed ? view.ReferencesOf(registry.Url, value) : 0;
                        scope.Unresolved = listed && view.UnresolvedOf(value);
                    }
            }
        }

        private sealed class Scope
        {
            internal Scope(string value, bool inProject, int referenceCount)
            { Value = value; InProject = inProject; ReferenceCount = referenceCount; }
            internal string Value { get; set; }
            internal bool InProject { get; set; }
            internal int ReferenceCount { get; set; }
            internal bool Unresolved { get; set; }
            // Same predicate as the final project-removal guard: installed from this registry, or origin not yet known.
            internal bool Blocked => ReferenceCount > 0 || Unresolved;
        }

        private sealed class Registry
        {
            internal Registry(int id, string name, string url, List<string> scopes, bool inProject, int referenceCount)
            { Id = id; Name = name ?? ""; Url = url ?? ""; Scopes = scopes.Select((value, index) => new Scope(value, inProject, index == 0 ? referenceCount : 0)).ToList(); }
            // Reassigned once when a draft is promoted into the catalog.
            internal int Id { get; set; }
            internal string Name { get; set; }
            internal string Url { get; set; }
            internal List<Scope> Scopes { get; set; }
            internal bool InProject => Scopes.Any(scope => scope.InProject);
            internal int ReferenceCount => Scopes.Sum(scope => scope.ReferenceCount);
            internal bool Blocked => Scopes.Any(scope => scope.Blocked);
            // Transient rows mirror a configured credential URL that is not in the catalog.
            internal bool Transient { get; set; }
            internal string DraftKey { get; set; }
            internal string CredentialUrl { get; set; }
        }
    }
}
