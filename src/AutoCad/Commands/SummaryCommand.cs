using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Newtonsoft.Json;

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// CLAUDE_SUMMARY — read-only structural snapshot of the active drawing.
    /// Writes <c>%TEMP%\claude_bridge\summary.json</c> with: layers, blocks, entity-type
    /// counts (model space), drawing extents, and basic document metadata.
    /// Does not modify the drawing in any way. Patterned after
    /// <c>profile/src/GasProfile.AutoCad/Commands/DumpBlocksCommand.cs</c>.
    /// </summary>
    public sealed class SummaryCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string SummaryFileName = "summary.json";

        [CommandMethod("CLAUDE_SUMMARY")]
        public void Run()
        {
            var document = Application.DocumentManager.MdiActiveDocument;
            var editor = document?.Editor;
            if (document == null || editor == null)
            {
                return;
            }

            try
            {
                var database = document.Database;
                object payload;

                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var layers = CollectLayers(transaction, database);
                    var blocks = CollectBlocks(transaction, database);
                    var entityCounts = CollectModelSpaceEntityCounts(transaction, database);

                    payload = new
                    {
                        plugin = "ClaudeBridge.AutoCad",
                        version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0",
                        timestamp_utc = DateTime.UtcNow.ToString("O"),
                        document = new
                        {
                            name = document.Name ?? string.Empty,
                            // Database.Filename is the on-disk path; can be empty for new unsaved drawings.
                            filename = database.Filename ?? string.Empty,
                            extents = FormatExtents(database)
                        },
                        layers = layers,
                        blocks = blocks,
                        entity_counts_model_space = entityCounts
                    };

                    transaction.Commit();
                }

                var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
                Directory.CreateDirectory(bridgeDir);
                var summaryPath = Path.Combine(bridgeDir, SummaryFileName);

                var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
                File.WriteAllText(summaryPath, json);

                editor.WriteMessage($"\nCLAUDE_SUMMARY: wrote {summaryPath}");
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nCLAUDE_SUMMARY failed: {ex.Message}");
            }
        }

        private static List<object> CollectLayers(Transaction transaction, Database database)
        {
            var result = new List<object>();
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId layerId in layerTable)
            {
                if (transaction.GetObject(layerId, OpenMode.ForRead, false) is LayerTableRecord layer)
                {
                    result.Add(new
                    {
                        name = layer.Name,
                        // ColorIndex (1..255) or ByLayer/ByBlock if entity color is special — for a layer
                        // itself ColorIndex is the canonical attribute.
                        color_index = (int)layer.Color.ColorIndex,
                        is_frozen = layer.IsFrozen,
                        is_off = layer.IsOff,
                        is_locked = layer.IsLocked,
                        is_plottable = layer.IsPlottable
                    });
                }
            }
            return result;
        }

        private static List<object> CollectBlocks(Transaction transaction, Database database)
        {
            var result = new List<object>();
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blockTable)
            {
                if (!(transaction.GetObject(blockId, OpenMode.ForRead, false) is BlockTableRecord block))
                {
                    continue;
                }

                // Skip anonymous (system-generated, e.g. dynamic block sub-defs) and layout block records
                // (Model/Paper spaces — entity counts come from a dedicated path below).
                if (block.IsAnonymous || block.IsLayout)
                {
                    continue;
                }

                var entityCount = 0;
                var attributeDefCount = 0;
                foreach (ObjectId entityId in block)
                {
                    entityCount++;
                    if (transaction.GetObject(entityId, OpenMode.ForRead, false) is AttributeDefinition)
                    {
                        attributeDefCount++;
                    }
                }

                result.Add(new
                {
                    name = block.Name,
                    entity_count = entityCount,
                    attribute_def_count = attributeDefCount,
                    has_attributes = block.HasAttributeDefinitions
                });
            }
            return result;
        }

        private static List<object> CollectModelSpaceEntityCounts(Transaction transaction, Database database)
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            // BlockTableRecord.ModelSpace — known string key for the model space block.
            var modelSpace = (BlockTableRecord)transaction.GetObject(
                blockTable[BlockTableRecord.ModelSpace],
                OpenMode.ForRead);

            var counts = new Dictionary<string, int>();
            foreach (ObjectId entityId in modelSpace)
            {
                if (transaction.GetObject(entityId, OpenMode.ForRead, false) is Entity entity)
                {
                    var typeName = entity.GetType().Name;
                    counts[typeName] = counts.TryGetValue(typeName, out var n) ? n + 1 : 1;
                }
            }

            return counts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => (object)new { type = kv.Key, count = kv.Value })
                .ToList();
        }

        private static object FormatExtents(Database database)
        {
            // Database.Extmin/Extmax reflect the last computed drawing extents.
            // AutoCAD divergence vs Teigha/NanoCAD: NanoCAD returns sentinel ±1e20 for
            // never-computed extents; AutoCAD throws eInvalidInput. Defensive wrap so
            // a fresh empty Drawing1 doesn't 500 the whole summary request.
            try
            {
                return new
                {
                    min = FormatPoint(database.Extmin),
                    max = FormatPoint(database.Extmax),
                    available = true,
                };
            }
            catch
            {
                return new
                {
                    min = (object?)null,
                    max = (object?)null,
                    available = false,
                    note = "extents not initialized (empty drawing or never had ZOOM_EXTENTS)",
                };
            }
        }

        private static object FormatPoint(Point3d point)
        {
            return new { x = point.X, y = point.Y, z = point.Z };
        }
    }
}
