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
        // Also assigns XDATA UUIDs to any blocks that don't have one yet (manualMode=true).
        // UUID writes happen safely here because we're outside the save-callback cascade.
        [CommandMethod("AIRENO_EXTRACT")]
        public void ExtractNow()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (PluginApplication.IsShuttingDown) return;

            doc.Editor.WriteMessage("\nAirenoOS: Running extraction (assigning UUIDs to new blocks)...\n");
            SaveHandler.OnSaveComplete(doc, manualMode: true);
        }

        // AIRENO_WRITEBACK — fetch confirmed objects from MCP server and write IDs back to XDATA
        [CommandMethod("AIRENO_WRITEBACK")]
        public void ApplyWriteback()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (PluginApplication.IsShuttingDown) return;

            var ed = doc.Editor;
            ed.WriteMessage("\nAirenoOS: Fetching pending writebacks from server...\n");

            // Fetch from server synchronously (the AIRENO_WRITEBACK command is manual,
            // user-initiated — blocking for a few ms while we await is fine).
            var serverItems = Communicator.HttpSender
                .FetchPendingWritebacksAsync(doc.Database)
                .GetAwaiter().GetResult();

            if (serverItems.Count == 0)
            {
                ed.WriteMessage("\nAirenoOS: No pending writebacks on server. Nothing to apply.\n");
                return;
            }

            // Resolve native_id → ObjectId by scanning ModelSpace XDATA, then enqueue.
            var queued = WritebackQueueLoader.EnqueueFromServer(doc.Database, serverItems);
            ed.WriteMessage($"\nAirenoOS: Queued {queued} item(s) for writeback.\n");

            // Apply (existing handler walks the queue, writes XDATA via XdataHelper).
            WritebackHandler.Apply(doc);
        }
    }
}
