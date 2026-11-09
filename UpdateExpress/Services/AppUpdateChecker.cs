using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateExpress.Services;

internal enum AppUpdateStatus
{
    UpToDate,
    UpdateAvailable,
    CheckFailed
}

internal sealed class AppUpdateManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("publishedAt")]
    public string PublishedAt { get; set; } = "";

    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; set; } = "";

    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
}

internal sealed class AppUpdateCheckResult
{
    public AppUpdateStatus Status { get; init; }
    public string InstalledVersion { get; init; } = ProductVersion.Display;
    public string? AvailableVersion { get; init; }
    public string? ReleaseNotes { get; init; }
    public string? DownloadUrl { get; init; }
    public string? Sha256 { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Reads the public UpdateExpress-Releases channel. Phase 1 checks versions;
/// Phase 2a uses the same metadata for verified installation.
/// </summary>
internal static class AppUpdateChecker
{
    internal const string LatestJsonUrl =
        "https://raw.githubusercontent.com/BlackAdder5/UpdateExpress-Releases/main/latest.json";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UpdateExpress/" + ProductVersion.Display);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    internal static async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        string installed = ProductVersion.Display;
        try
        {
            using var response = await Http.GetAsync(LatestJsonUrl, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed(installed, $"HTTP {(int)response.StatusCode}: {Trim(body)}");
            }

            var manifest = JsonSerializer.Deserialize<AppUpdateManifest>(body);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                return Failed(installed, "Update channel returned invalid JSON (missing version).");

            string? sha256 = string.IsNullOrWhiteSpace(manifest.Sha256) ? null : manifest.Sha256.Trim();
            int comparison = CompareVersions(manifest.Version.Trim(), installed);
            if (comparison > 0)
            {
                return new AppUpdateCheckResult
                {
                    Status = AppUpdateStatus.UpdateAvailable,
                    InstalledVersion = installed,
                    AvailableVersion = manifest.Version.Trim(),
                    ReleaseNotes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? null : manifest.ReleaseNotes.Trim(),
                    DownloadUrl = NormalizeHttpsUrl(manifest.DownloadUrl),
                    Sha256 = sha256
                };
            }

            return new AppUpdateCheckResult
            {
                Status = AppUpdateStatus.UpToDate,
                InstalledVersion = installed,
                AvailableVersion = manifest.Version.Trim(),
                ReleaseNotes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? null : manifest.ReleaseNotes.Trim(),
                DownloadUrl = NormalizeHttpsUrl(manifest.DownloadUrl),
                Sha256 = sha256
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(installed, ex.Message);
        }
    }

    internal static bool TryOpenDownload(string? downloadUrl, out string? error)
    {
        error = null;
        string? url = NormalizeHttpsUrl(downloadUrl);
        if (url is null)
        {
            error = "No HTTPS download URL was provided by the update channel.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static AppUpdateCheckResult Failed(string installed, string error) => new()
    {
        Status = AppUpdateStatus.CheckFailed,
        InstalledVersion = installed,
        Error = error
    };

    private static string? NormalizeHttpsUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return null;
        return uri.AbsoluteUri;
    }

    private static string Trim(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 180 ? value : value[..180] + "…";
    }

    /// <summary>
    /// Compares dotted numeric versions with any segment count (e.g. 0.7.2.6.35).
    /// Non-numeric suffixes on a segment are ignored after the numeric prefix.
    /// </summary>
    internal static int CompareVersions(string left, string right)
    {
        var a = ParseSegments(left);
        var b = ParseSegments(right);
        int len = Math.Max(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            int av = i < a.Length ? a[i] : 0;
            int bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        return 0;
    }

    private static int[] ParseSegments(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return new[] { 0 };
        string cleaned = version.Trim();
        int plus = cleaned.IndexOf('+');
        if (plus >= 0) cleaned = cleaned[..plus];
        var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            int end = 0;
            while (end < part.Length && char.IsDigit(part[end])) end++;
            numbers[i] = end == 0 ? 0 : int.Parse(part[..end]);
        }
        return numbers;
    }
}
