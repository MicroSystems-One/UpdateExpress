using System.Text;
using System.Text.RegularExpressions;

namespace UpdateExpress.Services.Security;

/// <summary>
/// Last line of defence for free text in a result (detail, detected value) before it is
/// shown, cached or logged. Providers must not read secrets in the first place; this catches
/// what an error message or a Windows value might carry along anyway.
/// </summary>
internal static class SecurityTextSanitizer
{
    internal const int MaxLength = 400;
    internal const string Redacted = "[redacted]";

    /// <summary>A BitLocker recovery password: eight groups of six digits.</summary>
    private static readonly Regex RecoveryPassword = new(
        @"\b\d{6}(?:-\d{6}){7}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>"password=…", "DefaultPassword: …", "recovery key = …", "token=…" and the like, up to the end of the value.</summary>
    private static readonly Regex NamedSecret = new(
        @"(?<name>\b[\w.-]*(?:password|passwd|pwd|passord|secret|recovery\s*key|token|credential)[\w.-]*\b\s*[:=]\s*)(?<value>""[^""]*""|'[^']*'|[^\s;,]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        // Bound the work first; the redaction below still sees far more than is kept.
        if (text.Length > MaxLength * 10) text = text[..(MaxLength * 10)];
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
            builder.Append(c == '\n' || !char.IsControl(c) ? c : ' ');
        string cleaned = builder.ToString().Trim();
        cleaned = RecoveryPassword.Replace(cleaned, Redacted);
        cleaned = NamedSecret.Replace(cleaned, m => m.Groups["name"].Value + Redacted);
        return cleaned.Length > MaxLength ? cleaned[..MaxLength] + "…" : cleaned;
    }
}
