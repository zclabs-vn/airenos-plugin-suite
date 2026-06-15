using Bricscad.ApplicationServices;
using Teigha.Runtime;

[assembly: ExtensionApplication(typeof(AirenoOS.BricsCAD.Plugin.PluginApplication))]
[assembly: CommandClass(typeof(AirenoOS.BricsCAD.Plugin.Commands))]

namespace AirenoOS.BricsCAD.Plugin
{
    /// <summary>
    /// Plugin entry point — loaded/unloaded by BricsCAD.
    /// </summary>
    public class PluginApplication : IExtensionApplication
    {
        internal static bool IsShuttingDown = false;

        public void Initialize()
        {
            // Brian feedback #5 (2026-06-05) — session-end sync.
            //
            // Same pattern as AutoCAD plugin: DocumentToBeDestroyed fires per-document
            // before each doc tears down (catches both "Close single tab" and "Close
            // BricsCAD"), while BeginQuit fires once after all docs are gone and is
            // only used for the IsShuttingDown flag (#6 shutdown guard). MdiActiveDocument
            // is unreliable at BeginQuit — verified empirically in v1.0.5 testing.
            Application.DocumentManager.DocumentToBeDestroyed += (s, e) =>
            {
                SaveHandler.SessionLog($"=== DocumentToBeDestroyed fired ({e.Document?.Name}) ===");
                try
                {
                    if (e.Document != null) SaveHandler.OnSessionEnd(e.Document);
                }
                catch (System.Exception ex)
                {
                    SaveHandler.SessionLog($"DocumentToBeDestroyed handler threw: {ex.GetType().Name}: {ex.Message}");
                }
            };

            Application.BeginQuit += (s, e) =>
            {
                SaveHandler.SessionLog("=== BeginQuit fired (setting shutdown flag) ===");
                IsShuttingDown = true;
            };

            // Hook every document EXACTLY ONCE — at creation/open time.
            // DocumentActivated fires on every tab/focus change, which would stack
            // multiple SaveComplete subscribers and cause N POSTs per single save.
            Application.DocumentManager.DocumentCreated += OnDocumentCreated;

            // Also wire any documents already open at plugin load time.
            foreach (Document open in Application.DocumentManager)
            {
                HookSaveComplete(open);
            }

            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\nAirenoOS Plugin loaded. Commands: AIRENO_CONNECT, AIRENO_EXTRACT, AIRENO_WRITEBACK\n");
        }

        public void Terminate()
        {
            IsShuttingDown = true;
        }

        private void OnDocumentCreated(object? sender, DocumentCollectionEventArgs e)
        {
            HookSaveComplete(e.Document);
        }

        private static void HookSaveComplete(Document? doc)
        {
            if (doc == null) return;

            doc.Database.SaveComplete += (s, args) =>
            {
                if (IsShuttingDown) return;
                SaveHandler.OnSaveComplete(doc);
            };
        }
    }
}
