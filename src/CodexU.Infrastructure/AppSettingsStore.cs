using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly string _backupPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettingsStore(string? applicationDataDirectory = null)
    {
        var directory = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        directory = Path.GetFullPath(directory);
        LocalRestoreJournal.RecoverPending(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
        _backupPath = _settingsPath + ".bak";
    }

    public string SettingsPath => _settingsPath;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_settingsPath) && !File.Exists(_backupPath))
            {
                return new AppSettings().Normalize();
            }

            try
            {
                return await ReadFileAsync(
                    File.Exists(_settingsPath) ? _settingsPath : _backupPath,
                    cancellationToken);
            }
            catch (Exception primaryException) when (IsRecoverableReadFailure(primaryException))
            {
                if (File.Exists(_backupPath))
                {
                    AppSettings recovered;
                    try
                    {
                        recovered = await ReadFileAsync(_backupPath, cancellationToken);
                    }
                    catch (Exception backupException) when (IsRecoverableReadFailure(backupException))
                    {
                        PreserveUnreadablePrimary();
                        return new AppSettings().Normalize();
                    }

                    PreserveUnreadablePrimary();
                    TryRepairPrimaryFromBackup();
                    return recovered;
                }
                else
                {
                    PreserveUnreadablePrimary();
                }

                // Settings are preferences rather than user-authored content. Keep an
                // unreadable copy for manual recovery, then let the application start
                // with safe defaults instead of becoming unlaunchable.
                return new AppSettings().Normalize();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AppSettings> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = settings.Validate().Normalize();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _settingsPath + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 16 * 1024,
                    FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(_settingsPath))
                {
                    File.Replace(temporaryPath, _settingsPath, _backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, _settingsPath);
                    TryCreateInitialBackup();
                }

                return normalized;
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A stale temporary file is replaced during the next save.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryCreateInitialBackup()
    {
        try
        {
            if (!File.Exists(_backupPath))
            {
                File.Copy(_settingsPath, _backupPath, overwrite: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The primary file is already durably committed. A first-generation
            // backup is best effort; reporting failure now would make callers retry
            // an operation that has already succeeded on disk.
        }
    }

    private static async Task<AppSettings> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var settings = document.RootElement.Deserialize<AppSettings>(JsonOptions)
            ?? throw new JsonException("设置文件内容为空");

        // Before rate catalogs were versioned, every custom model entry used family-prefix
        // matching and settings.json had no matchMode property. Preserve that established
        // behavior only for legacy settings; newly created rows default to safer exact matching.
        if (settings.CustomModelRates is { Count: > 0 } rates
            && TryGetProperty(document.RootElement, "customModelRates", out var rawRates)
            && rawRates.ValueKind == JsonValueKind.Array)
        {
            var rawEntries = rawRates.EnumerateArray().ToArray();
            settings = settings with
            {
                CustomModelRates = rates.Select((rate, index) =>
                    rate is not null
                    && index < rawEntries.Length
                    && rawEntries[index].ValueKind == JsonValueKind.Object
                    && !TryGetProperty(rawEntries[index], "matchMode", out _)
                        ? rate with { MatchMode = "prefix" }
                        : rate)
                    .OfType<ModelCreditRate>()
                    .ToArray()
            };
        }

        // Before subscription prices were kept per runtime, one field held the manual
        // fallback for both. It defaulted to 200 — a ChatGPT price — so whatever a user
        // put there described Codex, and that is where their saved value belongs.
        // Without this the value silently reverts to the default on first load.
        if (!TryGetProperty(document.RootElement, "codexMonthlySubscriptionAmount", out _)
            && TryGetProperty(document.RootElement, "monthlySubscriptionAmount", out var legacyAmount)
            && legacyAmount.ValueKind == JsonValueKind.Number
            && legacyAmount.TryGetDouble(out var legacyValue))
        {
            settings = settings with { CodexMonthlySubscriptionAmount = legacyValue };
        }

        // The auto-detect flag was shared too. It expressed one preference about both
        // runtimes, so it seeds both — unlike the amount, which only ever described one.
        if (!TryGetProperty(document.RootElement, "codexAutoDetectSubscriptionAmount", out _)
            && TryGetProperty(document.RootElement, "autoDetectSubscriptionAmount", out var legacyAuto)
            && legacyAuto.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            settings = settings with
            {
                CodexAutoDetectSubscriptionAmount = legacyAuto.GetBoolean(),
                ClaudeAutoDetectSubscriptionAmount = legacyAuto.GetBoolean()
            };
        }

        return settings.Validate().Normalize();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private void PreserveUnreadablePrimary()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            var fileName = $"settings.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json";
            File.Copy(_settingsPath, Path.Combine(directory, fileName), overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Loading defaults is still safer than preventing the application from opening.
        }
    }

    private void TryRepairPrimaryFromBackup()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            File.Copy(_backupPath, _settingsPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The backup was already validated and remains authoritative for this load.
            // Repairing the primary file is best effort and can be retried next startup.
        }
    }

    private static bool IsRecoverableReadFailure(Exception exception) =>
        exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException;
}
