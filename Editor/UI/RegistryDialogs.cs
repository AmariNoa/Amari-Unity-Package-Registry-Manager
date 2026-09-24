using System;
using UnityEditor;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    // Native dialogs with cancel on the left and the affirmative action on the right on Windows.
    // Escape/closing is not treated as cancel (accepted by the user); only the left button cancels.
    internal static class RegistryDialogs
    {
        // Replaceable by tests; defaults are the native dialogs.
        internal static Func<string, string, string, string, bool> NativeDialog = (title, message, ok, cancel) => EditorUtility.DisplayDialog(title, message, ok, cancel);
        internal static Func<string, string, string, string, string, int> NativeComplex = (title, message, ok, cancel, alt) => EditorUtility.DisplayDialogComplex(title, message, ok, cancel, alt);

        // Same values as the import choice already uses.
        internal const int AddScopes = 0, Cancel = 1, Replace = 2;

        // "ok" is shown on the left, so it carries Cancel and its true result means cancel.
        internal static bool Confirm(string title, string message) =>
            !NativeDialog(title, message, RegistryText.T("button.cancel"), RegistryText.T("button.continue"));

        // Windows shows ok, alt, cancel from left to right: Cancel, Replace name and scopes, Add scopes.
        internal static int ChooseImport(string title, string message)
        {
            switch (NativeComplex(title, message, RegistryText.T("button.cancel"), RegistryText.T("import.addScopes"), RegistryText.T("import.replace")))
            {
                case 1: return AddScopes;
                case 2: return Replace;
                default: return Cancel;
            }
        }
    }
}
