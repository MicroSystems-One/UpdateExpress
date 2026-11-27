using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateExpress.Services.Security;

/// <summary>
/// Keeps the last finished assessment in the user's own data folder, so that the main window
/// can show the last known status when it opens without assessing anything.
/// A file that is missing, unreadable, damaged or from another format version is "no
/// assessment yet"; it is never read as a pass or a fail.
/// </summary>
internal static class SecurityResultCache
{
    internal const string FileName = "security-overview.json";
    private const long MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>For tests and isolated runs: another file than the signed-in user's own.</summary>
    internal static string? PathOverride { get; set; }

    internal static string DefaultPath =>
        string.IsNullOrWhiteSpace(PathOverride) ? Path.Combine(AgentLog.UserDataDir, FileName) : PathOverride;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Names only: a number in the file is not taken for a status.
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    internal static SecurityAssessmentSnapshot? TryLoad(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            // The main window runs elevated and this folder is the user's: open the file
            // itself, never a link planted at its name.
            using var stream = LinkSafeFile.OpenRead(path);
            if (stream.Length == 0 || stream.Length > MaxFileBytes) return null;
            var snapshot = JsonSerializer.Deserialize<SecurityAssessmentSnapshot>(stream, Json);
            if (snapshot is null || snapshot.SchemaVersion != SecurityAssessmentSnapshot.CurrentSchemaVersion) return null;
            if (!snapshot.Completed || string.IsNullOrWhiteSpace(snapshot.BaselineId)) return null;
            if (snapshot.CompletedAt == default || snapshot.Results is null) return null;
            if (snapshot.Results.Any(r => r is null || string.IsNullOrWhiteSpace(r.Id))) return null;
            // The file is the user's to edit; what it says is cleaned like a fresh result.
            return snapshot with
            {
                ModuleVersion = SecurityTextSanitizer.Clean(snapshot.ModuleVersion),
                AppVersion = SecurityTextSanitizer.Clean(snapshot.AppVersion),
                ComputerName = SecurityTextSanitizer.Clean(snapshot.ComputerName),
                OsName = SecurityTextSanitizer.Clean(snapshot.OsName),
                OsVersion = SecurityTextSanitizer.Clean(snapshot.OsVersion),
                Results = snapshot.Results.Select(Clean).ToArray()
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves a finished assessment. A cancelled one is not kept: it would replace a whole picture with half of one.</summary>
    internal static bool TrySave(SecurityAssessmentSnapshot snapshot, string? path = null)
    {
        if (snapshot is null || !snapshot.Completed) return false;
        path ??= DefaultPath;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return false;
            Directory.CreateDirectory(directory);
            LinkSafeFile.Replace(path, output => JsonSerializer.Serialize(output, snapshot, Json));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static SecurityCheckResult Clean(SecurityCheckResult result) => result with
    {
        Id = result.Id.Trim(),
        // A check cannot still be running in a finished assessment.
        Status = result.Status == SecurityAssessmentStatus.Checking ? SecurityAssessmentStatus.Unknown : result.Status,
        DetectedCode = string.IsNullOrWhiteSpace(result.DetectedCode) ? SecurityStateCodes.Unknown : result.DetectedCode.Trim(),
        DetectedValue = SecurityTextSanitizer.Clean(result.DetectedValue),
        ReasonCode = SecurityTextSanitizer.Clean(result.ReasonCode),
        Detail = SecurityTextSanitizer.Clean(result.Detail)
    };
}
