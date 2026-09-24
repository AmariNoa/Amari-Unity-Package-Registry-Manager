using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal sealed class RegistryAuthWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.amari-noa.amari-unity-package-registry-manager/Editor/UI/RegistryAuthWindow.uxml";
        private static readonly string[] ModeNames = { "auth-mode-pat", "auth-mode-npm", "auth-mode-basic", "auth-mode-oauth", "auth-mode-none" };
        private static readonly string[] SummaryNames = { "auth-url", "auth-current-kind", "auth-current-account", "auth-current-expiry", "auth-config-path" };
        internal enum Mode { Unselected = -1, Pat, Npm, Basic, OAuth, None }

        // Replaceable by tests; defaults use ordered confirmations, native notices and the browser.
        internal static Func<string, string, bool> Confirm = RegistryDialogs.Confirm;
        internal static Action<string, string> Notify = (title, message) => EditorUtility.DisplayDialog(title, message, RegistryText.T("button.close"));
        internal static Action<string> Browser = Application.OpenURL;
        internal static Func<RegistryService, string, string, Action<string>, CancellationToken, Task> LoginRunner = (service, url, proxy, browser, cancel) => service.Login(url, proxy, browser, cancel);

        private string _url;
        private Action _changed;
        private RegistryService _service;
        private string _stamp;
        private string _loadError;
        private CredentialSummary _summary;
        private string _storedProxy;
        private CancellationTokenSource _operation;
        private bool _busy, _closed, _bound;
        private RadioButton[] _modes;
        private TextField _token, _username, _password, _proxy;
        private Button _apply, _relogin;
        private Action _relabel;

        internal bool Busy => _busy;

        internal static RegistryAuthWindow Open(string url, Action changed)
        {
            var window = Create(url, changed);
            window.titleContent = new GUIContent(RegistryText.T("auth.title"));
            window.minSize = new Vector2(440, 340);
            window.ShowUtility();
            return window;
        }

        // Captures the credential stamp at opening; Save/Delete are rejected if anything changed since.
        internal static RegistryAuthWindow Create(string url, Action changed)
        {
            var window = CreateInstance<RegistryAuthWindow>();
            window._url = url;
            window._changed = changed;
            try
            {
                window._service = CredentialServices.Create();
                window._stamp = window._service.Settings.Stamp(url);
                window.ReadSummary();
            }
            catch (Exception error)
            {
                window._stamp = null;
                window._loadError = Data.SafeError(error);
            }
            return window;
        }

        public void CreateGUI()
        {
            if (_bound) return;
            if (_url == null || (_service == null && _loadError == null))
            {
                // Recreated after a domain reload without its target: nothing safe to show.
                EditorApplication.delayCall += Close;
                return;
            }
            var template = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (template == null) throw new InvalidOperationException("Registry auth window UI asset is missing.");
            var root = rootVisualElement;
            template.CloneTree(root);
            _bound = true;
            _relabel = RegistryText.Localize(root);
            RegistryText.Changed += Relabel;
            _modes = ModeNames.Select(name => root.Q<RadioButton>(name)).ToArray();
            _token = root.Q<TextField>("auth-token");
            _username = root.Q<TextField>("auth-username");
            _password = root.Q<TextField>("auth-password");
            _proxy = root.Q<TextField>("auth-proxy");
            _apply = root.Q<Button>("auth-apply");
            _relogin = root.Q<Button>("auth-relogin");
            foreach (var mode in _modes) mode.RegisterValueChangedCallback(_ => UpdateState());
            foreach (var field in new[] { _token, _username, _password, _proxy }) field.RegisterValueChangedCallback(_ => UpdateState());
            _apply.clicked += Apply;
            _relogin.clicked += Relogin;
            root.Q<Button>("auth-cancel").clicked += Close;
            foreach (var summaryName in SummaryNames) root.Q<TextField>(summaryName).SetEnabled(false);
            root.Q<TextField>("auth-url").SetValueWithoutNotify(_url);
            // Filled once: a stored proxy is shown verbatim, and text the user clears is not restored.
            _proxy.SetValueWithoutNotify(string.IsNullOrEmpty(_storedProxy) ? DefaultProxy(_url) : _storedProxy);
            SelectMode(InitialMode());
            ShowSummary();
        }

        // Labels and derived texts only: entered values, the selected mode and a running sign-in are kept.
        private void Relabel(string _)
        {
            if (!_bound || _closed) return;
            _relabel();
            titleContent = new GUIContent(RegistryText.T("auth.title"));
            ShowSummary();
            UpdateState();
        }

        internal void SelectMode(Mode mode)
        {
            if (!_bound) return;
            for (var index = 0; index < _modes.Length; index++) _modes[index].SetValueWithoutNotify(index == (int)mode);
            UpdateState();
        }

        private Mode SelectedMode()
        {
            for (var index = 0; index < _modes.Length; index++) if (_modes[index].value) return (Mode)index;
            return Mode.Unselected;
        }

        private Mode InitialMode()
        {
            if (_summary == null) return Mode.None;
            switch (_summary.Kind)
            {
                case "Pat": return Mode.Pat;
                case "Npm": return Mode.Npm;
                case "Basic": return Mode.Basic;
                case "OAuth": return Mode.OAuth;
                default: return _summary.Configured ? Mode.Pat : Mode.None;
            }
        }

        private void ReadSummary()
        {
            _summary = _service.Settings.Summaries().FirstOrDefault(item => item.Url == _url);
            _storedProxy = _service.Settings.Read().Records.FirstOrDefault(item => item.Url == _url)?.Proxy;
        }

        private void ShowSummary()
        {
            if (!_bound || _closed) return;
            var root = rootVisualElement;
            var relogin = _summary != null && (_summary.NeedsLogin || _summary.InFlight) ? RegistryText.T("auth.reloginNeeded") : "";
            root.Q<TextField>("auth-current-kind").SetValueWithoutNotify(_summary == null ? (_loadError == null ? RegistryText.T("auth.kind.none") : "") : CredentialMessages.KindLabel(_summary) + relogin);
            root.Q<TextField>("auth-current-account").SetValueWithoutNotify(_summary?.Account ?? "");
            root.Q<TextField>("auth-current-expiry").SetValueWithoutNotify(_summary != null && _summary.ExpiresAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(_summary.ExpiresAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "");
            root.Q<TextField>("auth-config-path").SetValueWithoutNotify(_summary?.ConfigurationPath ?? _service?.Settings.Paths.ConfigurationFile ?? "");
        }

        private void UpdateState()
        {
            if (!_bound || _closed) return;
            var mode = SelectedMode();
            var ready = _loadError == null && !_busy;
            foreach (var item in _modes) item.SetEnabled(ready);
            _token.SetEnabled(ready && (mode == Mode.Pat || mode == Mode.Npm));
            _username.SetEnabled(ready && mode == Mode.Basic);
            _password.SetEnabled(ready && mode == Mode.Basic);
            _proxy.SetEnabled(ready && mode == Mode.OAuth);
            var refresh = mode == Mode.OAuth && RefreshesStoredLogin();
            _apply.text = RegistryText.T(mode == Mode.OAuth ? (refresh ? "auth.apply.refresh" : "auth.apply.login") : mode == Mode.None ? "auth.apply.delete" : "auth.apply.save");
            _apply.SetEnabled(ready && (refresh || CanApply(mode)));
            _apply.tooltip = _loadError != null ? CredentialMessages.Describe(_loadError) : "";
            _relogin.SetEnabled(ready && mode == Mode.OAuth && HasOAuth() && !string.IsNullOrWhiteSpace(_proxy.value));
        }

        private bool HasOAuth() => _summary != null && _summary.Kind == "OAuth";

        // Refresh always goes to the stored proxy, so it is offered only while the field still names that proxy
        // and the record can refresh here; any other proxy text, or a record needing login, goes through the browser.
        private bool RefreshesStoredLogin() => HasOAuth() && !_summary.NeedsLogin && !_summary.InFlight &&
            !string.IsNullOrEmpty(_storedProxy) && SameProxy(_proxy.value, _storedProxy) &&
            string.Equals(_summary.ConfigurationPath, _service?.Settings.Paths.ConfigurationFile, StringComparison.OrdinalIgnoreCase);

        // Scheme/host compare case-insensitively and a trailing slash is ignored; the path is case-sensitive.
        internal static bool SameProxy(string left, string right)
        {
            try { return UrlRules.Key((left ?? "").Trim()) == UrlRules.Key((right ?? "").Trim()); }
            catch (RegistryException) { return false; }
        }

        // https://<registry host> only: the registry's port, path and user information are not carried over.
        internal static string DefaultProxy(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return "";
            return new UriBuilder(Uri.UriSchemeHttps, uri.Host).Uri.GetLeftPart(UriPartial.Authority);
        }

        private bool CanApply(Mode mode)
        {
            switch (mode)
            {
                case Mode.Pat:
                case Mode.Npm: return !string.IsNullOrWhiteSpace(_token.value);
                case Mode.Basic: return !string.IsNullOrEmpty(_username.value) && !string.IsNullOrEmpty(_password.value);
                case Mode.OAuth: return !string.IsNullOrWhiteSpace(_proxy.value);
                case Mode.None: return _summary != null && (_summary.Configured || !_summary.External);
                default: return false;
            }
        }

        internal void Apply()
        {
            if (!_bound || _busy || _closed || _loadError != null) return;
            var mode = SelectedMode();
            try
            {
                switch (mode)
                {
                    case Mode.Pat:
                    case Mode.Npm:
                        // An empty entry is an error, never a deletion.
                        if (string.IsNullOrWhiteSpace(_token.value)) throw new RegistryException("token_required");
                        if (!ConfirmShared(RegistryText.T("auth.op.saveToken"), true)) return;
                        _service.SaveToken(_url, _token.value.Trim(), mode == Mode.Pat ? CredentialKind.Pat : CredentialKind.Npm, _stamp);
                        break;
                    case Mode.Basic:
                        if (!ConfirmShared(RegistryText.T("auth.op.saveBasic"), true)) return;
                        _service.SaveBasic(_url, _username.value, _password.value, _stamp);
                        break;
                    case Mode.OAuth:
                        if (RefreshesStoredLogin()) RefreshToken();
                        else StartLogin();
                        return;
                    case Mode.None:
                        if (!ConfirmShared(RegistryText.T("auth.op.delete"), false)) return;
                        _service.Delete(_url, _stamp);
                        break;
                    default:
                        return;
                }
            }
            catch (Exception error)
            {
                ClearSecrets();
                Say(CredentialMessages.Describe(error));
                UpdateState();
                return;
            }
            ClearSecrets();
            Completed(RegistryText.T("auth.done.saved"), true);
        }

        private void StartLogin()
        {
            var destination = _proxy.value.Trim();
            if (!ConfirmShared(RegistryText.F("auth.op.login", destination), true)) return;
            if (_service.Settings.Stamp(_url) != _stamp) throw new RegistryException("settings_conflict");
            Run(cancel => LoginRunner(_service, _url, destination, Browser, cancel), "auth.done.login", true, false);
        }

        internal void RefreshToken()
        {
            if (!_bound || _busy || _closed || _loadError != null || !HasOAuth()) return;
            try
            {
                // Same guard as saves: a stale window must not refresh an account another operation replaced.
                if (_service.Settings.Stamp(_url) != _stamp) throw new RegistryException("settings_conflict");
            }
            catch (Exception error)
            {
                Say(CredentialMessages.Describe(error));
                UpdateState();
                return;
            }
            Run(cancel => _service.Refresh(_url, cancel), "auth.done.refresh", false, true);
        }

        // Explicit browser login with the entered proxy, e.g. to switch accounts while a refresh would still work.
        internal void Relogin()
        {
            if (!_bound || _busy || _closed || _loadError != null || SelectedMode() != Mode.OAuth || !HasOAuth()) return;
            try { StartLogin(); }
            catch (Exception error)
            {
                ClearSecrets();
                Say(CredentialMessages.Describe(error));
                UpdateState();
            }
        }

        // successKey is translated when the operation ends, so a language switch during sign-in is honoured.
        private async void Run(Func<CancellationToken, Task> action, string successKey, bool closeOnSuccess, bool ownChangeOnFailure)
        {
            if (_busy || _closed) return;
            _busy = true;
            _operation = new CancellationTokenSource();
            UpdateState();
            Exception failure = null;
            try { await action(_operation.Token); }
            catch (Exception error) { failure = error; }
            Finish(failure, successKey, closeOnSuccess, ownChangeOnFailure);
        }

        // Runs on the Unity thread after an async operation; must not touch UI once the window is gone.
        internal void Finish(Exception failure, string successKey, bool closeOnSuccess, bool ownChangeOnFailure)
        {
            _busy = false;
            var operation = _operation;
            _operation = null;
            operation?.Dispose();
            if (_closed) return;
            if (failure == null)
            {
                Completed(RegistryText.T(successKey), closeOnSuccess);
                return;
            }
            if (ownChangeOnFailure) Recapture();
            if (!(failure is OperationCanceledException)) Say(CredentialMessages.Describe(failure));
            ShowSummary();
            UpdateState();
        }

        private void Completed(string message, bool close)
        {
            Recapture();
            _changed?.Invoke();
            Say(message);
            if (close) Close();
            else
            {
                ShowSummary();
                UpdateState();
            }
        }

        // Every result names the URL captured at opening, not whatever the settings page shows now.
        private void Say(string message) => Notify(RegistryText.T("auth.title"), RegistryText.F("common.targetUrl", _url) + "\n\n" + message);

        // Only after this window's own change; a failed re-read blocks further writes instead of dropping the check.
        private void Recapture()
        {
            try
            {
                _stamp = _service.Settings.Stamp(_url);
                ReadSummary();
            }
            catch (Exception error)
            {
                _stamp = null;
                _loadError = Data.SafeError(error);
            }
        }

        private bool ConfirmShared(string operation, bool sendsSecret)
        {
            if (!Confirm(RegistryText.T("auth.confirm.title"), operation + "\n\n" + RegistryText.T("auth.confirm.shared") + "\n\n" + _url)) return false;
            return !sendsSecret || !_url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                Confirm(RegistryText.T("http.confirm.title"), RegistryText.T("http.confirm.save"));
        }

        private void ClearSecrets()
        {
            if (!_bound) return;
            _token.SetValueWithoutNotify("");
            _password.SetValueWithoutNotify("");
        }

        private void CancelOperation()
        {
            try { _operation?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void OnEnable() { EditorApplication.quitting += CancelOperation; }

        private void OnDisable()
        {
            EditorApplication.quitting -= CancelOperation;
            RegistryText.Changed -= Relabel;
            // Disabled before a domain reload or close: stop the sign-in so the loopback listener is released.
            _closed = true;
            CancelOperation();
            ClearSecrets();
        }

        private void OnDestroy()
        {
            RegistryText.Changed -= Relabel;
            _closed = true;
            CancelOperation();
            ClearSecrets();
        }
    }

    internal static class CredentialMessages
    {
        internal static string KindLabel(CredentialSummary summary)
        {
            switch (summary.Kind)
            {
                case "Pat": return "PAT";
                case "Npm": return "npm token";
                case "Basic": return "Basic (_auth)";
                case "OAuth": return "OAuth";
                default: return RegistryText.T(summary.Configured ? "kind.externalConfigured" : "kind.externalEmpty");
            }
        }

        internal static string Describe(Exception error) => Describe(Data.SafeError(error));

        // Codes are fixed identifiers without secrets; the text never includes tokens, codes or URLs with queries.
        internal static string Describe(string code)
        {
            // Most codes have their own "error.<code>" text; related codes share one.
            string key;
            switch (code)
            {
                case "settings_conflict":
                case "settings_busy":
                case "token_required":
                case "token_invalid":
                case "basic_username":
                case "basic_password":
                case "registry_url":
                case "registry_url_alias":
                case "proxy_https_required":
                case "oauth_protocol":
                case "oauth_disabled":
                case "callback_port_busy":
                case "callback_bind_failed":
                case "oauth_consent_denied":
                case "oauth_login_required":
                case "oauth_retry_later":
                case "oauth_missing":
                case "configuration_path_changed":
                case "login_busy":
                case "permission_denied":
                case "redirect_rejected":
                case "network_error":
                case "cancelled":
                case "windows_required": key = "error." + code; break;
                case "oauth_capabilities":
                case "oauth_endpoints":
                case "callback_unavailable": key = "error.oauth_unsupported"; break;
                case "store_unreadable":
                case "store_version":
                case "store_invalid": key = "error.store"; break;
                case "recovery_conflict":
                case "recovery_invalid": key = "error.recovery"; break;
                case "settings_owner":
                case "settings_acl":
                case "settings_reparse_point": key = "error.settings_unsafe"; break;
                case "file_access":
                case "file_io": key = "error.file"; break;
                default:
                    key = code != null && code.StartsWith("toml_", StringComparison.Ordinal) ? "error.toml" : "error.operation_failed";
                    break;
            }
            return RegistryText.T(key) + " (" + code + ")";
        }

        // Describes only what this check actually did for its captured URL, not the page's cached credential view.
        internal static string Describe(ConnectionResult result)
        {
            var credential = !result.Attempted ? RegistryText.T("check.notSent") : RegistryText.F("check.sent",
                result.SentScheme == "Bearer" ? "token (Bearer)" : result.SentScheme == "Basic" ? "_auth (Basic)" : RegistryText.T("check.sentNone"));
            string text;
            switch (result.Outcome)
            {
                case ConnectionOutcome.Reachable:
                    text = RegistryText.F("check.reachable", result.Status);
                    break;
                case ConnectionOutcome.CredentialRequired:
                    text = RegistryText.T("check.credentialRequired");
                    break;
                case ConnectionOutcome.Denied:
                    text = RegistryText.T("check.denied");
                    break;
                default:
                    text = result.Status > 0 ? RegistryText.F("check.undeterminedStatus", result.Status) : RegistryText.T("check.undetermined");
                    break;
            }
            if (result.LoginRequired) text += "\n" + RegistryText.T("check.loginRequired");
            return RegistryText.F("common.targetUrl", result.Url) + "\n\n" + text + "\n\n" + credential;
        }
    }
}
