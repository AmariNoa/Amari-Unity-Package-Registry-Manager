using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
namespace com.amari_noa.amari_unity_package_registry_manager.editor
{
    [InitializeOnLoad]
    internal static class CredentialRefreshScheduler
    {
        private static readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private static double next;
        private static bool running;
        static CredentialRefreshScheduler()
        {
            // Batch mode (CI, -executeMethod verification) never touches paths or the store.
            if (Application.isBatchMode) return;
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }
        private static void Stop()
        {
            lifetime.Cancel();
            EditorApplication.update -= Tick;
        }
        private static async void Tick()
        {
            if (running || lifetime.IsCancellationRequested || EditorApplication.isCompiling || EditorApplication.timeSinceStartup < next) return;
            next = EditorApplication.timeSinceStartup + 30; running = true;
            try { await RefreshDue(CredentialServices.Create(), lifetime.Token); }
            catch (Exception) { }
            finally { running = false; }
        }
        // Refreshes due OAuth records of the new private store only. Returns the number refreshed.
        internal static async Task<int> RefreshDue(RegistryService service, CancellationToken cancellation)
        {
            if (!File.Exists(service.Settings.StorePath)) return 0;
            int refreshed = 0;
            foreach (var record in service.Settings.Read().Records)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!RegistryService.RefreshDue(record, service.Settings.Paths.ConfigurationFile, Data.Now)) continue;
                try { await service.Refresh(record.Url, cancellation); refreshed++; }
                catch (Exception) when (!cancellation.IsCancellationRequested) { }
            }
            return refreshed;
        }
    }
}
