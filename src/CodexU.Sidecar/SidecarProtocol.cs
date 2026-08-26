namespace CodexU.Sidecar;

public static class SidecarProtocol
{
    public const int Version = 1;

    public const int MaximumFrameBytes = 1024 * 1024;

    public const string ShutdownMethod = "shutdown";

    public const string ShutdownAcknowledgementMethod = "shutdownAck";
}

public sealed record SidecarHandshake(
    int Version,
    string Type,
    int ProtocolVersion,
    string BackendVersion,
    IReadOnlyList<string> Capabilities)
{
    public static SidecarHandshake Create(string backendVersion, IReadOnlyList<string> capabilities) =>
        new(
            SidecarProtocol.Version,
            "handshake",
            SidecarProtocol.Version,
            backendVersion,
            capabilities);
}

public sealed record SidecarControlMessage(
    int Version,
    string Type,
    string Method)
{
    public static SidecarControlMessage ShutdownAcknowledgement() =>
        new(
            SidecarProtocol.Version,
            "control",
            SidecarProtocol.ShutdownAcknowledgementMethod);
}

public sealed record SidecarHostState(
    int Version,
    string Type,
    bool GlobalHotKeyRegistered);

public sealed class SidecarProtocolException : IOException
{
    public SidecarProtocolException(string message)
        : base(message)
    {
    }

    public SidecarProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
