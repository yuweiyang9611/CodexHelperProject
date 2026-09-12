using CodexU.Sidecar;

if (args.Length == 1 && args[0] == "--desktop-bridge") return await DesktopWindowBridge.RunAsync();

return await SidecarEntrypoint.RunAsync(
    args,
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    Console.Error);
