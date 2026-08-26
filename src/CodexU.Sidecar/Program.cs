using CodexU.Sidecar;

return await SidecarEntrypoint.RunAsync(
    args,
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    Console.Error);
