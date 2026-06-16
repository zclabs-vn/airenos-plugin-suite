using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using AirenoOS.AutoCAD.Plugin.Communicator;

namespace AirenoOS.AutoCAD.Plugin
{
    /// <summary>
    /// Free-feature: background-polls /v1/highlight and, when the MCP cockpit pushes a
    /// (native_ids[]) request for this drawing's project token, flashes a yellow
    /// rectangle around each matched entity for HighlightDurationMs.
    ///
    /// Threading:
    ///   • the poll runs on a ThreadPool callback (System.Threading.Timer)
    ///   • all AutoCAD drawable / transaction calls are marshalled back to the document
    ///     command context via ExecuteInCommandContextAsync — the .NET API is not
    ///     thread-safe and any other approach would crash AutoCAD intermittently.
    /// </summary>
    internal static class HighlightManager
    {
        private const int PollIntervalMs       = 3_000;
        private const int HighlightDurationMs  = 5_000;

        private static Timer? _pollTimer;
        private static readonly List<Drawable> _activeOverlays = new List<Drawable>();
        private static int _pollInFlight; // 0 = idle, 1 = busy

        public static void Start()
        {
            // Stagger first poll so plugin Initialize() returns quickly.
            _pollTimer = new Timer(PollOnce, null, PollIntervalMs, PollIntervalMs);
        }

        public static void Stop()
        {
            try { _pollTimer?.Dispose(); } catch { }
            _pollTimer = null;
            try { ClearOverlays(); } catch { }
        }

        // ── Background polling ────────────────────────────────────────────────────

        private static async void PollOnce(object? state)
        {
            if (PluginApplication.IsShuttingDown) return;
            // Skip if the previous poll is still running — avoids piling up requests
            // when the MCP server is slow / unreachable.
            if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0) return;
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                var requests = await HttpSender.FetchHighlightsAsync(doc.Database).ConfigureAwait(false);
                if (requests.Count == 0) return;

                var nativeIds = requests
                    .SelectMany(r => r.NativeIds ?? new List<string>())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToHashSet();
                if (nativeIds.Count == 0) return;

                // ExecuteInCommandContextAsync marshals onto AutoCAD's document thread —
                // the only place we're allowed to touch DatabaseServices / GraphicsInterface.
                // Returns DocumentCollection.ExecutionResult (not Task), so no ConfigureAwait.
                await Application.DocumentManager.ExecuteInCommandContextAsync(
                    async _ =>
                    {
                        try { ApplyHighlights(doc, nativeIds); } catch { }
                        await Task.CompletedTask;
                    }, null);
            }
            catch { /* never crash AutoCAD from a background poll */ }
            finally
            {
                Interlocked.Exchange(ref _pollInFlight, 0);
            }
        }

        // ── Apply / clear overlays (main thread) ──────────────────────────────────

        private static void ApplyHighlights(Document doc, HashSet<string> nativeIds)
        {
            ClearOverlays();

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    var uuid = XdataHelper.ReadField(ent, 0);
                    if (string.IsNullOrEmpty(uuid) || !nativeIds.Contains(uuid)) continue;
                    try { AddOverlayForEntity(ent); } catch { /* skip entities without extents */ }
                }
                tr.Commit();
            }

            try { doc.Editor.WriteMessage($"\nAirenoOS: highlighting {_activeOverlays.Count} object(s)...\n"); } catch { }

            // Auto-clear after HighlightDurationMs. Schedule the clear back through the
            // document command context so we don't dispose Drawables off-thread.
            _ = new Timer(_ =>
            {
                try
                {
                    Application.DocumentManager.ExecuteInCommandContextAsync(
                        async __ => { ClearOverlays(); await Task.CompletedTask; }, null);
                }
                catch { }
            }, null, HighlightDurationMs, Timeout.Infinite);
        }

        private static void AddOverlayForEntity(Entity ent)
        {
            var ext = ent.GeometricExtents;
            // Fully qualify — Autodesk.AutoCAD.GraphicsInterface also defines a Polyline.
            var pl = new Autodesk.AutoCAD.DatabaseServices.Polyline(5);
            pl.AddVertexAt(0, new Point2d(ext.MinPoint.X, ext.MinPoint.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(ext.MaxPoint.X, ext.MinPoint.Y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(ext.MinPoint.X, ext.MaxPoint.Y), 0, 0, 0);
            pl.AddVertexAt(4, new Point2d(ext.MinPoint.X, ext.MinPoint.Y), 0, 0, 0);
            pl.Closed = true;
            pl.Color = Color.FromColorIndex(ColorMethod.ByAci, 2); // yellow (ACI 2)
            // Slightly thicker than default so the highlight reads at zoom-fit scale.
            pl.ConstantWidth = Math.Max(1.0, (ext.MaxPoint.X - ext.MinPoint.X) * 0.02);

            TransientManager.CurrentTransientManager.AddTransient(
                pl, TransientDrawingMode.Highlight, 128, new IntegerCollection());
            _activeOverlays.Add(pl);
        }

        private static void ClearOverlays()
        {
            foreach (var d in _activeOverlays)
            {
                try
                {
                    TransientManager.CurrentTransientManager.EraseTransient(d, new IntegerCollection());
                    d.Dispose();
                }
                catch { }
            }
            _activeOverlays.Clear();
        }
    }
}
