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
    /// Free-feature: background-polls /v1/highlight and, when the MCP backend pushes a
    /// (native_ids[]) request for this drawing's project token, focuses the user on
    /// each matched entity using — in order of preference:
    ///   1. Native AutoCAD pick-first selection (Editor.SetImpliedSelection) so the
    ///      user sees grips + selection glow, indistinguishable from clicking the
    ///      entity themselves. This is what Brian asked for in the 2026-06-19 feedback.
    ///   2. Yellow TransientManager rectangle as a fallback for entities that can't
    ///      participate in pick-first selection (frozen layer, batch failure).
    ///
    /// Threading:
    ///   • the poll runs on a ThreadPool callback (System.Threading.Timer)
    ///   • all AutoCAD drawable / transaction / editor calls are marshalled back to the
    ///     document command context via ExecuteInCommandContextAsync — the .NET API is
    ///     not thread-safe and any other approach would crash AutoCAD intermittently.
    /// </summary>
    internal static class HighlightManager
    {
        private const int PollIntervalMs       = 3_000;
        private const int HighlightDurationMs  = 5_000;

        private static Timer? _pollTimer;
        private static readonly List<Drawable> _activeOverlays = new List<Drawable>();
        private static readonly List<ObjectId> _highlightedIds = new List<ObjectId>();
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
            SaveHandler.SessionLog($"ApplyHighlights entered, {nativeIds.Count} native_ids to match");
            ClearOverlays();
            ClearHighlightState(doc);

            var selectable = new List<ObjectId>();  // Entities we can natively highlight.
            int fallbackCount = 0;

            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                int scanned = 0;
                foreach (ObjectId id in ms)
                {
                    scanned++;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;
                    var uuid = XdataHelper.ReadField(ent, 0);
                    if (string.IsNullOrEmpty(uuid) || !nativeIds.Contains(uuid)) continue;

                    // Frozen-layer entities can't be picked by AutoCAD — go straight
                    // to the transient-rectangle fallback so the user still gets a
                    // visible hint without having to unfreeze the layer manually.
                    bool frozen = false;
                    try
                    {
                        var layer = tr.GetObject(ent.LayerId, OpenMode.ForRead) as LayerTableRecord;
                        frozen = layer?.IsFrozen ?? false;
                    }
                    catch { }

                    if (frozen)
                    {
                        try { AddOverlayForEntity(ent); fallbackCount++; } catch { }
                    }
                    else
                    {
                        selectable.Add(id);
                    }
                }
                SaveHandler.SessionLog($"Scanned {scanned} MS entities, matched {selectable.Count} selectable + {fallbackCount} frozen-fallback");
                tr.Commit();
            }

            // Native selection + entity Highlight() — Brian 2026-06-19 refinement. Two
            // complementary APIs so the user sees BOTH grip markers (SetImpliedSelection)
            // AND the standard "selected" colour swap (Entity.Highlight). Highlight() is
            // the more reliable signal because grip visibility depends on GRIPS sysvar,
            // current view, and pickfirst state — some hosts (and BricsCAD) leave grips
            // hidden even after SetImpliedSelection unless the user's cursor is over the
            // graphics area, whereas the colour swap always renders.
            int nativeCount = 0;
            if (selectable.Count > 0)
            {
                // (a) Pick-first selection — the CAD app treats these as the currently
                // picked objects. Any subsequent AutoCAD command (ERASE, MOVE, …) will
                // target them. Failure here is unusual and we fall through to overlay.
                try
                {
                    doc.Editor.SetImpliedSelection(selectable.ToArray());
                    SaveHandler.SessionLog("SetImpliedSelection OK");
                }
                catch (System.Exception ex)
                {
                    SaveHandler.SessionLog($"SetImpliedSelection threw: {ex.GetType().Name}: {ex.Message}");
                }

                // (b) Entity.Highlight() flips the entity into the standard "selected"
                // render state (colour swap + halo). Track the highlighted IDs so the
                // auto-clear timer can unhighlight them; native selection self-clears
                // on next user action but Highlight() doesn't.
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (var sid in selectable)
                    {
                        try
                        {
                            var ent = tr.GetObject(sid, OpenMode.ForRead) as Entity;
                            if (ent != null && !ent.IsErased)
                            {
                                ent.Highlight();
                                _highlightedIds.Add(sid);
                                nativeCount++;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            SaveHandler.SessionLog($"Entity.Highlight threw for id {sid.Handle}: {ex.Message}");
                        }
                    }
                    tr.Commit();
                }
                SaveHandler.SessionLog($"Native highlight applied to {nativeCount}/{selectable.Count} entities");

                // Force a screen refresh so the user sees the colour swap without needing
                // to move the mouse — some editor states defer redraw until next event.
                try { doc.Editor.UpdateScreen(); } catch { }

                // If both native calls failed for every entity, degrade to overlay so the
                // user still gets a visible hint.
                if (nativeCount == 0)
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (var sid in selectable)
                        {
                            var ent = tr.GetObject(sid, OpenMode.ForRead) as Entity;
                            if (ent != null)
                                try { AddOverlayForEntity(ent); fallbackCount++; } catch { }
                        }
                        tr.Commit();
                    }
                    SaveHandler.SessionLog($"Native path yielded 0 highlights, fell back to {fallbackCount} overlays");
                }
            }

            try
            {
                var total = nativeCount + fallbackCount;
                var suffix = fallbackCount > 0 ? $" ({fallbackCount} via overlay fallback)" : "";
                doc.Editor.WriteMessage($"\nAirenoOS: highlighted {total} object(s){suffix}\n");
            }
            catch { }

            // Both Entity.Highlight() and transient overlays persist until we clear
            // them explicitly — schedule an auto-clear so the highlight fades after
            // HighlightDurationMs the way Brian's mock UI expects.
            if (_activeOverlays.Count > 0 || _highlightedIds.Count > 0)
            {
                _ = new Timer(_ =>
                {
                    try
                    {
                        Application.DocumentManager.ExecuteInCommandContextAsync(
                            async __ =>
                            {
                                ClearOverlays();
                                ClearHighlightState(doc);
                                await Task.CompletedTask;
                            }, null);
                    }
                    catch { }
                }, null, HighlightDurationMs, Timeout.Infinite);
            }
        }

        private static void ClearHighlightState(Document doc)
        {
            // Unhighlight each entity we lit up via Entity.Highlight().
            if (_highlightedIds.Count > 0)
            {
                try
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        foreach (var id in _highlightedIds)
                        {
                            try
                            {
                                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                                if (ent != null && !ent.IsErased) ent.Unhighlight();
                            }
                            catch { }
                        }
                        tr.Commit();
                    }
                }
                catch { }
                _highlightedIds.Clear();
            }

            // Clear the pick-first / implied selection so a subsequent user command
            // (ERASE, MOVE, …) doesn't accidentally act on objects the user thinks
            // are no longer selected. Runs unconditionally — even if _highlightedIds
            // was empty, an earlier SetImpliedSelection may have set the pick-first set.
            try { doc.Editor.SetImpliedSelection(Array.Empty<ObjectId>()); } catch { }
            try { doc.Editor.UpdateScreen(); } catch { }
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
