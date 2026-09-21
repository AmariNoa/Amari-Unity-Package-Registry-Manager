using UnityEditor;

namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    internal static class RegistryCredentialsSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(RegistryCredentialsSettingsProvider).Assembly);
            var displayName = package?.displayName ?? "Amari Unity Package Registry Manager";

            return new SettingsProvider("Project/Package Manager/Registry Credentials", SettingsScope.Project)
            {
                label = "Registry Credentials",
                guiHandler = _ =>
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.LabelField(displayName, EditorStyles.miniLabel);
                    }
                }
            };
        }
    }
}
