using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodexU.Core;

namespace CodexU.Infrastructure;

public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    public const string Repository = "yuweiyang9611/CodexHelperProject";
    public const string ReleasesPage = "https://github.com/yuweiyang9611/CodexHelperProject/releases";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly string _cachePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubUpdateService(HttpClient? client = null, string? applicationDataDirectory = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("codexU-Windows/1.0");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        var token = Environment.GetEnvironmentVariable("CODEXU_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token) && _client.DefaultRequestHeaders.Authorization is null)
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        var directory = applicationDataDirectory
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "codexU");
        _cachePath = Path.Combine(directory, "update-check.json");
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        bool includePrereleases,
        bool force,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!force)
            {
                var cached = await TryReadCacheAsync(currentVersion, includePrereleases, cancellationToken);
                if (cached is not null)
                {
                    return cached;
                }
            }

            var requestUri = includePrereleases
                ? $"https://api.github.com/repos/{Repository}/releases?per_page=20"
                : $"https://api.github.com/repos/{Repository}/releases/latest";
            using var response = await _client.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return await SaveAsync(new UpdateCheckResult(
                    currentVersion,
                    null,
                    false,
                    false,
                    null,
                    ReleasesPage,
                    null,
                    DateTimeOffset.Now,
                    "无法读取私有 GitHub Release；可设置 CODEXU_GITHUB_TOKEN，或直接打开发布页。"), includePrereleases, cancellationToken);
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var release = includePrereleases
                ? document.RootElement.EnumerateArray().FirstOrDefault(IsUsableRelease)
                : document.RootElement;
            if (release.ValueKind != JsonValueKind.Object || !IsUsableRelease(release))
            {
                return await SaveAsync(new UpdateCheckResult(
                    currentVersion,
                    null,
                    false,
                    false,
                    null,
                    ReleasesPage,
                    null,
                    DateTimeOffset.Now,
                    "暂未发现可用 Release。"), includePrereleases, cancellationToken);
            }

            var tag = GetString(release, "tag_name")?.TrimStart('v', 'V');
            var isPrerelease = GetBoolean(release, "prerelease");
            var isAvailable = IsNewerVersion(tag, currentVersion);
            var result = new UpdateCheckResult(
                currentVersion,
                tag,
                isAvailable,
                isPrerelease,
                GetString(release, "name") ?? (tag is null ? null : $"codexU v{tag}"),
                GetString(release, "html_url") ?? ReleasesPage,
                GetDate(release, "published_at"),
                DateTimeOffset.Now,
                isAvailable ? $"发现新版本 v{tag}" : "当前已是最新版本",
                LimitNotes(GetString(release, "body")));
            return await SaveAsync(result, includePrereleases, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return new UpdateCheckResult(
                currentVersion,
                null,
                false,
                false,
                null,
                ReleasesPage,
                null,
                DateTimeOffset.Now,
                $"更新检查失败：{exception.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateCheckResult?> TryReadCacheAsync(
        string currentVersion,
        bool includePrereleases,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cachePath))
            {
                return null;
            }

            await using var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var cache = await JsonSerializer.DeserializeAsync<UpdateCache>(stream, JsonOptions, cancellationToken);
            if (cache is null
                || cache.IncludePrereleases != includePrereleases
                || DateTimeOffset.Now - cache.Result.CheckedAt >= TimeSpan.FromDays(1))
            {
                return null;
            }

            return RecalculateAvailability(cache.Result, currentVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task<UpdateCheckResult> SaveAsync(
        UpdateCheckResult result,
        bool includePrereleases,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(_cachePath)!;
            Directory.CreateDirectory(directory);
            var temporary = _cachePath + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new UpdateCache(includePrereleases, result),
                    JsonOptions,
                    cancellationToken);
            }
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed cache write must not turn a successful update check into an error.
        }

        return result;
    }

    private static UpdateCheckResult RecalculateAvailability(UpdateCheckResult result, string currentVersion)
    {
        var available = IsNewerVersion(result.LatestVersion, currentVersion);
        return result with
        {
            CurrentVersion = currentVersion,
            IsUpdateAvailable = available,
            Status = result.LatestVersion is null
                ? result.Status
                : available ? $"发现新版本 v{result.LatestVersion}" : "当前已是最新版本"
        };
    }

    private static bool IsUsableRelease(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && !GetBoolean(value, "draft")
        && !string.IsNullOrWhiteSpace(GetString(value, "tag_name"));

    private static bool IsNewerVersion(string? candidate, string? current) =>
        TryParseReleaseVersion(candidate, out var candidateVersion)
        && TryParseReleaseVersion(current, out var currentVersion)
        && candidateVersion.CompareTo(currentVersion) > 0;

    private static bool TryParseReleaseVersion(string? value, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().TrimStart('v', 'V').Split('-', 2);
        if (!Version.TryParse(parts[0], out var core))
        {
            return false;
        }

        version = new ReleaseVersion(core, parts.Length == 2 ? parts[1] : null);
        return true;
    }

    private static string? GetString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool GetBoolean(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetDate(JsonElement value, string name) =>
        DateTimeOffset.TryParse(GetString(value, name), out var parsed) ? parsed : null;

    private static string? LimitNotes(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(value.Trim().Length, 2_000)];

    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private readonly record struct ReleaseVersion(Version Core, string? Prerelease) : IComparable<ReleaseVersion>
    {
        public int CompareTo(ReleaseVersion other)
        {
            var coreComparison = Core.CompareTo(other.Core);
            if (coreComparison != 0) return coreComparison;
            if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
            if (other.Prerelease is null) return -1;

            var left = Prerelease.Split('.');
            var right = other.Prerelease.Split('.');
            for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
            {
                if (index >= left.Length) return -1;
                if (index >= right.Length) return 1;
                var leftNumeric = int.TryParse(left[index], out var leftNumber);
                var rightNumeric = int.TryParse(right[index], out var rightNumber);
                var comparison = leftNumeric && rightNumeric
                    ? leftNumber.CompareTo(rightNumber)
                    : leftNumeric ? -1
                    : rightNumeric ? 1
                    : string.Compare(left[index], right[index], StringComparison.OrdinalIgnoreCase);
                if (comparison != 0) return comparison;
            }
            return 0;
        }
    }

    private sealed record UpdateCache(bool IncludePrereleases, UpdateCheckResult Result);
}
