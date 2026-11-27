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

    private const string SecretWord = "(?:password|passwd|pwd|passord|secret|recovery\\s*key|token|credential)";

    /// <summary>
    /// "password=…", "DefaultPassword: …", "recovery key = …", JSON's "access_token":"…" and the
    /// like. A value in quotes ends at its closing quote. Any other value goes to the end of its
    /// line: a password may hold spaces, semicolons and brackets, so nothing shorter is safe.
    /// </summary>
    private static readonly Regex NamedSecret = new(
        @"(?<name>[""']?\b[\w.-]*" + SecretWord + @"[\w.-]*\b[""']?[ \t]*[:=][ \t]*)" +
        @"(?<value>""(?:[^""\\\r\n]|\\.)*""|'(?:[^'\\\r\n]|\\.)*'|[^\r\n]+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The same in XML: &lt;Password&gt;…&lt;/Password&gt;.</summary>
    private static readonly Regex SecretElement = new(
        @"(?<name><[\w:.-]*" + SecretWord + @"[\w:.-]*(?:\s[^<>]*)?>)(?<value>[^<]+)",
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
        cleaned = SecretElement.Replace(cleaned, m => m.Groups["name"].Value + Redacted);
        return cleaned.Length > MaxLength ? cleaned[..MaxLength] + "…" : cleaned;
    }
}
