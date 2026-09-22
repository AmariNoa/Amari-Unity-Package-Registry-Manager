using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal sealed class RegistryAddWindow : EditorWindow
    {
        private const string UxmlPath = "Packages/com.amari-noa.amari-unity-package-registry-manager/Editor/UI/RegistryAddWindow.uxml";
        private Func<string, string, string> _register;

        internal static void Open(Func<string, string, string> register)
        {
            var window = CreateInstance<RegistryAddWindow>();
            window._register = register;
            window.titleContent = new GUIContent("レジストリを追加");
            window.minSize = new Vector2(360, 130);
            window.ShowUtility();
        }

        public void CreateGUI()
        {
            var template = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
            if (template == null) throw new InvalidOperationException("Registry add window UI asset is missing.");
            template.CloneTree(rootVisualElement);
            var name = rootVisualElement.Q<TextField>("registry-name-input");
            var url = rootVisualElement.Q<TextField>("registry-url-input");
            rootVisualElement.Q<Button>("save-registry").clicked += () =>
            {
                if (_register == null) { Close(); return; }
                var error = _register(name.value, url.value);
                if (error != null)
                {
                    EditorUtility.DisplayDialog("登録できませんでした", error, "閉じる");
                    return;
                }
                Close();
            };
            rootVisualElement.Q<Button>("cancel-registry").clicked += Close;
        }
    }
}
