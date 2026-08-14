using System.IO;
using CodexU.Core;

namespace CodexU.App;

internal static class SmokeTestSignals
{
    private const string ReadyEventEnvironmentVariable = "CODEXU_SMOKE_READY_EVENT";
    private const string ApplicationDataDirectoryEnvironmentVariable = "CODEXU_SMOKE_APP_DATA_DIRECTORY";
    private const string ForceStatusStripEnvironmentVariable = "CODEXU_SMOKE_FORCE_STATUS_STRIP";
    private const string StatusEventEnvironmentVariable = "CODEXU_SMOKE_STATUS_EVENT";
    private const string StatusEventPrefix = "Local\\codexU-status-";

    public static bool IsSmokeRequested =>
        SmokeTestDataRoot.IsValidReadyEventName(
            Environment.GetEnvironmentVariable(ReadyEventEnvironmentVariable));

    public static bool IsSmokeMode =>
        TryGetApplicationDataDirectory(out _);

    public static bool IsStatusStripSmokeMode
    {
        get
        {
            var statusEventName = Environment.GetEnvironmentVariable(StatusEventEnvironmentVariable);

            return IsSmokeMode
                   && string.Equals(
                       Environment.GetEnvironmentVariable(ForceStatusStripEnvironmentVariable),
                       "1",
                       StringComparison.Ordinal)
                   && IsValidEventName(statusEventName, StatusEventPrefix);
        }
    }

    public static bool TryGetApplicationDataDirectory(out string applicationDataDirectory) =>
        SmokeTestDataRoot.TryResolve(
            Environment.GetEnvironmentVariable(ReadyEventEnvironmentVariable),
            Environment.GetEnvironmentVariable(ApplicationDataDirectoryEnvironmentVariable),
            Path.GetTempPath(),
            out applicationDataDirectory);

    public static void SignalWebReady()
    {
        if (IsSmokeMode)
        {
            SignalEvent(ReadyEventEnvironmentVariable, SmokeTestDataRoot.ReadyEventPrefix);
        }
    }

    public static void SignalStatusRendered()
    {
        if (IsStatusStripSmokeMode)
        {
            SignalEvent(StatusEventEnvironmentVariable, StatusEventPrefix);
        }
    }

    private static void SignalEvent(string environmentVariable, string prefix)
    {
        var eventName = Environment.GetEnvironmentVariable(environmentVariable);
        if (!IsValidEventName(eventName, prefix))
        {
            return;
        }

        try
        {
            using var readyEvent = EventWaitHandle.OpenExisting(eventName!);
            readyEvent.Set();
        }
        catch (Exception exception) when (exception is WaitHandleCannotBeOpenedException
                                           or UnauthorizedAccessException
                                           or IOException)
        {
            // The smoke-test harness may have exited before the application reached this stage.
        }
    }

    private static bool IsValidEventName(string? eventName, string prefix) =>
        eventName is not null
        && eventName.StartsWith(prefix, StringComparison.Ordinal)
        && eventName.Length == prefix.Length + SmokeTestDataRoot.NonceLength
        && eventName[prefix.Length..].All(Uri.IsHexDigit);
}
