using System.Reflection;
using System.Runtime.InteropServices;
using ClaudeBridge.NanoCad;
using ClaudeBridge.NanoCad.Commands;
using Teigha.Runtime;

[assembly: ExtensionApplication(typeof(PluginApp))]
[assembly: CommandClass(typeof(PingCommand))]
[assembly: CommandClass(typeof(SummaryCommand))]
[assembly: CommandClass(typeof(SnapshotCommand))]
[assembly: CommandClass(typeof(ProbeCommand))]
[assembly: CommandClass(typeof(SendProbeCommand))]
[assembly: CommandClass(typeof(QueryCommand))]
[assembly: CommandClass(typeof(ExecCommand))]
[assembly: CommandClass(typeof(CameraCommand))]
[assembly: CommandClass(typeof(OverlayCommand))]
[assembly: CommandClass(typeof(HttpControlCommands))]

[assembly: AssemblyTitle("ClaudeBridge.NanoCad")]
[assembly: AssemblyDescription("Eyes and hands bridge: lets Claude see and act inside NanoCAD via MCP.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("ClaudeBridge.NanoCad")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: ComVisible(false)]
[assembly: Guid("2B5DFFA4-3D3E-4021-BE91-B87F3060D51B")]
[assembly: AssemblyVersion("0.5.0.0")]
[assembly: AssemblyFileVersion("0.5.0.0")]
