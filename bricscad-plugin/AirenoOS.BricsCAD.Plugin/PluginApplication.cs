using System.IO;
using System.Reflection;
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
            // First-thing log so we can prove the plugin was NETLOADed at all,
            // independent of any later code that might throw.
            SaveHandler.SessionLog($"=== PluginApplication.Initialize entered (PID={System.Diagnostics.Process.GetCurrentProcess().Id}, TEMP={Path.GetTempPath()}) ===");

            // BricsCAD V25 / .NET Framework 4.x strong-name binding workaround.
            //
            // System.Text.Json.dll (net48 build) carries a strong-name reference to
            // System.Runtime.CompilerServices.Unsafe v4.0.4.1, but we ship the v6.0.0
            // NuGet output. .NET Framework's loader treats this as a binding failure
            // because there's no app.config <bindingRedirect> for the BricsCAD EXE.
            // The result: HttpSender throws TypeInitializationException → no payload
            // ever leaves the host (verified 2026-06-17 in session_end.log).
            //
            // The fix: hook AssemblyResolve so when ANY shipped dep is requested by
            // ANY version, we serve whatever DLL of the same simple-name is sitting
            // next to this plugin. Newer-to-older redirection is the standard fix
            // — System.Runtime.CompilerServices.Unsafe v6 is API-compatible with v4.
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

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

            // Free feature: background poll for highlight requests pushed by the MCP cockpit.
            HighlightManager.Start();

            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            ed?.WriteMessage("\nAirenoOS Plugin loaded. Commands: AIRENO_CONNECT, AIRENO_EXTRACT, AIRENO_WRITEBACK\n");
        }

        public void Terminate()
        {
            IsShuttingDown = true;
            HighlightManager.Stop();
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
        }

        /// <summary>
        /// Manual binding-redirect handler. Called by the CLR when a strong-name bind
        /// fails — typically because the version System.Text.Json asks for (e.g.
        /// System.Runtime.CompilerServices.Unsafe v4.0.4.1) doesn't match the version
        /// we ship (v6.0.0). We resolve by simple name from the plugin's own
        /// directory and let Assembly.LoadFrom serve whatever DLL is sitting there.
        /// Returning null lets the CLR continue with its default failure path.
        /// </summary>
        private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
        {
            try
            {
                var requested = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(requested)) return null;

                var pluginDir = Path.GetDirectoryName(typeof(PluginApplication).Assembly.Location);
                if (string.IsNullOrEmpty(pluginDir)) return null;

                var candidate = Path.Combine(pluginDir, requested + ".dll");
                if (!File.Exists(candidate)) return null;

                SaveHandler.SessionLog($"AssemblyResolve: '{args.Name}' → {candidate}");
                return Assembly.LoadFrom(candidate);
            }
            catch (System.Exception ex)
            {
                SaveHandler.SessionLog($"AssemblyResolve threw: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
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
