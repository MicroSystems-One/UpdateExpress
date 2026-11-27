using UpdateExpress.Services.Security;
using Xunit;

namespace UpdateExpress.Tests;

public class SecurityTextSanitizerTests
{
    [Theory]
    [InlineData("Signature: 1.463.492.0")]
    [InlineData("Last updated: 27.11.2026 04:20")]
    [InlineData("Windows 11 Pro 25H2 · Build 26200")]
    [InlineData("AutoAdminLogon=1; a user name is set")]
    [InlineData("Line one\nLine two")]
    [InlineData("KB5132061, 2026-11 Cumulative Update")]
    public void Ordinary_detail_is_kept_as_it_is(string text)
    {
        Assert.Equal(text, SecurityTextSanitizer.Clean(text));
    }

    [Fact]
    public void Nothing_in_gives_nothing_out()
    {
        Assert.Equal("", SecurityTextSanitizer.Clean(null));
        Assert.Equal("", SecurityTextSanitizer.Clean(""));
        Assert.Equal("", SecurityTextSanitizer.Clean("  \t "));
    }

    [Fact]
    public void A_BitLocker_recovery_password_is_removed()
    {
        string cleaned = SecurityTextSanitizer.Clean("Protector {ABC}: 123456-234567-345678-456789-567890-678901-789012-890123 end");

        Assert.Equal("Protector {ABC}: [redacted] end", cleaned);
    }

    [Theory]
    [InlineData("DefaultPassword=hunter2", "hunter2")]
    [InlineData("DefaultPassword: hunter2; DefaultUserName: kiosk", "hunter2")]
    [InlineData("password = \"two words\" and more", "two words")]
    [InlineData("Passord: 'hemmelig verdi'", "hemmelig")]
    [InlineData("AdmPwd=Xk3!9z", "Xk3!9z")]
    [InlineData("recovery key = 4f2a-99", "4f2a-99")]
    [InlineData("access_token=eyJhbGciOi", "eyJhbGciOi")]
    [InlineData("ClientSecret:abc123", "abc123")]
    public void A_value_named_as_a_secret_is_removed_and_its_name_is_kept(string text, string secret)
    {
        string cleaned = SecurityTextSanitizer.Clean(text);

        Assert.DoesNotContain(secret, cleaned);
        Assert.Contains(SecurityTextSanitizer.Redacted, cleaned);
    }

    [Fact]
    public void What_follows_a_removed_value_is_kept()
    {
        Assert.Equal("DefaultPassword: [redacted]; DefaultUserName: kiosk",
            SecurityTextSanitizer.Clean("DefaultPassword: hunter2; DefaultUserName: kiosk"));
    }

    [Fact]
    public void Control_characters_become_spaces_and_line_breaks_stay()
    {
        Assert.Equal("a b\nc", SecurityTextSanitizer.Clean("a\u0007b\nc\r"));
    }

    [Fact]
    public void Long_text_is_cut()
    {
        string cleaned = SecurityTextSanitizer.Clean(new string('x', 5000));

        Assert.Equal(SecurityTextSanitizer.MaxLength + 1, cleaned.Length);
        Assert.EndsWith("…", cleaned);
    }

    [Fact]
    public void A_secret_past_the_cut_does_not_survive_in_part()
    {
        string text = new string('x', SecurityTextSanitizer.MaxLength - 20) + " password=abcdefghijklmnopqrstuvwxyz0123456789";

        string cleaned = SecurityTextSanitizer.Clean(text);

        Assert.DoesNotContain("abcdefgh", cleaned);
    }
}
