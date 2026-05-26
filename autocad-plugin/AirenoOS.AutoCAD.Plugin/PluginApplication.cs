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
            // Shutdown guard — set flag before AutoCAD tears down so SaveComplete/Writeback abort cleanly
            Application.BeginQuit += (s, e) => IsShuttingDown = true;

            // Hook every document EXACTLY ONCE — at creation/open time.
            // DocumentActivated fires on every tab/focus change, which would stack
            // multiple SaveComplete subscribers and cause N POSTs per single save.
            Application.DocumentManager.DocumentCreated += OnDocumentCreated;

            // Also wire any documents already open when the plugin is NETLOADed mid-session.
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
