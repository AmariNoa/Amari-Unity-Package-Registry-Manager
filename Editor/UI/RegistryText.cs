using System;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine.UIElements;
using com.amari_noa.unity_editor_localization_core.editor;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    // Package strings through the shared localization core; the current language is the core's shared setting.
    [InitializeOnLoad]
    internal static class RegistryText
    {
        internal const string SourceId = "com.amari-noa.amari-unity-package-registry-manager";
        // Editor/Localization, holding en-US.json and ja-JP.json.
        private const string FolderGuid = "3f9d6c2e8a1b4e7d9c0f5a2b6e4d8c13";
        internal static readonly string[] Codes = { "en-US", "ja-JP" };
        internal static readonly string[] Choices = { "English", "日本語" };
        private static readonly int MainThread;

        static RegistryText()
        {
            MainThread = Thread.CurrentThread.ManagedThreadId;
            if (EditorLocalization.Service.RegisteredSourceIds.Contains(SourceId)) return;
            EditorLocalization.Service.RegisterSource(new EditorLocalizationSourceDefinition
            {
                SourceId = SourceId,
                DisplayName = "Amari Unity Package Registry Manager",
                LocalizationFolderGuid = FolderGuid,
                DefaultLanguageCode = "en-US",
                BaseLanguageCode = "en-US"
            });
        }

        internal static event Action<string> Changed
        {
            add => EditorLocalization.Service.LanguageChanged += value;
            remove => EditorLocalization.Service.LanguageChanged -= value;
        }

        // The core reads tables through the AssetDatabase, so a lookup off the main thread returns the key unchanged.
        internal static string T(string key) =>
            Thread.CurrentThread.ManagedThreadId == MainThread ? EditorLocalization.Service.Get(SourceId, key) : key;

        internal static string F(string key, params object[] args) => string.Format(T(key), args);

        // A shared language this package does not ship shows the English fallback without changing the shared setting.
        internal static int ChoiceIndex => Math.Max(0, Array.IndexOf(Codes, EditorLocalization.Service.CurrentLanguageCode));

        internal static void Select(int index)
        {
            if (index < 0 || index >= Codes.Length || EditorLocalization.Service.CurrentLanguageCode == Codes[index]) return;
            EditorLocalization.Service.SetLanguage(SourceId, Codes[index]);
        }

        // Applies "ui.<element name>" texts of a cloned UXML tree; the returned action reapplies them in place.
        internal static Action Localize(VisualElement root)
        {
            Action apply = () => { };
            root.Query<VisualElement>().ForEach(element =>
            {
                var key = "ui." + element.name;
                if (string.IsNullOrEmpty(element.name) || T(key) == key) return;
                apply += () => SetText(element, T(key));
            });
            apply();
            return apply;
        }

        private static void SetText(VisualElement element, string text)
        {
            switch (element)
            {
                case ListView list: list.headerTitle = text; break;
                case RadioButton radio: radio.text = text; break;
                case BaseField<string> field: field.label = text; break;
                case TextElement label: label.text = text; break;
            }
        }

        // Validation messages resolved when read, so helpers can throw off the UI thread and still show the current language.
        internal sealed class Error : ArgumentException
        {
            private readonly string _key;
            internal Error(string key) { _key = key; }
            public override string Message => T(_key);
        }
    }
}
