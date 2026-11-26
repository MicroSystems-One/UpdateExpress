using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace UpdateExpress.Services.OemDrivers;

/// <summary>
/// In-app OEM package / readme download (HttpClient). Avoids opening the default browser.
/// Rejects HTML support pages so they are never treated as installers.
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
        bool allowTextReadme = false,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("Download URL is empty.", nameof(url));
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("OEM download URL must be http(s).");

        if (!allowTextReadme && LooksLikeSupportPage(uri))
            throw new InvalidOperationException(
                "URL points to a support/details page, not a package file. Open it in the browser instead.");

        Directory.CreateDirectory(DownloadsRoot);
        string fileName = PreferFileName(preferredFileName, uri, allowTextReadme);
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

        var media = response.Content.Headers.ContentType;
        if (!allowTextReadme && IsHtmlContentType(media))
            throw new InvalidOperationException(
                "Server returned an HTML page instead of a package. Open the link in the browser instead.");

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
        {
            TryDelete(path);
            throw new InvalidOperationException("Downloaded file is empty.");
        }

        if (!allowTextReadme && LooksLikeHtmlFile(path))
        {
            TryDelete(path);
            throw new InvalidOperationException(
                "Downloaded content is an HTML support page, not a package. Open the link in the browser instead.");
        }

        if (!allowTextReadme && !LooksLikePackagePath(path) && LooksGeneric(Path.GetFileName(path)))
        {
            // Generic name + no package extension after Content-Disposition — still suspicious.
            if (IsHtmlContentType(media) || LooksLikeHtmlFile(path))
            {
                TryDelete(path);
                throw new InvalidOperationException(
                    "Downloaded content does not look like an OEM package. Open the link in the browser instead.");
            }
        }

        progress?.Invoke($"Saved {Path.GetFileName(path)} ({FormatSize(info.Length)}).");
        return path;
    }

    internal static bool LooksLikeSupportPage(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && LooksLikeSupportPage(uri);

    internal static bool LooksLikeSupportPage(Uri uri)
    {
        string host = uri.Host ?? "";
        string path = uri.AbsolutePath ?? "";
        if (host.Contains("dell.com", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("driversdetails", StringComparison.OrdinalIgnoreCase))
            return true;
        if (host.Contains("support.hp.com", StringComparison.OrdinalIgnoreCase) &&
            !LooksLikePackagePath(path))
            return path.Contains("/drivers", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/product", StringComparison.OrdinalIgnoreCase);
        if (host.Contains("pcsupport.lenovo.com", StringComparison.OrdinalIgnoreCase) &&
            !LooksLikePackagePath(path) &&
            (path.Contains("/help", StringComparison.OrdinalIgnoreCase) ||
             path.Contains("/detail", StringComparison.OrdinalIgnoreCase)))
            return true;
        // Bare support HTML with no package-looking path segment.
        if (!LooksLikePackagePath(path) &&
            (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private static bool IsHtmlContentType(MediaTypeHeaderValue? media)
    {
        if (media?.MediaType is not { Length: > 0 } type) return false;
        return type.Contains("text/html", StringComparison.OrdinalIgnoreCase) ||
               type.Contains("application/xhtml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHtmlFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[512];
            int read = stream.Read(buffer);
            if (read <= 0) return false;
            string head = System.Text.Encoding.UTF8.GetString(buffer[..read]).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                   head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                   head.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
                   head.Contains("<html", StringComparison.OrdinalIgnoreCase) &&
                   head.Contains("<body", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikePackagePath(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    private static string PreferFileName(string? preferred, Uri uri, bool allowTextReadme)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
            return SanitizeFileName(preferred);
        string fromUrl = Path.GetFileName(uri.LocalPath);
        if (!string.IsNullOrWhiteSpace(fromUrl) && fromUrl.Contains('.', StringComparison.Ordinal))
            return SanitizeFileName(fromUrl);
        return allowTextReadme ? "oem-readme.txt" : "oem-package.bin";
    }

    private static bool LooksGeneric(string fileName) =>
        fileName.Equals("oem-package.bin", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("oem-readme.txt", StringComparison.OrdinalIgnoreCase) ||
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
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
