<#
.SYNOPSIS
  Silent Update Express install for N-able RMM, with business maintenance policy
  matching the Pilot UI (profile business, active hours 07–17, install daily 17:00,
  reboot outside active hours) and local N-able PME service disabled.

.NOTES
  - Requires Update Express v0.7.2.18+ (--silent on --apply-maintenance).
  - Stopping PME.Agent.PmeService is LOCAL only. Also disable Windows patching for
    the device in the N-able console / patch profile, or RMM may re-enable PME.
  - Policy files are written per Windows user profile under
    %LocalAppData%\UpdateExpress\ so the tray agent picks them up without the wizard.
  - Run elevated (N-able Automation / SYSTEM is fine).

.EXAMPLE
  .\Install-UpdateExpress-Nable.ps1 -Version 0.7.2.18

.EXAMPLE
  .\Install-UpdateExpress-Nable.ps1 -ExePath 'C:\Temp\UpdateExpress_v0.7.2.18.exe' -Scope machine
#>
[CmdletBinding()]
param(
    [ValidateSet('user', 'machine')]
    [string]$Scope = 'user',

    [string]$Version = '0.7.2.18',

    [string]$ExePath,

    [switch]$Desktop = $true,
    [switch]$StartMenu = $true,
    [switch]$Taskbar,
    # Background agent at sign-in (user scope only; machine install keeps agent off in the EXE).
    [switch]$Agent = $true,

    [switch]$DisablePme = $true,

    [string]$PmeServiceName = 'PME.Agent.PmeService',

    # Screenshot defaults
    [string]$SetupProfile = 'business',
    [int]$ActiveHoursStart = 7,
    [int]$ActiveHoursEnd = 17,
    [string]$InstallTime = '17:00',
    [int]$InstallDaysMask = 127,   # Mon..Sun
    [int]$RebootCountdownSeconds = 300,
    [ValidateSet('outsideActiveHours', 'confirm')]
    [string]$RebootMode = 'outsideActiveHours',
    [switch]$AutoReboot = $true,
    [switch]$Reminders = $true,
    [switch]$WindowsAutomation = $true,
    [string]$Language = 'en'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-UeLog([string]$Message) {
    $line = '[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Output $line
}

function Get-InteractiveUserProfiles {
    $profiles = @()
    try {
        $loggedOn = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty UserName
        if ($loggedOn -and $loggedOn -notmatch '^(SYSTEM|LOCAL SERVICE|NETWORK SERVICE)$') {
            $sid = (New-Object System.Security.Principal.NTAccount($loggedOn)).Translate(
                [System.Security.Principal.SecurityIdentifier]).Value
            $profilePath = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\$sid" -ErrorAction SilentlyContinue).ProfileImagePath
            if ($profilePath -and (Test-Path $profilePath)) {
                $profiles += [pscustomobject]@{ User = $loggedOn; Sid = $sid; Path = $profilePath }
            }
        }
    } catch { Write-UeLog "Could not resolve console user: $($_.Exception.Message)" }

    Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction SilentlyContinue |
        ForEach-Object {
            $sid = $_.PSChildName
            if ($sid -notmatch '^S-1-5-21-') { return }
            $p = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).ProfileImagePath
            if (-not $p -or -not (Test-Path $p)) { return }
            if ($p -match '\\(Default|Public|systemprofile)$') { return }
            if ($profiles.Sid -contains $sid) { return }
            $profiles += [pscustomobject]@{ User = $sid; Sid = $sid; Path = $p }
        }

    if ($profiles.Count -eq 0) {
        $fallback = Join-Path $env:USERPROFILE ''
        if (Test-Path $fallback) {
            $profiles += [pscustomobject]@{ User = $env:USERNAME; Sid = ''; Path = $env:USERPROFILE }
        }
    }
    return $profiles
}

