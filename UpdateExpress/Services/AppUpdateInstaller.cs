using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateExpress.Services;

/// <summary>
/// Phase 2a: download a published package to temp, verify SHA-256, then hand off to the
/// detached maintenance helper. The verified package stays under the temp update root until
/// the helper copies it over UpdateExpress.exe; the helper itself is staged under the
/// install directory when possible (App Control / WDAC often blocks unsigned Temp launches).
/// </summary>
internal static class AppUpdateInstaller
{
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UpdateExpress/" + ProductVersion.Display);
        return client;
    }

    internal static async Task PrepareAndLaunchAsync(
        AppUpdateCheckResult offer,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (offer.Status != AppUpdateStatus.UpdateAvailable)
            throw new InvalidOperationException("No Update Express update is available to install.");
        if (string.IsNullOrWhiteSpace(offer.DownloadUrl))
            throw new InvalidOperationException("Update channel did not provide an HTTPS download URL.");
        if (string.IsNullOrWhiteSpace(offer.Sha256))
            throw new InvalidOperationException("Update channel did not provide a SHA-256 checksum. Installation aborted.");
        if (string.IsNullOrWhiteSpace(offer.AvailableVersion))
            throw new InvalidOperationException("Update channel did not provide an available version.");

        progress?.Invoke("Downloading Update Express " + offer.AvailableVersion + "…");
        string packagePath = await DownloadToTempAsync(offer.DownloadUrl!, cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Invoke("Verifying SHA-256…");
            VerifySha256(packagePath, offer.Sha256!);
            progress?.Invoke("Verifying package version…");
            VerifyPackageVersion(packagePath, offer.InstalledVersion, offer.AvailableVersion!);
        }
        catch
        {
            TryDeleteTree(Path.GetDirectoryName(packagePath));
            throw;
        }

        // From here the maintenance helper owns the package path; do not delete on launch errors.
        progress?.Invoke("Preparing replacement…");
        AgentLog.Write($"App update package verified: path={packagePath}; version={offer.AvailableVersion}; sha256={offer.Sha256}");
        InstallationManager.LaunchSelfUpdate(packagePath);
    }

    private static async Task<string> DownloadToTempAsync(string url, CancellationToken cancellationToken)
    {
        string dir = Path.Combine(Path.GetTempPath(), "MicroSystems-UpdateExpress-Update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "UpdateExpress-update.exe");
        using var response = await DownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Download failed (HTTP {(int)response.StatusCode}). {Trim(body)}");
        }

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }

        var info = new FileInfo(path);
        if (info.Length < 1024 * 1024)
            throw new InvalidOperationException($"Downloaded package is unexpectedly small ({info.Length} bytes).");
        return path;
    }

    internal static void VerifySha256(string filePath, string expectedHex)
    {
        string normalizedExpected = NormalizeHex(expectedHex);
        if (normalizedExpected.Length != 64)
            throw new InvalidOperationException("Update channel SHA-256 is invalid.");

        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        string actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, normalizedExpected, StringComparison.Ordinal))
            throw new InvalidOperationException("SHA-256 mismatch. The downloaded package was rejected.");
    }

    internal static void VerifyPackageVersion(string packagePath, string installedVersion, string expectedVersion)
    {
        var info = FileVersionInfo.GetVersionInfo(packagePath);
        string packageVersion = (info.ProductVersion ?? info.FileVersion ?? "").Split('+')[0].Trim();
        if (string.IsNullOrWhiteSpace(packageVersion))
            throw new InvalidOperationException("Downloaded package has no product version metadata.");

        if (AppUpdateChecker.CompareVersions(packageVersion, installedVersion) <= 0)
            throw new InvalidOperationException(
                $"Downloaded package version {packageVersion} is not newer than installed {installedVersion}.");

        if (AppUpdateChecker.CompareVersions(packageVersion, expectedVersion) < 0)
            throw new InvalidOperationException(
                $"Downloaded package version {packageVersion} is older than channel version {expectedVersion}.");
    }

    private static string NormalizeHex(string value)
    {
        var chars = value.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (chars.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            chars = chars[2..];
        return chars.ToLowerInvariant();
    }

    private static void TryDeleteTree(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception ex) { AgentLog.Write("Could not delete temp update folder: " + ex.Message); }
    }

    private static string Trim(string value)
    {
        value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return value.Length <= 180 ? value : value[..180] + "…";
    }
}
