using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace UpdateExpress.Services;

/// <summary>
/// Resolves the Windows Update / COM HRESULT to show after a driver or firmware install failure.
/// Wrapped <see cref="InvalidOperationException"/> values (for example 0x80131509) are not WUA codes.
/// </summary>
internal static class WindowsUpdateHResult
{
    internal const string ManagedInvalidOperation = "0x80131509";

    private static readonly Regex Embedded = new(
        @"(?:Item)?HRESULT[=:\s]+(0x[0-9A-Fa-f]{8})|(?<!\w)(0x80[0-9A-Fa-f]{6})(?!\w)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Best display HRESULT from an exception chain, or empty when none found.</summary>
    internal static string BestFromException(Exception ex)
    {
        var candidates = new List<string>();
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is COMException com && com.ErrorCode != 0)
                candidates.Add(ToHex(com.ErrorCode));
            if (cur.HResult != 0)
                candidates.Add(ToHex(cur.HResult));
            candidates.AddRange(FromText(cur.Message));
        }
        candidates.AddRange(FromText(ex.ToString()));
        return PickBest(candidates);
    }

    /// <summary>Best display HRESULT from free text (Installation Manager state, tips, logs).</summary>
    internal static string BestFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return PickBest(FromText(text));
    }

    internal static string FormatForFailedState(Exception ex)
    {
        string code = BestFromException(ex);
        return string.IsNullOrEmpty(code) ? "Failed" : "Failed " + code;
    }

    private static IEnumerable<string> FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (Match match in Embedded.Matches(text))
        {
            string code = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(code))
                yield return WindowsUpdateErrorCatalog.Normalize(code);
        }
    }

    private static string PickBest(IEnumerable<string> raw)
    {
        string? fallback = null;
        string? managed = null;
        foreach (string code in raw.Select(WindowsUpdateErrorCatalog.Normalize).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(code, ManagedInvalidOperation, StringComparison.OrdinalIgnoreCase))
            {
                managed ??= code;
                continue;
            }
            if (IsLikelyWuaOrWin32(code))
                return code;
            fallback ??= code;
        }
        return fallback ?? managed ?? "";
    }

    private static bool IsLikelyWuaOrWin32(string code)
    {
        if (!code.StartsWith("0x80", StringComparison.OrdinalIgnoreCase) || code.Length != 10)
            return false;
        return !string.Equals(code, ManagedInvalidOperation, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToHex(int value) => $"0x{unchecked((uint)value):X8}";
}