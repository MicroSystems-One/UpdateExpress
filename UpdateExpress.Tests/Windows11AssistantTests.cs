using System.Diagnostics;
using System.IO;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public class Windows11AssistantTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "UpdateExpress.Tests-" + Guid.NewGuid().ToString("N"));

    public Windows11AssistantTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>The .NET host that runs the tests: an embedded Microsoft signature on every machine that can build.</summary>
    private static string MicrosoftSignedFile()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        string root = !string.IsNullOrEmpty(configured) && File.Exists(Path.Combine(configured, "dotnet.exe"))
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        string path = Path.Combine(root, "dotnet.exe");
        Assert.True(File.Exists(path), "dotnet.exe not found at " + path);
        return path;
    }

    private string CopyOfSignedFile(string name = "signed.exe")
    {
        string copy = Path.Combine(_dir, name);
        File.Copy(MicrosoftSignedFile(), copy);
        return copy;
    }

    [Theory]
    [InlineData("https://download.microsoft.com/download/e4bc63b3/Windows11InstallationAssistant.exe", true)]
    [InlineData("https://go.microsoft.com/fwlink/?linkid=2171764", true)]
    [InlineData("https://software.download.prss.microsoft.com/x/y.exe", true)]
    [InlineData("https://microsoft.com/x.exe", true)]
    [InlineData("https://DOWNLOAD.MICROSOFT.COM/x.exe", true)]
    [InlineData("http://download.microsoft.com/x.exe", false)]            // not HTTPS
    [InlineData("https://download.microsoft.com.evil.example/x.exe", false)]
    [InlineData("https://evilmicrosoft.com/x.exe", false)]
    [InlineData("https://microsoft.com.evil.example/x.exe", false)]
    [InlineData("https://example.com/?u=https://download.microsoft.com/x.exe", false)]
    [InlineData("https://download.microsoft.com@evil.example/x.exe", false)]
    [InlineData("file:///C:/x.exe", false)]
    public void OnlyHttpsOnMicrosoftCom_IsAcceptedAsDownloadSource(string url, bool expected)
    {
        Assert.Equal(expected, Windows11Assistant.IsMicrosoftDownloadUri(new Uri(url)));
    }

    [Fact]
    public void NoAddress_IsNotAccepted()
    {
        Assert.False(Windows11Assistant.IsMicrosoftDownloadUri(null));
        Assert.False(Windows11Assistant.IsMicrosoftDownloadUri(new Uri("x.exe", UriKind.Relative)));
    }

    [Fact]
    public void TheDownloadLink_IsMicrosoftsOwn()
    {
        Assert.True(Windows11Assistant.IsMicrosoftDownloadUri(new Uri(Windows11Assistant.DownloadUrl)));
        Assert.True(Windows11Assistant.IsMicrosoftDownloadUri(new Uri(Windows11Assistant.DownloadPageUrl)));
    }

    [Fact]
    public void MicrosoftSignedFile_IsRecognised()
    {
        var signature = AuthenticodeSignature.Read(MicrosoftSignedFile());

        Assert.True(signature.Trusted, signature.Describe());
        Assert.Equal("Microsoft Corporation", signature.SignerOrganization);
        Assert.StartsWith("Microsoft Root Certificate Authority", signature.RootName);
        Assert.True(signature.IsMicrosoft, signature.Describe());
    }

    [Fact]
    public void UnsignedFile_IsNotTrusted()
    {
        string path = Path.Combine(_dir, "unsigned.exe");
        File.WriteAllBytes(path, new byte[4096]);

        var signature = AuthenticodeSignature.Read(path);

        Assert.False(signature.Trusted);
        Assert.False(signature.IsMicrosoft);
        Assert.NotEqual(0, signature.TrustError);
    }

    [Fact]
    public void SignedFileChangedAfterSigning_IsNotTrusted()
    {
        string path = CopyOfSignedFile("tampered.exe");
        byte[] bytes = File.ReadAllBytes(path);
        // Inside the code, well before the signature at the end of the file.
        int at = bytes.Length / 2;
        bytes[at] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        var signature = AuthenticodeSignature.Read(path);

        Assert.False(signature.Trusted);
        Assert.False(signature.IsMicrosoft);
    }

    [Fact]
    public void MissingFile_IsNotTrusted()
    {
        Assert.False(AuthenticodeSignature.Read(Path.Combine(_dir, "nothing-here.exe")).IsMicrosoft);
    }

    [Theory]
    [InlineData(true, "Microsoft Corporation", "Microsoft Root Certificate Authority 2011", "Microsoft Corporation", true)]
    [InlineData(true, "Microsoft Corporation", "Microsoft Root Certificate Authority 2010", "Microsoft Corporation", true)]
    [InlineData(false, "Microsoft Corporation", "Microsoft Root Certificate Authority 2011", "Microsoft Corporation", false)]
    [InlineData(true, "Microsoft Corporation", "DigiCert Trusted Root G4", "DigiCert Inc", false)]    // the name alone is not enough
    [InlineData(true, "Microsoft Corporation", "Microsoft Root Certificate Authority 2011", "Someone Else", false)]
    [InlineData(true, "Contoso Ltd", "Microsoft Root Certificate Authority 2011", "Microsoft Corporation", false)]
    [InlineData(true, "microsoft corporation", "Microsoft Root Certificate Authority 2011", "Microsoft Corporation", false)]
    [InlineData(true, "Microsoft Corporation", "", "", false)]                                          // chain without a root
    public void IsMicrosoft_NeedsTrust_MicrosoftAsSigner_AndAMicrosoftRoot(bool trusted, string signerOrganization, string rootName, string rootOrganization, bool expected)
    {
        var signature = new AuthenticodeSignature(trusted, 0, "Signer", signerOrganization, rootName, rootOrganization);

        Assert.Equal(expected, signature.IsMicrosoft);
    }

    [Fact]
    public void VerifyWithoutStart_ReturnsTheSigner_AndStartsNothing()
    {
        string path = CopyOfSignedFile();

        var outcome = Windows11Assistant.VerifyAndStart(path, "https://download.microsoft.com/x", 123, start: false);

        Assert.False(outcome.Started);
        Assert.Equal(Path.GetFileName(path), Path.GetFileName(outcome.FilePath));
        Assert.True(File.Exists(outcome.FilePath));
        Assert.False(string.IsNullOrWhiteSpace(outcome.Signer));
    }

    [Fact]
    public void FileReachedThroughALinkedFolder_IsCheckedUnderItsRealPath()
    {
        string realDir = Path.Combine(_dir, "real");
        Directory.CreateDirectory(realDir);
        string realFile = Path.Combine(realDir, "signed.exe");
        File.Copy(MicrosoftSignedFile(), realFile);
        string link = Path.Combine(_dir, "link");
        // A junction needs no special rights, unlike a symbolic link.
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{realDir}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit(30000);
            Assert.Equal(0, mklink.ExitCode);
        }

        try
        {
            var outcome = Windows11Assistant.VerifyAndStart(Path.Combine(link, "signed.exe"), "https://download.microsoft.com/x", 123, start: false);

            Assert.DoesNotContain(@"\link\", outcome.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(@"\real\signed.exe", outcome.FilePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Removes the junction only; the folder it points to is left to Dispose.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void FileNotSignedByMicrosoft_IsRefused_BeforeAnythingIsStarted()
    {
        string path = Path.Combine(_dir, "unsigned.exe");
        File.WriteAllBytes(path, new byte[4096]);
        var steps = new List<string>();

        var error = Assert.Throws<InvalidOperationException>(() =>
            Windows11Assistant.VerifyAndStart(path, "https://download.microsoft.com/x", 4096, start: true, steps.Add));

        Assert.Contains("not signed by Microsoft", error.Message);
        Assert.DoesNotContain("start", steps);
    }

    /// <summary>
    /// What the check-then-start relies on: while the file is held open the way
    /// <see cref="Windows11Assistant.VerifyAndStart"/> holds it, it can be verified and run
    /// but not replaced, changed or deleted.
    /// </summary>
    [Fact]
    public void FileHeldOpenForTheCheck_CanBeVerifiedAndRun_ButNotReplaced()
    {
        string path = CopyOfSignedFile("held.exe");
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.True(AuthenticodeSignature.Read(path).IsMicrosoft);
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(path, new byte[16]));
        Assert.ThrowsAny<IOException>(() => File.Delete(path));
        Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
        Assert.ThrowsAny<IOException>(() => Directory.Move(_dir, _dir + "-moved"));

        using var process = Process.Start(new ProcessStartInfo(path, "--version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30000), "The held file did not run to the end.");
    }

    [Fact]
    public void TestSwitch_IsOnOnlyForTheValueOne()
    {
        string? before = Environment.GetEnvironmentVariable(Windows11Assistant.TestSwitchVariable);
        try
        {
            foreach (var (value, expected) in new (string?, bool)[] { (null, false), ("", false), ("0", false), ("true", false), ("1", true), (" 1 ", true) })
            {
                Environment.SetEnvironmentVariable(Windows11Assistant.TestSwitchVariable, value);
                Assert.Equal(expected, Windows11Assistant.TestSwitchOn);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(Windows11Assistant.TestSwitchVariable, before);
        }
    }
}
