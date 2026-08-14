namespace CodexU.Core;

public static class SmokeTestDataRoot
{
    public const string ReadyEventPrefix = "Local\\codexU-smoke-";
    public const string DirectoryPrefix = "codexU-smoke-";
    public const int NonceLength = 32;

    public static bool IsValidReadyEventName(string? eventName) =>
        TryGetNonce(eventName, out _);

    public static bool TryResolve(
        string? readyEventName,
        string? configuredDirectory,
        string tempDirectory,
        out string applicationDataDirectory)
    {
        applicationDataDirectory = string.Empty;
        if (!TryGetNonce(readyEventName, out var nonce)
            || string.IsNullOrWhiteSpace(configuredDirectory)
            || string.IsNullOrWhiteSpace(tempDirectory)
            || !Path.IsPathFullyQualified(configuredDirectory)
            || !Path.IsPathFullyQualified(tempDirectory)
            || !string.Equals(configuredDirectory, configuredDirectory.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var suppliedDirectory = Path.TrimEndingDirectorySeparator(configuredDirectory);
            var normalizedDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(configuredDirectory));
            var normalizedTempDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(tempDirectory));

            // A smoke harness must pass the already-normalized direct child that it
            // created. Accepting dot segments or arbitrary descendants would let a
            // malformed harness redirect app writes outside its disposable sandbox.
            if (!string.Equals(
                    suppliedDirectory,
                    normalizedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var expectedDirectory = Path.Combine(
                normalizedTempDirectory,
                $"{DirectoryPrefix}{nonce}");
            if (!string.Equals(
                    normalizedDirectory,
                    expectedDirectory,
                    StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(normalizedDirectory))
            {
                return false;
            }

            applicationDataDirectory = normalizedDirectory;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                           or NotSupportedException
                                           or PathTooLongException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryGetNonce(string? eventName, out string nonce)
    {
        nonce = string.Empty;
        if (eventName is null
            || !eventName.StartsWith(ReadyEventPrefix, StringComparison.Ordinal)
            || eventName.Length != ReadyEventPrefix.Length + NonceLength)
        {
            return false;
        }

        var candidate = eventName[ReadyEventPrefix.Length..];
        if (!candidate.All(Uri.IsHexDigit))
        {
            return false;
        }

        nonce = candidate;
        return true;
    }
}
