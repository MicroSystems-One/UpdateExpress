using System.IO;
using System.Net.Http;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// In-app OEM package / readme download (HttpClient). Avoids opening the default browser.
/// </summary>
internal static class OemPackageDownload
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UpdateExpress/" + ProductVersion.Display);
        return client;
    }

    internal static string DownloadsRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "UpdateExpress-OEM");

    internal static async Task<string> DownloadAsync(
        string url,
        string? preferredFileName = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Download URL is empty.", nameof(url));
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("OEM download URL must be http(s).");

        Directory.CreateDirectory(DownloadsRoot);
        string fileName = PreferFileName(preferredFileName, uri);
        string path = UniquePath(Path.Combine(DownloadsRoot, fileName));

        progress?.Invoke("Downloading " + Path.GetFileName(path) + "…");
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Download failed (HTTP {(int)response.StatusCode}). {Trim(body)}");
        }

        // Content-Disposition filename when the URL path is a generic CDN token.
        string? headerName = TryContentDispositionFileName(response);
        if (!string.IsNullOrWhiteSpace(headerName) &&
            string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase) &&
            LooksGeneric(fileName))
        {
            path = UniquePath(Path.Combine(DownloadsRoot, SanitizeFileName(headerName)));
        }

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        var info = new FileInfo(path);
        if (info.Length <= 0)
            throw new InvalidOperationException("Downloaded file is empty.");
        progress?.Invoke($"Saved {Path.GetFileName(path)} ({FormatSize(info.Length)}).");
        return path;
    }

    private static string PreferFileName(string? preferred, Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
            return SanitizeFileName(preferred);
        string fromUrl = Path.GetFileName(uri.LocalPath);
        if (!string.IsNullOrWhiteSpace(fromUrl) && fromUrl.Contains('.', StringComparison.Ordinal))
            return SanitizeFileName(fromUrl);
        return "oem-package.bin";
    }

    private static bool LooksGeneric(string fileName) =>
        fileName.Equals("oem-package.bin", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("download", StringComparison.OrdinalIgnoreCase);

    private static string? TryContentDispositionFileName(HttpResponseMessage response)
    {
        try
        {
            if (response.Content.Headers.ContentDisposition?.FileNameStar is { Length: > 0 } star)
                return star.Trim('"');
            if (response.Content.Headers.ContentDisposition?.FileName is { Length: > 0 } name)
                return name.Trim('"');
        }
        catch { /* ignore malformed headers */ }
        return null;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? DownloadsRoot;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem}-{Guid.NewGuid():N}{ext}");
    }

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Trim().Trim('.');
        return string.IsNullOrWhiteSpace(name) ? "oem-package.bin" : name;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    private static string Trim(string text)
    {
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}
