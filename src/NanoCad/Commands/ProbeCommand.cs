using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HostMgd.ApplicationServices;
using Teigha.Runtime;

namespace ClaudeBridge.NanoCad.Commands
{
    /// <summary>
    /// CLAUDE_PROBE — reflection-only API surface dump. Writes
    /// <c>%TEMP%\claude_bridge\api_probe.txt</c> listing the public members of key
    /// runtime objects (Document, Editor, GraphicsManager) and all types in the
    /// referenced NanoCAD managed assemblies whose name matches rendering / plot /
    /// graphics keywords. Does not invoke any of those members — purely safe metadata
    /// lookup. Used to discover what we can actually use vs what's marked
    /// <c>MGD NOT_IMPLEMENTED</c> in this NanoCAD build.
    /// </summary>
    public sealed class ProbeCommand
    {
        private const string BridgeFolderName = "claude_bridge";
        private const string ProbeFileName = "api_probe.txt";

        private static readonly string[] RenderKeywords =
        {
            "view", "graphics", "plot", "render", "snapshot",
            "image", "bitmap", "window", "screen", "display", "raster"
        };

        // Keywords used when scanning *type names* across assemblies (TitleCase forms).
        private static readonly string[] TypeKeywords =
        {
            "Plot", "View", "Graphics", "Render", "Snapshot",
            "Image", "Bitmap", "Raster", "Display", "Window", "Manager"
        };

        [CommandMethod("CLAUDE_PROBE")]
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
                var sb = new StringBuilder();
                sb.AppendLine($"# API Probe — {DateTime.UtcNow:O}");
                sb.AppendLine($"# Plugin version: {Assembly.GetExecutingAssembly().GetName().Version}");
                sb.AppendLine();

                // ----------------------------------------------------------------
                // 1. Document.GraphicsManager — full surface (this is where
                //    GetGsView lives; we want to see siblings and alternatives).
                // ----------------------------------------------------------------
                var gm = document.GraphicsManager;
                sb.AppendLine("## Document.GraphicsManager — full public surface");
                sb.AppendLine($"   Concrete type: {gm.GetType().FullName}");
                sb.AppendLine($"   Assembly: {gm.GetType().Assembly.GetName().Name}");
                sb.AppendLine();
                DumpMembers(sb, gm.GetType(), filter: null);
                sb.AppendLine();

                // ----------------------------------------------------------------
                // 2. Document instance — filtered by rendering keywords
                // ----------------------------------------------------------------
                sb.AppendLine("## Document — members matching render/window keywords");
                sb.AppendLine($"   Concrete type: {document.GetType().FullName}");
                sb.AppendLine();
                DumpMembers(sb, document.GetType(), filter: RenderKeywords);
                sb.AppendLine();

                // ----------------------------------------------------------------
                // 3. Editor instance — filtered
                // ----------------------------------------------------------------
                sb.AppendLine("## Editor — members matching render/window keywords");
                sb.AppendLine($"   Concrete type: {editor.GetType().FullName}");
                sb.AppendLine();
                DumpMembers(sb, editor.GetType(), filter: RenderKeywords);
                sb.AppendLine();

                // ----------------------------------------------------------------
                // 4. Application — static surface, filtered
                // ----------------------------------------------------------------
                sb.AppendLine("## HostMgd.ApplicationServices.Application — static members matching keywords");
                sb.AppendLine();
                DumpStaticMembers(sb, typeof(Application), filter: RenderKeywords);
                sb.AppendLine();

                // ----------------------------------------------------------------
                // 5. Relevant types across all loaded NanoCAD managed assemblies
                // ----------------------------------------------------------------
                sb.AppendLine("## Types matching render/plot/graphics keywords in NanoCAD-related assemblies");
                sb.AppendLine();
                var seedAssemblies = new[]
                {
                    typeof(Application).Assembly,        // hostmgd
                    document.Database.GetType().Assembly, // hostdbmgd
                    typeof(CommandMethodAttribute).Assembly // nrxgatemgd
                }.Distinct().ToList();

                // Pull in any other loaded assembly that looks NanoCAD-related (host*, teigha*).
                var relatedAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => IsNanoCadRelated(a.GetName().Name ?? string.Empty))
                    .ToList();
                foreach (var a in relatedAssemblies)
                {
                    if (!seedAssemblies.Contains(a))
                    {
                        seedAssemblies.Add(a);
                    }
                }

                foreach (var asm in seedAssemblies.OrderBy(a => a.GetName().Name))
                {
                    sb.AppendLine($"### Assembly: {asm.GetName().Name} ({asm.GetName().Version})");
                    Type[] types;
                    try
                    {
                        types = asm.GetExportedTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t != null).ToArray()!;
                    }

                    var hits = types
                        .Where(t => t != null && TypeKeywords.Any(k => t.Name.IndexOf(k, StringComparison.Ordinal) >= 0))
                        .OrderBy(t => t.FullName)
                        .ToList();

