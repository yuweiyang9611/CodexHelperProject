using System.Diagnostics;
using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class CodexAppServerClient(string? configuredExecutable = null) : IAppServerClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CandidateStartupTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TotalStartupTimeout = TimeSpan.FromSeconds(8);

    public async Task<AppServerSnapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<string>();
        var executableCandidates = CodexExecutableLocator.FindCandidates(configuredExecutable);

        if (executableCandidates.Count == 0)
        {
            return new AppServerSnapshot(null, null, null, ["未找到 ChatGPT/Codex CLI（codex.exe），额度暂不可用"]);
        }

        using var process = await StartFirstAvailableAsync(executableCandidates, diagnostics, cancellationToken);
        if (process is null)
        {
            return new AppServerSnapshot(null, null, null, diagnostics);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        AccountSnapshot? account = null;
        RateLimitWindow? primary = null;
        RateLimitWindow? secondary = null;
        var completed = new HashSet<int>();
        var malformedLineCount = 0;

        try
        {
            foreach (var request in BuildSnapshotRequests())
            {
                await WriteAsync(process, request, timeout.Token);
            }

            while (!timeout.IsCancellationRequested && completed.Count < 2)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null)
                {
                    break;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    malformedLineCount++;
                    if (malformedLineCount == 1)
                    {
                        diagnostics.Add("Codex app-server 输出包含无法解析的行，已跳过并继续读取");
                    }
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (!TryGetInt(root, "id", out var id))
                    {
                        continue;
                    }

                    if (root.TryGetProperty("error", out var error))
                    {
                        var message = error.TryGetProperty("message", out var messageValue)
                            ? messageValue.GetString()
                            : "未知错误";
                        diagnostics.Add(FormatAppServerError(id, message));
                        completed.Add(id);
                        continue;
                    }

                    if (!root.TryGetProperty("result", out var result))
                    {
                        completed.Add(id);
                        continue;
                    }

                    switch (id)
                    {
                        case 2:
                            account = ParseAccount(result);
                            break;
                        case 3:
                            (primary, secondary) = ParseRateLimits(result);
                            break;
                    }

                    completed.Add(id);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add("读取 Codex app-server 超时");
        }
        catch (IOException ex)
        {
            diagnostics.Add($"Codex app-server 通信失败：{ex.Message}");
        }
        finally
        {
            TryStop(process);
        }

        return new AppServerSnapshot(account, primary, secondary, diagnostics);
    }

    private static Process CreateProcess(string executable)
    {
        var isScript = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = isScript ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : executable,
            Arguments = isScript ? $"/d /s /c \"\"{executable}\" app-server\"" : "app-server",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        return new Process { StartInfo = startInfo };
    }

    internal static async Task<Process?> StartFirstAvailableAsync(
        IReadOnlyList<string> executableCandidates,
        ICollection<string> diagnostics,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        var attemptedCandidates = 0;
        using var startupBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupBudget.CancelAfter(TotalStartupTimeout);

        foreach (var executable in executableCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptedCandidates++;
            var process = CreateProcess(executable);
            var started = false;
            try
            {
                if (process.Start())
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(startupBudget.Token);
                    timeout.CancelAfter(CandidateStartupTimeout);

                    await WriteAsync(process, new
                    {
                        id = 1,
                        method = "initialize",
                        @params = new
                        {
                            clientInfo = new
                            {
                                name = "codexU-windows",
                                title = "codexU",
                                version = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString(3) ?? "development"
                            },
                            capabilities = new { experimentalApi = true }
                        }
                    }, timeout.Token);

                    await WaitForInitializeAsync(process, timeout.Token);
                    await WriteAsync(process, new { method = "initialized" }, timeout.Token);
                    started = true;
                    return process;
                }

                lastError = new InvalidOperationException("进程启动请求未成功");
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                              or System.ComponentModel.Win32Exception
                                              or UnauthorizedAccessException
                                              or IOException
                                              or InvalidDataException)
            {
                lastError = exception;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException(
                    startupBudget.IsCancellationRequested ? "初始化总等待超时" : "初始化握手超时");
            }
            finally
            {
                if (!started)
                {
                    try
                    {
                        TryStop(process);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            if (startupBudget.IsCancellationRequested)
            {
                break;
            }
        }

        var detail = lastError is null ? string.Empty : $"：{lastError.Message}";
        diagnostics.Add($"已找到 Codex CLI，但 app-server 无法启动（已尝试 {attemptedCandidates} 个候选）{detail}");
        return null;
    }

    private static async Task WaitForInitializeAsync(Process process, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new InvalidDataException("app-server 在初始化完成前退出");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!TryGetInt(root, "id", out var id) || id != 1)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var message = error.TryGetProperty("message", out var messageValue)
                        ? messageValue.GetString()
                        : "未知错误";
                    throw new InvalidDataException($"initialize 被拒绝：{message}");
                }

                if (!root.TryGetProperty("result", out _))
                {
                    throw new InvalidDataException("initialize 响应缺少 result");
                }

                return;
            }
        }
    }

    internal static string FormatAppServerError(int requestId, string? message)
    {
        if (!string.IsNullOrWhiteSpace(message)
            && (message.Contains("authentication required", StringComparison.OrdinalIgnoreCase)
                || message.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                || message.Contains("login required", StringComparison.OrdinalIgnoreCase)))
        {
            return "ChatGPT/Codex CLI 登录状态不可用，无法读取额度；请先运行 codex login status，必要时执行 codex login";
        }

        return $"app-server {requestId}: {message ?? "未知错误"}";
    }

    private static async Task WriteAsync(Process process, object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    internal static IReadOnlyList<object> BuildSnapshotRequests() =>
    [
        new { id = 2, method = "account/read", @params = new { refreshToken = false } },
        new { id = 3, method = "account/rateLimits/read" }
    ];

    internal static AccountSnapshot? ParseAccount(JsonElement result)
    {
        if (!result.TryGetProperty("account", out var account) || account.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        var type = GetString(account, "type");
        return new AccountSnapshot(
            type,
            GetString(account, "planType"),
            GetString(account, "email"),
            !string.IsNullOrWhiteSpace(type));
    }

    internal static (RateLimitWindow? Primary, RateLimitWindow? Secondary) ParseRateLimits(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimits", out var limits) || limits.ValueKind is JsonValueKind.Null)
        {
            return (null, null);
        }

        return RateLimitWindowClassifier.Classify(
            ParseWindow(limits, "primary"),
            ParseWindow(limits, "secondary"));
    }

    private static RateLimitWindow? ParseWindow(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (!TryGetDouble(value, "usedPercent", out var used))
        {
            return null;
        }

        if (!double.IsFinite(used))
        {
            return null;
        }

        int? duration = TryGetInt(value, "windowDurationMins", out var durationValue) ? durationValue : null;
        var reset = TryGetLong(value, "resetsAt", out var resetValue) ? UsageCredits.FromUnixTime(resetValue) : null;
        if (reset is { } resetTime && resetTime <= DateTimeOffset.Now)
        {
            return null;
        }

        return new RateLimitWindow(Math.Clamp(used, 0d, 100d), duration, reset);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value);
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetDouble(out value);
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process has already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Best effort cleanup only.
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            // Best effort cleanup only.
        }
    }
}
