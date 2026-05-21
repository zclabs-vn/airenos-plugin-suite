using System.Reflection;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;

namespace AirenoOS.Revit.Plugin
{
    /// <summary>
    /// Plugin entry point — loaded/unloaded by Revit at app startup/shutdown.
    /// Registers the ribbon, subscribes to DocumentSaved + ApplicationClosing,
    /// and exposes IsShuttingDown so writebacks abort cleanly during host quit.
    /// </summary>
    public class PluginApplication : IExternalApplication
    {
        internal static bool IsShuttingDown = false;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                application.ControlledApplication.DocumentSaved += OnDocumentSaved;

                // ApplicationClosing was removed from ControlledApplication in newer
                // Revit versions. OnShutdown is the canonical add-in shutdown signal
                // and reliably fires before host quit, so we rely on it instead.

                BuildRibbon(application);
                return Result.Succeeded;
            }
            catch
            {
                // Never block Revit from starting. Ribbon failure ≠ plugin failure;
                // commands still work via the keyboard shortcuts / API.
                return Result.Succeeded;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            IsShuttingDown = true;
            try { application.ControlledApplication.DocumentSaved -= OnDocumentSaved; }
            catch { }
            return Result.Succeeded;
        }

        private void OnDocumentSaved(object? sender, DocumentSavedEventArgs e)
        {
            if (IsShuttingDown) return;
            if (e.Document == null || e.Document.IsFamilyDocument) return;
            SaveHandler.OnDocumentSaved(e.Document);
        }

        // ── Ribbon ────────────────────────────────────────────────────────────────────

        private const string TabName   = "AirenoOS";
        private const string PanelName = "AirenoOS";

        private static void BuildRibbon(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); } catch { /* tab already exists */ }
            var panel = app.CreateRibbonPanel(TabName, PanelName);

            var asmPath = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData(
                "AirenoConnect", "Connect", asmPath,
                "AirenoOS.Revit.Plugin.Commands.ConnectCommand")
            {
                ToolTip = "Save AirenoOS endpoint + bearer token on this project."
            });

            panel.AddItem(new PushButtonData(
                "AirenoExtract", "Extract Now", asmPath,
                "AirenoOS.Revit.Plugin.Commands.ExtractNowCommand")
            {
                ToolTip = "Run extraction immediately and POST to AirenoOS."
            });

            panel.AddItem(new PushButtonData(
                "AirenoWriteback", "Apply Writeback", asmPath,
                "AirenoOS.Revit.Plugin.Commands.ApplyWritebackCommand")
            {
                ToolTip = "Write confirmed identity data back onto elements."
            });
        }
    }
}
