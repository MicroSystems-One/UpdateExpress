using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace UpdateExpress.Services;

/// <summary>
/// Fetches Microsoft's Windows 11 Installation Assistant from Microsoft's own download link,
/// checks that the file is signed by Microsoft, and starts it. Update Express does not upgrade
/// Windows itself: the Assistant asks the user before it does anything.
/// </summary>
internal static class Windows11Assistant
{
    /// <summary>The link behind "Download Now" for the Installation Assistant on Microsoft's Windows 11 download page.</summary>
    internal const string DownloadUrl = "https://go.microsoft.com/fwlink/?linkid=2171764";
    internal const string DownloadPageUrl = "https://www.microsoft.com/software-download/windows11";
    internal const string FileName = "Windows11InstallationAssistant.exe";

    /// <summary>
    /// Test switch. Set this environment variable to 1 and the button is shown on any PC,
    /// marked as a test, and a click downloads and verifies the Assistant without starting it.
    /// </summary>
    internal const string TestSwitchVariable = "UE_TEST_WIN11_ASSISTANT";

    internal static bool TestSwitchOn =>
        string.Equals(Environment.GetEnvironmentVariable(TestSwitchVariable)?.Trim(), "1", StringComparison.Ordinal);

    private const long MinBytes = 1024 * 1024;
    private const long MaxBytes = 200L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UpdateExpress/" + ProductVersion.Display);
        return client;
    }

    internal sealed record Outcome(string FilePath, string DownloadedFrom, long Bytes, string Signer, bool Started);

    /// <summary>
    /// Download, verify, and start the Assistant unless <paramref name="start"/> is false.
    /// Throws with a message for the user when a step fails; nothing is started in that case.
    /// </summary>
    internal static async Task<Outcome> RunAsync(bool start, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Invoke("download");
        var (path, source, bytes) = await DownloadAsync(cancellationToken).ConfigureAwait(false);
        Outcome outcome;
        try
        {
            progress?.Invoke("verify");
            outcome = VerifyAndStart(path, source, bytes, start, progress);
        }
        catch
        {
            TryDeleteTree(Path.GetDirectoryName(path));
            throw;
        }
        // A file that was only checked has no further use. A started one is in use and stays.
        if (!start) TryDeleteTree(Path.GetDirectoryName(path));
        return outcome;
    }

    /// <summary>
    /// The file is held open without write or delete sharing from before the check until
    /// after the start, so the file that is started is the file that was checked.
    /// </summary>
    internal static Outcome VerifyAndStart(string path, string source, long bytes, bool start, Action<string>? progress = null)
    {
        using var frozen = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        // Checked and started under the name Windows itself gives the open file. A folder on
        // the way that is really a link could be pointed elsewhere between the two steps; the
        // real folders cannot be renamed or replaced while the file in them is open.
        string real = RealPath(frozen);
        var signature = AuthenticodeSignature.Read(real);
        if (!signature.IsMicrosoft)
            throw new InvalidOperationException("The downloaded file is not signed by Microsoft. " + signature.Describe());

        if (start)
        {
            progress?.Invoke("start");
            StartFile(real);
        }
        return new Outcome(real, source, bytes, signature.SignerName, start);
    }

    /// <summary>The full path of an open file with every link and short name resolved.</summary>
    internal static string RealPath(FileStream file)
    {
        var buffer = new char[1024];
        uint length = GetFinalPathNameByHandleW(file.SafeFileHandle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            throw new IOException("Could not resolve the path of the downloaded file.");
        string real = new(buffer, 0, (int)length);
        if (real.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + real[8..];
        return real.StartsWith(@"\\?\", StringComparison.Ordinal) ? real[4..] : real;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    /// <summary>The Assistant's own manifest asks for administrator rights; Windows handles that.</summary>
    internal static void StartFile(string path)
    {
        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
        });
    }

    /// <summary>Only an HTTPS address on microsoft.com is accepted as the place the file came from.</summary>
    internal static bool IsMicrosoftDownloadUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        string host = uri.IdnHost.TrimEnd('.');
        return host.Equals("microsoft.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string DownloadRoot => Path.Combine(Path.GetTempPath(), "MicroSystems-UpdateExpress-Win11Assistant");

    private static async Task<(string Path, string Source, long Bytes)> DownloadAsync(CancellationToken cancellationToken)
    {
        RemoveEarlierDownloads();
        using var response = await Http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        Uri? source = response.RequestMessage?.RequestUri;
        if (!IsMicrosoftDownloadUri(source))
            throw new InvalidOperationException("The download did not come from a Microsoft address: " + (source?.Host ?? "unknown"));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Download failed (HTTP {(int)response.StatusCode}) from {source!.Host}.");
        if (response.Content.Headers.ContentLength is long announced && announced > MaxBytes)
            throw new InvalidOperationException($"The download is unexpectedly large ({announced} bytes).");

        string dir = Path.Combine(DownloadRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileName);
        long total = 0;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                    throw new InvalidOperationException("The download is unexpectedly large.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDeleteTree(dir);
            throw;
        }

        if (total < MinBytes)
        {
            TryDeleteTree(dir);
            throw new InvalidOperationException($"The download is unexpectedly small ({total} bytes).");
        }
        return (path, source!.GetLeftPart(UriPartial.Path), total);
    }

    /// <summary>Best effort: a copy that is still running cannot be removed and is left alone.</summary>
    private static void RemoveEarlierDownloads()
    {
        try
        {
            if (!Directory.Exists(DownloadRoot)) return;
            foreach (string dir in Directory.GetDirectories(DownloadRoot))
                TryDeleteTree(dir);
        }
        catch { }
    }

    private static void TryDeleteTree(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch { }
        // The folder that holds the downloads goes too once it is empty.
        try { if (Directory.Exists(DownloadRoot)) Directory.Delete(DownloadRoot, false); }
        catch { }
    }
}
