using System.Reflection;
using System.Runtime.InteropServices;
using ClaudeBridge.AutoCad;
using ClaudeBridge.AutoCad.Commands;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(PluginApp))]
[assembly: CommandClass(typeof(PingCommand))]
[assembly: CommandClass(typeof(SummaryCommand))]
[assembly: CommandClass(typeof(SnapshotCommand))]
[assembly: CommandClass(typeof(ProbeCommand))]
[assembly: CommandClass(typeof(SendProbeCommand))]
[assembly: CommandClass(typeof(QueryCommand))]
[assembly: CommandClass(typeof(ExecCommand))]
[assembly: CommandClass(typeof(CameraCommand))]
[assembly: CommandClass(typeof(BatchCaptureCommand))]
[assembly: CommandClass(typeof(BatchCaptureLayoutsCommand))]
[assembly: CommandClass(typeof(OverlayCommand))]
[assembly: CommandClass(typeof(ExtractRouteCommand))]
[assembly: CommandClass(typeof(HttpControlCommands))]

[assembly: AssemblyTitle("ClaudeBridge.AutoCad")]
[assembly: AssemblyDescription("Eyes and hands bridge: lets Claude see and act inside AutoCAD via MCP. Fork of the nanoCAD plugin.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("ClaudeBridge.AutoCad")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: ComVisible(false)]
[assembly: Guid("C4F66461-078C-4296-9A47-E2DE8850BE7B")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
