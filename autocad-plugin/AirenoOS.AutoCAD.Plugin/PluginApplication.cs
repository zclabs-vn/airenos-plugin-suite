using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(AirenoOS.AutoCAD.Plugin.PluginApplication))]
[assembly: CommandClass(typeof(AirenoOS.AutoCAD.Plugin.Commands))]

namespace AirenoOS.AutoCAD.Plugin
{
    /// <summary>
    /// Plugin entry point — loaded/unloaded by AutoCAD via NETLOAD or autoload bundle.
    /// </summary>
    public class PluginApplication : IExtensionApplication
    {
        internal static bool IsShuttingDown = false;

        public void Initialize()
        {
            // Brian feedback #5 (2026-06-05) — session-end sync.
            //
            // Two events cover all paths to "user is closing something":
            //   DocumentToBeDestroyed → fires per-document right before each doc tears down.
            //     Catches both "Close single tab" and "Close AutoCAD" (each open doc fires
            //     this event in turn before BeginQuit). The doc is still readable here —
            //     unlike at BeginQuit, where MdiActiveDocument has already gone null.
            //   BeginQuit → fires once after all docs are gone. We use it only for the
            //     IsShuttingDown flag (#6 shutdown guard) since by then there's nothing
            //     to extract.
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

            // Also wire any documents already open when the plugin is NETLOADed mid-session.
            foreach (Document open in Application.DocumentManager)
            {
                HookSaveComplete(open);
            }

            // Free feature: background poll for highlight requests pushed by the MCP cockpit.
            HighlightManager.Start();

            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\nAirenoOS Plugin loaded. Commands: AIRENO_CONNECT, AIRENO_EXTRACT, AIRENO_WRITEBACK\n");
        }

        public void Terminate()
        {
            IsShuttingDown = true;
            HighlightManager.Stop();
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
