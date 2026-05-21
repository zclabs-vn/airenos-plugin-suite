using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace AirenoOS.AutoCAD.Plugin
{
    /// <summary>
    /// The 3 menu commands exposed to AutoCAD users.
    /// </summary>
    public class Commands
    {
        // AIRENO_CONNECT — store bearer token + endpoint in Named Object Dictionary
        [CommandMethod("AIRENO_CONNECT")]
        public void Connect()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            var tokenResult = ed.GetString(new PromptStringOptions("\nEnter AirenoOS bearer token: ") { AllowSpaces = false });
            if (tokenResult.Status != PromptStatus.OK) return;

            var endpointResult = ed.GetString(new PromptStringOptions("\nEnter AirenoOS endpoint URL: ") { AllowSpaces = false });
            if (endpointResult.Status != PromptStatus.OK) return;

            ProjectTokenManager.EnsureProjectToken(doc.Database);
            ConnectionConfig.Save(doc.Database, endpointResult.StringResult, tokenResult.StringResult);

            ed.WriteMessage("\nAirenoOS connected. Token and endpoint saved to drawing.\n");
        }

        // AIRENO_EXTRACT — manual full extraction (Layer 1 + Layer 2)
        [CommandMethod("AIRENO_EXTRACT")]
        public void ExtractNow()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (PluginApplication.IsShuttingDown) return;

            doc.Editor.WriteMessage("\nAirenoOS: Running extraction...\n");
            SaveHandler.OnSaveComplete(doc);
        }

        // AIRENO_WRITEBACK — write confirmed IDs back to object XDATA
        [CommandMethod("AIRENO_WRITEBACK")]
        public void ApplyWriteback()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (PluginApplication.IsShuttingDown) return;

            doc.Editor.WriteMessage("\nAirenoOS: Applying writeback...\n");
            WritebackHandler.Apply(doc);
        }
    }
}