function Write-UePolicyFiles {
    param(
        [Parameter(Mandatory)][string]$LocalAppDataRoot
    )
    $dir = Join-Path $LocalAppDataRoot 'UpdateExpress'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    $policy = [ordered]@{
        WindowsAutomation = [ordered]@{
            Enabled         = [bool]$WindowsAutomation
            NeverAutoReboot = -not [bool]$AutoReboot
            AutoReboot      = [ordered]@{
                Enabled           = [bool]$AutoReboot
                Mode              = $RebootMode
                ActiveHoursStart  = $ActiveHoursStart
                ActiveHoursEnd    = $ActiveHoursEnd
                CountdownSeconds  = $RebootCountdownSeconds
            }
            Defender = [ordered]@{
                Enabled         = $true
                IntervalMinutes = 60
                Mode            = 'autoInstall'
                PreferredInstallTime = $InstallTime
                Weekday         = 2
            }
            Critical = [ordered]@{
                Enabled              = $true
                IntervalMinutes      = 1440
                Mode                 = 'downloadAndPrompt'
                PreferredInstallTime = $InstallTime
                Weekday              = 2
            }
            General = [ordered]@{
                Enabled              = $true
                IntervalMinutes      = 10080
                Mode                 = 'downloadAndPrompt'
                PreferredInstallTime = $InstallTime
                Weekday              = 7
            }
        }
        AutomaticDrivers            = $false
        AutomaticFirmware           = $false
        AutomaticApplications       = $false
        ApplicationsIntervalMinutes = 240
        AutomaticApplicationIds     = @()
        AutomaticWindows            = [bool]$WindowsAutomation
        SetupCompleted              = $true
        SetupProfile                = $SetupProfile
        InstallDaysMask             = $InstallDaysMask
        InstallTime                 = $InstallTime
    }

    $schedule = [ordered]@{
        Use24HourClock = $true
        Language       = $Language
        DateFormat     = 'text'
        Enabled        = [bool]$Reminders
        Weekday        = 7
        Time           = $InstallTime
    }

    $policyPath = Join-Path $dir 'update-policy.json'
    $schedulePath = Join-Path $dir 'schedule.json'
    ($policy | ConvertTo-Json -Depth 8) | Set-Content -Path $policyPath -Encoding UTF8
    ($schedule | ConvertTo-Json -Depth 5) | Set-Content -Path $schedulePath -Encoding UTF8
    Write-UeLog "Wrote policy: $policyPath"
    Write-UeLog "Wrote schedule: $schedulePath"
}

function Disable-NablePme {
    param([string]$ServiceName)
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Write-UeLog "PME service '$ServiceName' not found (OK if patch agent not installed)."
        return
    }
    Write-UeLog "Disabling PME service '$ServiceName' (was $($svc.Status) / $($svc.StartType))..."
    try {
        if ($svc.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
        Set-Service -Name $ServiceName -StartupType Disabled -ErrorAction Stop
        # sc.exe as belt-and-suspenders when Set-Service is overridden by policy later
        & sc.exe stop $ServiceName | Out-Null
        & sc.exe config $ServiceName start= disabled | Out-Null
        $after = Get-Service -Name $ServiceName
        Write-UeLog "PME now: $($after.Status) / $($after.StartType)"
    } catch {
        Write-UeLog "WARNING: Could not disable PME: $($_.Exception.Message)"
        Write-UeLog "Disable Windows Update patching for this device in the N-able console as well."
    }
}

function Register-UeAgentRunKey {
    param(
        [string]$Sid,
        [string]$Exe
    )
    if ([string]::IsNullOrWhiteSpace($Sid) -or -not (Test-Path $Exe)) { return }
    $runPath = "Registry::HKEY_USERS\$Sid\Software\Microsoft\Windows\CurrentVersion\Run"
    if (-not (Test-Path $runPath)) {
        New-Item -Path $runPath -Force | Out-Null
    }
    $value = '"{0}" --agent' -f $Exe
    New-ItemProperty -Path $runPath -Name 'MicroSystems Update Express Agent' -Value $value -PropertyType String -Force | Out-Null
    Write-UeLog "Registered agent Run key for SID $Sid"
}