                    if (hits.Count == 0)
                    {
                        sb.AppendLine("   (no matching types)");
                    }
                    else
                    {
                        foreach (var t in hits)
                        {
                            sb.AppendLine($"   {(t.IsAbstract ? "abstract " : "")}{(t.IsSealed ? "sealed " : "")}{(t.IsInterface ? "interface" : (t.IsEnum ? "enum" : "class"))} {t.FullName}");
                        }
                    }
                    sb.AppendLine();
                }

                // ----------------------------------------------------------------
                // 6. Specific spotcheck on PlotFactory / PlotEngine / PlotInfo
                //    if any of those types are reachable — list their methods so we
                //    know whether the programmatic plot path is open.
                // ----------------------------------------------------------------
                sb.AppendLine("## Plot pipeline types (if present)");
                sb.AppendLine();
                var plotTypeNames = new[]
                {
                    "Teigha.DatabaseServices.PlotFactory",
                    "Teigha.DatabaseServices.PlotEngine",
                    "Teigha.DatabaseServices.PlotInfo",
                    "Teigha.DatabaseServices.PlotInfoValidator",
                    "Teigha.DatabaseServices.PlotSettings",
                    "Teigha.DatabaseServices.PlotSettingsValidator",
                    "HostMgd.PlottingServices.PlotFactory",
                    "HostMgd.PlottingServices.PlotEngine",
                    "HostMgd.PlottingServices.PlotInfo",
                    "HostMgd.PlottingServices.PlotProgressDialog"
                };
                foreach (var name in plotTypeNames)
                {
                    var t = FindType(seedAssemblies, name);
                    if (t == null)
                    {
                        sb.AppendLine($"### {name} — NOT FOUND");
                        sb.AppendLine();
                        continue;
                    }
                    sb.AppendLine($"### {t.FullName}");
                    sb.AppendLine($"   Assembly: {t.Assembly.GetName().Name}");
                    DumpMembers(sb, t, filter: null, includeInstance: false);
                    sb.AppendLine();
                    DumpMembers(sb, t, filter: null, includeStatic: false);
                    sb.AppendLine();
                }

                var bridgeDir = Path.Combine(Path.GetTempPath(), BridgeFolderName);
                Directory.CreateDirectory(bridgeDir);
                var probePath = Path.Combine(bridgeDir, ProbeFileName);
                File.WriteAllText(probePath, sb.ToString());

                editor.WriteMessage($"\nCLAUDE_PROBE: wrote {probePath} ({sb.Length:N0} chars)");
            }
            catch (System.Exception ex)
            {
                editor.WriteMessage($"\nCLAUDE_PROBE failed: {ex.Message}");
            }
        }

        // ---- helpers --------------------------------------------------------

        private static bool IsNanoCadRelated(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return false;
            var n = assemblyName.ToLowerInvariant();
            return n.StartsWith("hostmgd") || n.StartsWith("hostdb")
                || n.StartsWith("nrxgate") || n.StartsWith("teigha")
                || n.StartsWith("hostplot") || n.StartsWith("acmgd");
        }

        private static Type? FindType(IEnumerable<Assembly> assemblies, string fullName)
        {
            foreach (var asm in assemblies)
            {
                var t = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
                if (t != null) return t;
            }
            return null;
        }

        private static void DumpMembers(
            StringBuilder sb,
            Type type,
            string[]? filter,
            bool includeInstance = true,
            bool includeStatic = true)
        {
            var flags = BindingFlags.Public | BindingFlags.DeclaredOnly;
            if (includeInstance) flags |= BindingFlags.Instance;
            if (includeStatic) flags |= BindingFlags.Static;

            // Walk the inheritance chain so we see inherited members too (Manager : DisposableWrapper etc).
            var visited = new HashSet<string>();
            Type? cursor = type;
            while (cursor != null && cursor != typeof(object))
            {
                foreach (var prop in cursor.GetProperties(flags).OrderBy(p => p.Name))
                {
                    if (filter != null && !MatchesAny(prop.Name, filter)) continue;
                    var sig = $"  prop {FormatType(prop.PropertyType)} {prop.Name} {{ {(prop.CanRead ? "get;" : "")} {(prop.CanWrite ? "set;" : "")} }}";
                    if (visited.Add(sig)) sb.AppendLine(sig);
                }
                foreach (var meth in cursor.GetMethods(flags)
                             .Where(m => !m.IsSpecialName) // skip property getters/setters
                             .OrderBy(m => m.Name))
                {
                    if (filter != null && !MatchesAny(meth.Name, filter)) continue;
                    var args = string.Join(", ", meth.GetParameters()
                        .Select(p => $"{FormatType(p.ParameterType)} {p.Name}"));
                    var staticPrefix = meth.IsStatic ? "static " : "";
                    var sig = $"  {staticPrefix}method {FormatType(meth.ReturnType)} {meth.Name}({args})";
                    if (visited.Add(sig)) sb.AppendLine(sig);
                }
                cursor = cursor.BaseType;
            }
        }

        private static void DumpStaticMembers(StringBuilder sb, Type type, string[] filter)
        {
            DumpMembers(sb, type, filter, includeInstance: false, includeStatic: true);
        }

        private static bool MatchesAny(string name, string[] filter)
        {
            foreach (var k in filter)
            {
                if (name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string FormatType(Type t)
        {
            if (t == typeof(void)) return "void";
            if (t == typeof(int)) return "int";
            if (t == typeof(string)) return "string";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(double)) return "double";
            if (t == typeof(object)) return "object";
            if (t == typeof(IntPtr)) return "IntPtr";
            return t.Name;
        }
    }
}
