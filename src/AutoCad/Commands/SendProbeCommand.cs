using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

namespace ClaudeBridge.AutoCad.Commands
{
    /// <summary>
    /// One-shot probe for <c>Document.SendStringToExecute</c> overloads. Writes signatures
    /// to <c>%TEMP%\claude_bridge\sendstring_probe.txt</c>. Used to learn the actual NanoCAD
    /// signature (NanoCAD's <c>SendStringToExecute</c> differs from AutoCAD's). Safe to delete
    /// once the HTTP bridge is stable.
    /// </summary>
    public sealed class SendProbeCommand
    {
        [CommandMethod("CLAUDE_PROBE_SEND")]
        public void Run()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var editor = doc?.Editor;
            if (doc == null || editor == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"# SendStringToExecute probe — {DateTime.UtcNow:O}");

            var docType = doc.GetType();
            sb.AppendLine($"Document concrete type: {docType.FullName}");
            sb.AppendLine();

            var methods = docType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "SendStringToExecute")
                .ToList();

            sb.AppendLine($"Overloads found: {methods.Count}");
            foreach (var m in methods)
            {
                var args = string.Join(", ",
                    m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
                sb.AppendLine($"  {m.ReturnType.Name} SendStringToExecute({args})");
            }

            var path = Path.Combine(Path.GetTempPath(), "claude_bridge", "sendstring_probe.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString());
            editor.WriteMessage($"\nCLAUDE_PROBE_SEND: wrote {path}");
        }
    }
}
