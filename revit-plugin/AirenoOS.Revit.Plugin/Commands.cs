using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace AirenoOS.Revit.Plugin.Commands
{
    /// <summary>
    /// AIRENO_CONNECT — prompt for endpoint + bearer token, store on document.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ConnectCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) { message = "No active document."; return Result.Failed; }

            var doc = uidoc.Document;
            try
            {
                var dialog = new UI.ConnectDialog();
                var current = ConnectionConfig.Load(doc);
                dialog.Endpoint = current.Endpoint;
                dialog.Token = current.Token;
                if (dialog.ShowDialog() != true) return Result.Cancelled;

                ProjectTokenManager.EnsureProjectToken(doc);
                ConnectionConfig.Save(doc, dialog.Endpoint, dialog.Token);

                TaskDialog.Show("AirenoOS", "Connected. Endpoint and token saved on this project.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>
    /// AIRENO_EXTRACT — manual full extraction (L1 + L2).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ExtractNowCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) { message = "No active document."; return Result.Failed; }
            if (PluginApplication.IsShuttingDown) return Result.Cancelled;

            SaveHandler.OnDocumentSaved(uidoc.Document, trigger: "manual_command");
            TaskDialog.Show("AirenoOS", "Extraction started. Payload will POST asynchronously.");
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// AIRENO_WRITEBACK — apply pending writebacks to elements.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class ApplyWritebackCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, Autodesk.Revit.DB.ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) { message = "No active document."; return Result.Failed; }
            if (PluginApplication.IsShuttingDown) return Result.Cancelled;

            try
            {
                var applied = Writer.WritebackHandler.Apply(uidoc.Document);
                TaskDialog.Show("AirenoOS",
                    applied == 0
                        ? "No pending writeback items."
                        : $"Writeback applied to {applied} element(s).");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
