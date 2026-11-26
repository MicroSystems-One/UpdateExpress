using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

/// <summary>
/// The N-able install script is never run here: it installs. Its functions are taken out of
/// the file and called on their own, and the place where the script uses them is checked in
/// its syntax tree.
/// </summary>
public class NableInstallScriptTests : IDisposable
{
    private const string ScriptsCopy = @"scripts\Install-UpdateExpress-Nable.ps1";
    private const string PublishedCopy = @"Publisert\Install-UpdateExpress-Nable.ps1";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "ue-nable-tests-" + Guid.NewGuid().ToString("N"));

    public NableInstallScriptTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { }
    }

    /// <summary>
    /// Windows PowerShell 5.1, the only one a Windows Server ships with, reads a script without
    /// a byte-order mark in the Windows code page. There the last byte of a UTF-8 arrow or dash
    /// is a quote character, which ended a string early and stopped the whole script from parsing.
    /// </summary>
    [Theory]
    [InlineData(ScriptsCopy)]
    [InlineData(PublishedCopy)]
    public void Script_IsPlainAscii_AndParsesInWindowsPowerShell(string script)
    {
        byte[] bytes = File.ReadAllBytes(PathOf(script));
        int firstNonAscii = Array.FindIndex(bytes, b => b >= 128);
        Assert.True(
            firstNonAscii < 0,
            $"{script} has a non-ASCII byte in line {bytes.Take(Math.Max(firstNonAscii, 0)).Count(b => b == '\n') + 1}.");

        string output = Run($$"""
            $tokens = $null; $errors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile('{{PathOf(script)}}', [ref]$tokens, [ref]$errors)
            'errors=' + $errors.Count
            $errors | ForEach-Object { 'line ' + $_.Extent.StartLineNumber + ': ' + $_.Message }
            'version=' + $PSVersionTable.PSVersion.Major
            """);

        Assert.Contains("errors=0", output);
        Assert.Contains("version=5", output);
    }

    [Theory]
    [InlineData(ScriptsCopy)]
    [InlineData(PublishedCopy)]
    public void AutoReboot_LeftOut_IsOnOnlyOnAClient_AndWhatTheCallerPassedAlwaysWins(string script)
    {
        string output = RunWithFunctions(script, new[] { "Resolve-UeAutoReboot" }, """
            foreach ($type in 'WinNT', 'ServerNT', 'LanmanNT', '') {
                foreach ($explicit in $true, $false) {
                    foreach ($requested in $true, $false) {
                        '{0}|{1}|{2}={3}' -f $type, $explicit, $requested, (Resolve-UeAutoReboot -Explicit $explicit -Requested $requested -ProductType $type)
                    }
                }
            }
            """);

        var results = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('='))
            .ToDictionary(parts => parts[0], parts => bool.Parse(parts[1]));

        Assert.Equal(16, results.Count);
        foreach (string type in new[] { "WinNT", "ServerNT", "LanmanNT", "" })
        {
            // Passed by the caller: taken as passed, on any Windows.
            Assert.True(results[$"{type}|True|True"], type);
            Assert.False(results[$"{type}|True|False"], type);
            // Left out: the script's own default.
            bool client = type == "WinNT";
            Assert.Equal(client, results[$"{type}|False|True"]);
            Assert.Equal(client, results[$"{type}|False|False"]);
        }
    }

    [Theory]
    [InlineData(ScriptsCopy)]
    [InlineData(PublishedCopy)]
    public void ProductType_IsReadTheSameWayAsTheAppReadsIt(string script)
    {
        string output = RunWithFunctions(script, new[] { "Get-UeOsProductType" }, "Get-UeOsProductType").Trim();

        string expected = WindowsProductType.Read() switch
        {
            WindowsProductType.Workstation => "WinNT",
            WindowsProductType.DomainController => "LanmanNT",
            WindowsProductType.Server => "ServerNT",
            _ => ""
        };
        Assert.Equal(expected, output);
    }

    [Theory]
    [InlineData(ScriptsCopy)]
    [InlineData(PublishedCopy)]
    public void Script_DecidesAutoRebootBeforeItWritesAnyPolicy(string script)
    {
        string output = Run($$"""
            $ErrorActionPreference = 'Stop'
            $tokens = $null; $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{PathOf(script)}}', [ref]$tokens, [ref]$errors)
            if ($errors.Count -gt 0) { throw "The script does not parse: $($errors[0].Message)" }

            $parameter = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'AutoReboot' }
            'default=' + $parameter.DefaultValue.Extent.Text
            'type=' + $parameter.StaticType.Name

            # Top level only: not inside the functions.
            $assignments = @($ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $n.Left.Extent.Text -eq '$AutoReboot' }, $false))
            'assignments=' + $assignments.Count
            'assignment=' + ($assignments[0].Right.Extent.Text -replace '\s+', ' ')
            $explicit = @($ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $n.Left.Extent.Text -eq '$autoRebootExplicit' }, $false))
            'explicit=' + ($explicit[0].Right.Extent.Text -replace '\s+', ' ')
            $productType = @($ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                $n.Left.Extent.Text -eq '$osProductType' }, $false))
            'productType=' + ($productType[0].Right.Extent.Text -replace '\s+', ' ')

            $writes = @($ast.FindAll({ param($n)
                $n -is [System.Management.Automation.Language.CommandAst] -and
                $n.GetCommandName() -eq 'Write-UePolicyFiles' }, $false))
            'writes=' + $writes.Count
            'decidedFirst=' + ($assignments[0].Extent.StartOffset -lt ($writes | ForEach-Object { $_.Extent.StartOffset } | Measure-Object -Minimum).Minimum)
            """);

        var facts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);

        // On a client nothing changes: the switch still defaults to on.
        Assert.Equal("$true", facts["default"]);
        Assert.Equal("SwitchParameter", facts["type"]);
        Assert.Equal("1", facts["assignments"]);
        Assert.Equal(
            "Resolve-UeAutoReboot -Explicit $autoRebootExplicit -Requested ([bool]$AutoReboot) -ProductType $osProductType",
            facts["assignment"]);
        Assert.Equal("$PSBoundParameters.ContainsKey('AutoReboot')", facts["explicit"]);
        Assert.Equal("Get-UeOsProductType", facts["productType"]);
        Assert.True(int.Parse(facts["writes"]) >= 2, "The script should write the policy files.");
        Assert.Equal("True", facts["decidedFirst"]);
    }

    [Theory]
    [InlineData(ScriptsCopy, false)]
    [InlineData(ScriptsCopy, true)]
    [InlineData(PublishedCopy, false)]
    [InlineData(PublishedCopy, true)]
    public void PolicyFile_CarriesTheDecidedAutoReboot_AndTheAppReadsIt(string script, bool autoReboot)
    {
        string root = Path.Combine(_temp, "LocalAppData");
        Directory.CreateDirectory(root);
        RunWithFunctions(script, new[] { "Write-UeLog", "Write-UePolicyFiles" }, $"""
            $WindowsAutomation = $true
            $AutoReboot = ${autoReboot.ToString().ToLowerInvariant()}
            $RebootMode = 'outsideActiveHours'
            $ActiveHoursStart = 7
            $ActiveHoursEnd = 17
            $RebootCountdownSeconds = 300
            $InstallTime = '17:00'
            $InstallDaysMask = 127
            $SetupProfile = 'business'
            $Language = 'en'
            $Reminders = $true
            Write-UePolicyFiles -LocalAppDataRoot '{root}'
            """);

        var policy = JsonSerializer.Deserialize<UpdatePolicy>(
            File.ReadAllText(Path.Combine(root, "UpdateExpress", "update-policy.json")))!;
        policy.Normalize();

        Assert.Equal(autoReboot, policy.WindowsAutomation.AutoReboot.Enabled);
        Assert.Equal(!autoReboot, policy.WindowsAutomation.NeverAutoReboot);
        Assert.True(policy.SetupCompleted);
        Assert.True(policy.WindowsAutomation.Enabled);
        Assert.True(policy.WindowsAutomation.Defender.Enabled);
        Assert.Equal("autoInstall", policy.WindowsAutomation.Defender.Mode);
        Assert.Equal(60, policy.WindowsAutomation.Defender.IntervalMinutes);
        Assert.Equal("downloadAndPrompt", policy.WindowsAutomation.Critical.Mode);
        Assert.Equal("downloadAndPrompt", policy.WindowsAutomation.General.Mode);
        Assert.Equal((127, "17:00"), (policy.InstallDaysMask, policy.InstallTime));
    }

    [Fact]
    public void SeededFleetPolicy_WithAutoRebootOff_DoesNotSwitchItBackOn()
    {
        string template = Path.Combine(RepoRoot(), "scripts", "fleet-policy.business.json");
        string seeded = Path.Combine(_temp, "fleet-policy.json");
        File.Copy(template, seeded);

        // As shipped the template turns automatic restart on over whatever the policy file says.
        var asShipped = ReadFleet(seeded);
        Assert.True(asShipped.AutoRebootEnabled);
        var policy = ServerPolicyWithoutRestart();
        FleetPolicy.ApplyOverlay(policy, asShipped);
        Assert.True(policy.WindowsAutomation.AutoReboot.Enabled);

        RunWithFunctions(ScriptsCopy, new[] { "Set-UeFleetAutoReboot" }, $"Set-UeFleetAutoReboot -Path '{seeded}' -Enabled $false");

        Assert.Equal((byte)'{', File.ReadAllBytes(seeded)[0]);
        var patched = ReadFleet(seeded);
        Assert.False(patched.AutoRebootEnabled);
        policy = ServerPolicyWithoutRestart();
        FleetPolicy.ApplyOverlay(policy, patched);
        policy.Normalize();
        Assert.False(policy.WindowsAutomation.AutoReboot.Enabled);
        Assert.True(policy.WindowsAutomation.NeverAutoReboot);

        // Everything else in the template is carried over unchanged.
        patched.AutoRebootEnabled = asShipped.AutoRebootEnabled;
        Assert.Equal(JsonSerializer.Serialize(asShipped), JsonSerializer.Serialize(patched));
    }

    [Fact]
    public void ScriptsCopy_PatchesTheSeededFleetPolicy_OnlyWhenAutoRebootIsOff()
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), ScriptsCopy));
        int copy = text.IndexOf("Copy-Item -LiteralPath $fleetTemplate -Destination $fleetPath -Force", StringComparison.Ordinal);
        int guard = text.IndexOf("if (-not $AutoReboot) {", copy, StringComparison.Ordinal);
        int patch = text.IndexOf("Set-UeFleetAutoReboot -Path $fleetPath -Enabled $false", copy, StringComparison.Ordinal);

        Assert.True(copy > 0, "The script should seed the fleet policy from its template.");
        Assert.True(copy < guard && guard < patch, "The seeded copy should be patched right after it is copied, when AutoReboot is off.");
        Assert.True(
            text.IndexOf("$AutoReboot = Resolve-UeAutoReboot", StringComparison.Ordinal) < copy,
            "AutoReboot should be decided before the fleet policy is seeded.");
    }

    private static UpdatePolicy ServerPolicyWithoutRestart()
    {
        var policy = new UpdatePolicy();
        MaintenanceSetup.ApplyProfileDefaults(policy, "server");
        return policy;
    }

    private static FleetPolicyDocument ReadFleet(string path) =>
        JsonSerializer.Deserialize<FleetPolicyDocument>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// <summary>Defines the named functions from the script, then runs <paramref name="body"/>.</summary>
    private string RunWithFunctions(string script, string[] functions, string body) => Run($$"""
        $ErrorActionPreference = 'Stop'
        $tokens = $null; $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile('{{PathOf(script)}}', [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) { throw "The script does not parse: $($errors[0].Message)" }
        foreach ($name in '{{string.Join("', '", functions)}}') {
            $definition = $ast.Find({ param($n)
                $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $false)
            if ($null -eq $definition) { throw "The script has no function $name." }
            . ([scriptblock]::Create($definition.Extent.Text))
        }
        {{body}}
        """);

    private string Run(string powerShell)
    {
        string file = Path.Combine(_temp, Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, powerShell, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "PowerShell did not finish.");
        Assert.True(process.ExitCode == 0, $"PowerShell failed ({process.ExitCode}): {error.Result}\n{output}");
        return output;
    }

    private static string PathOf(string script) => Path.Combine(RepoRoot(), script);

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, ScriptsCopy)))
                return folder.FullName;
        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
