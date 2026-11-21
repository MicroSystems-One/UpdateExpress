using System;
using System.IO;

namespace UpdateExpress.Services;

/// <summary>
/// Shared prompt/queue/status directory for automation helpers.
/// Elevating with a different admin identity must keep reading the tray user's files,
/// so elevated launches pass --automation-state-dir explicitly.
/// </summary>
internal static class AutomationStatePaths
{
    private static string? _overrideDirectory;

    internal static string Directory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_overrideDirectory))
                return _overrideDirectory!;
            return InstallationManager.InstallDir;
        }
    }

    internal static string ElevateArguments(string elevateArg)
    {
        string dir = Directory;
        return $"{elevateArg} --automation-state-dir=\"{dir}\"";
    }

    /// <summary>
    /// Apply --automation-state-dir from elevated helper argv. Fail closed on invalid paths.
    /// </summary>
    internal static bool TryApplyFromArgs(string[] args)
    {
        string? raw = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--automation-state-dir=", StringComparison.OrdinalIgnoreCase))
            {
                raw = a["--automation-state-dir=".Length..].Trim().Trim('"');
                break;
            }
            if (string.Equals(a, "--automation-state-dir", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < args.Length)
            {
                raw = args[i + 1].Trim().Trim('"');
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!TryValidateStateDirectory(raw, out string full, out string reason))
        {
            AgentLog.Write("Rejected automation-state-dir: " + reason + " (" + raw + ")");
            return false;
        }

        _overrideDirectory = full;
        AgentLog.Write("Automation state directory override: " + full);
        return true;
    }

    internal static bool TryValidateStateDirectory(string candidate, out string fullPath, out string reason)
    {
        fullPath = "";
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                reason = "empty";
                return false;
            }
            if (candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                reason = "invalid characters";
                return false;
            }
            if (!Path.IsPathRooted(candidate))
            {
                reason = "not rooted";
                return false;
            }
            if (candidate.Contains("..", StringComparison.Ordinal))
            {
                reason = "path traversal";
                return false;
            }

            fullPath = Path.GetFullPath(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string name = Path.GetFileName(fullPath);
            string parent = Path.GetFileName(Path.GetDirectoryName(fullPath) ?? "");
            if (!string.Equals(parent, "MicroSystems", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(name, "Update Express", StringComparison.OrdinalIgnoreCase))
            {
                reason = "must be ...\\MicroSystems\\Update Express";
                return false;
            }
            if (!System.IO.Directory.Exists(fullPath))
            {
                reason = "directory does not exist";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }
}
