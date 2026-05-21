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
            // Subscribe to shutdown guard
            Application.BeginQuit += (s, e) => IsShuttingDown = true;

            // Subscribe to save event
            Application.DocumentManager.DocumentActivated += OnDocumentActivated;

            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\nAirenoOS Plugin loaded. Commands: AIRENO_CONNECT, AIRENO_EXTRACT, AIRENO_WRITEBACK\n");
        }

        public void Terminate()
        {
            IsShuttingDown = true;
        }

        private void OnDocumentActivated(object? sender, DocumentCollectionEventArgs e)
        {
            if (e.Document == null) return;

            // Hook SaveComplete on each newly activated document
            e.Document.Database.SaveComplete += (s, args) =>
            {
                if (IsShuttingDown) return;
                SaveHandler.OnSaveComplete(e.Document);
            };
        }
    }
}