# --- Download / resolve EXE ---
if (-not $ExePath) {
    $url = "https://github.com/MicroSystems-One/UpdateExpress/releases/download/v$Version/UpdateExpress_v$Version.exe"
    $ExePath = Join-Path $env:TEMP "UpdateExpress_v$Version.exe"
    Write-UeLog "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $ExePath -UseBasicParsing
}
if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "EXE not found: $ExePath"
}
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
Write-UeLog "Using EXE: $ExePath"

# --- Optional: disable N-able PME locally before install ---
if ($DisablePme) {
    Disable-NablePme -ServiceName $PmeServiceName
}

# --- Silent install via Update Express maintenance CLI ---
# parentPid 0 = no wait (RMM/script). --silent suppresses UI dialogs (0.7.2.18+).
$env:UE_SILENT = '1'
$desktopFlag = if ($Desktop) { '1' } else { '0' }
$menuFlag = if ($StartMenu) { '1' } else { '0' }
$agentFlag = if ($Agent -and $Scope -eq 'user') { '1' } else { '0' }
$taskbarFlag = if ($Taskbar) { '1' } else { '0' }

$argList = @(
    '--apply-maintenance',
    'install',
    '0',
    $ExePath,
    $Scope,
    $desktopFlag,
    $menuFlag,
    $agentFlag,
    $taskbarFlag,
    '--silent'
)

Write-UeLog "Silent install: scope=$Scope desktop=$desktopFlag startMenu=$menuFlag agent=$agentFlag taskbar=$taskbarFlag"
$p = Start-Process -FilePath $ExePath -ArgumentList $argList -Wait -PassThru -WindowStyle Hidden
if ($null -eq $p -or $p.ExitCode -notin 0, $null) {
    $code = if ($p) { $p.ExitCode } else { 'null' }
    throw "Silent install failed (exit $code). Check %LOCALAPPDATA%\UpdateExpress\agent.log or ProgramData logs."
}
Write-UeLog "Install process exit code: $($p.ExitCode)"

$installedUser = Join-Path $env:LOCALAPPDATA 'MicroSystems\Update Express\UpdateExpress.exe'
$installedMachine = Join-Path ${env:ProgramFiles} 'MicroSystems\Update Express\UpdateExpress.exe'
$installedExe = if ($Scope -eq 'machine' -and (Test-Path $installedMachine)) {
    $installedMachine
} elseif (Test-Path $installedUser) {
    $installedUser
} elseif (Test-Path $installedMachine) {
    $installedMachine
} else {
    $null
}
if (-not $installedExe) {
    Write-UeLog "WARNING: Installed EXE path not found yet; policy will still be written."
} else {
    Write-UeLog "Installed EXE: $installedExe"
}

# --- Seed Pilot policy (screenshot values) for each profile ---
$profiles = Get-InteractiveUserProfiles
foreach ($prof in $profiles) {
    $localApp = Join-Path $prof.Path 'AppData\Local'
    if (-not (Test-Path $localApp)) { continue }
    Write-UePolicyFiles -LocalAppDataRoot $localApp
    if ($Agent -and $installedExe -and $prof.Sid) {
        Register-UeAgentRunKey -Sid $prof.Sid -Exe $installedExe
    }
}

# Also seed current process LocalAppData (SYSTEM or deploying user)
Write-UePolicyFiles -LocalAppDataRoot $env:LOCALAPPDATA

# Start agent now when user-scope install requested and EXE exists
if ($Agent -and $Scope -eq 'user' -and $installedExe) {
    try {
        Start-Process -FilePath $installedExe -ArgumentList '--agent' -WindowStyle Hidden | Out-Null
        Write-UeLog 'Started --agent'
    } catch {
        Write-UeLog "Could not start agent now: $($_.Exception.Message)"
    }
}

Write-UeLog 'Done. Reminder: confirm N-able patch profile does not re-enable PME / Windows patching for this device.'
exit 0
