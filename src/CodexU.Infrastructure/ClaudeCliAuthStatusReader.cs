using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

internal sealed record ClaudeCliExecutable(string Path, ClaudeCliSource Source);
internal sealed record ClaudeCliAuthCommandResult(int ExitCode, string Output);

/// <summary>Runs only the official CLI's auth-status command and returns no account or credential fields.</summary>
public sealed class ClaudeCliAuthStatusReader
{
    private const int MaximumOutputBytes = 64 * 1024;
    private readonly Func<ClaudeCliExecutable?> _discover;
    private readonly Func<ClaudeCliExecutable, CancellationToken, Task<ClaudeCliAuthCommandResult>> _execute;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeout;

    public ClaudeCliAuthStatusReader() : this(
        () => Discover(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("PATH")), RunAsync, TimeProvider.System, TimeSpan.FromSeconds(8))
    { }

    internal ClaudeCliAuthStatusReader(Func<ClaudeCliExecutable?> discover,
        Func<ClaudeCliExecutable, CancellationToken, Task<ClaudeCliAuthCommandResult>> execute,
        TimeProvider clock, TimeSpan timeout)
    {
        _discover = discover;
        _execute = execute;
        _clock = clock;
        _timeout = timeout;
    }

    public async Task<ClaudeCliAuthStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ClaudeCliSource.None;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            var executable = _discover();
            if (executable is null) return Result(ClaudeCliLoginStatus.NotInstalled, ClaudeCliAuthMethod.None, source);
            source = executable.Source;
            var command = await _execute(executable, deadline.Token);
            return Parse(command, source, _clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // InvalidDataException derives from SystemException rather than IOException; the
        // discovery and output bounds throw it and must surface as an unavailable check.
        catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidDataException
            or UnauthorizedAccessException or Win32Exception or ArgumentException or InvalidOperationException
            or NotSupportedException or System.Security.SecurityException)
        {
            return Result(ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown, source);
        }
    }

    private ClaudeCliAuthStatus Result(ClaudeCliLoginStatus status, ClaudeCliAuthMethod method, ClaudeCliSource source) =>
        new(status, method, source, _clock.GetUtcNow());

    internal static ClaudeCliAuthStatus Parse(ClaudeCliAuthCommandResult command, ClaudeCliSource source, DateTimeOffset checkedAt)
    {
        var unavailable = new ClaudeCliAuthStatus(ClaudeCliLoginStatus.Unavailable, ClaudeCliAuthMethod.Unknown, source, checkedAt);
        try
        {
            if (Encoding.UTF8.GetByteCount(command.Output) > MaximumOutputBytes) return unavailable;
            using var json = JsonDocument.Parse(command.Output, new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unavailable;
            var critical = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if ((property.Name is "loggedIn" or "authMethod") && !critical.Add(property.Name)) return unavailable;
            if (!root.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("authMethod", out var authMethod) || authMethod.ValueKind != JsonValueKind.String)
                return unavailable;
            var method = authMethod.GetString() switch
            {
                "none" => ClaudeCliAuthMethod.None,
                "claude.ai" => ClaudeCliAuthMethod.ClaudeAi,
                "oauth_token" => ClaudeCliAuthMethod.OauthToken,
                "api_key" => ClaudeCliAuthMethod.ApiKey,
                "api_key_helper" => ClaudeCliAuthMethod.ApiKeyHelper,
                "third_party" => ClaudeCliAuthMethod.ThirdParty,
                _ => ClaudeCliAuthMethod.Unknown
            };
            if (loggedIn.GetBoolean() && command.ExitCode == 0 && method != ClaudeCliAuthMethod.None)
                return new(ClaudeCliLoginStatus.LoggedIn, method, source, checkedAt);
            if (!loggedIn.GetBoolean() && command.ExitCode == 1)
                return new(ClaudeCliLoginStatus.LoggedOut, method, source, checkedAt);
            return unavailable;
        }
        catch (JsonException) { return unavailable; }
    }

    internal static ClaudeCliExecutable? Discover(string home, string localApplicationData, string? searchPath)
    {
        var fileName = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        var standalone = Path.Combine(home, ".local", "bin", fileName);
        if (File.Exists(standalone)) return new(standalone, ClaudeCliSource.Standalone);
        foreach (var entry in (searchPath ?? "").Split(Path.PathSeparator).Take(256))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory) || directory.StartsWith("\\\\", StringComparison.Ordinal)) continue;
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return new(candidate, ClaudeCliSource.Path);
        }

        var desktopRoot = Path.Combine(localApplicationData, "Claude-Data");
        if (!Directory.Exists(desktopRoot)) return null;
        var packages = BoundedDirectories(desktopRoot, "claude_*", 16);
        var versions = packages.SelectMany(package =>
        {
            var root = Path.Combine(package, "claude-code");
            return Directory.Exists(root) ? BoundedDirectories(root, "*", 64) : [];
        }).Select(path => (Path: path, Version: Version.TryParse(Path.GetFileName(path), out var version) ? version : null))
            .Where(item => item.Version is not null).OrderByDescending(item => item.Version).ThenBy(item => item.Path, StringComparer.Ordinal).ToArray();
        foreach (var version in versions)
            foreach (var installation in BoundedDirectories(version.Path, "*", 16).Order(StringComparer.Ordinal))
            {
                var candidate = Path.Combine(installation, fileName);
                if (File.Exists(candidate)) return new(candidate, ClaudeCliSource.DesktopEmbedded);
            }
        return null;
    }

    private static string[] BoundedDirectories(string root, string pattern, int limit)
    {
        var directories = Directory.EnumerateDirectories(root, pattern, SearchOption.TopDirectoryOnly).Take(limit + 1).ToArray();
        if (directories.Length > limit) throw new InvalidDataException("CLI discovery limit exceeded.");
        return directories;
    }

    internal static ProcessStartInfo StartInfo(string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("auth");
        info.ArgumentList.Add("status");
        return info;
    }

    private static Task<ClaudeCliAuthCommandResult> RunAsync(ClaudeCliExecutable executable, CancellationToken cancellationToken) =>
        RunProcessAsync(StartInfo(executable.Path), cancellationToken);

    internal static async Task<ClaudeCliAuthCommandResult> RunProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("CLI did not start.");
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, MaximumOutputBytes, cancellationToken);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 16 * 1024, cancellationToken);
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdout, stderr);
            return new(process.ExitCode, new UTF8Encoding(false, true).GetString(await stdout));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) { }
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit) throw new InvalidDataException("CLI output limit exceeded.");
            output.Write(buffer, 0, count);
        }
    }
}
