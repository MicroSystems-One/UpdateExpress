using System.Globalization;
using System.IO;
using System.Text.Json;

namespace UpdateExpress.Services;

public sealed class ReminderSchedule
{
    public bool Use24HourClock { get; set; } = true;
    public string Language { get; set; } = "en";
    /// <summary>text (ddd dd MMM yyyy), dmy (dd.MM.yyyy), iso (yyyy-MM-dd), or mdy (MM/dd/yyyy).</summary>
    public string DateFormat { get; set; } = "text";
    public bool Enabled { get; set; }
    // Monday = 0, Sunday = 6, Every day = 7
    public int Weekday { get; set; } = 2;
    public string Time { get; set; } = "20:00";
    public DateTime? LastPromptedOccurrence { get; set; }
    public DateTime? ConfirmedOccurrence { get; set; }
    public DateTime? SnoozedUntil { get; set; }

    private static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateExpress");
    public static string SettingsPath => Path.Combine(DirectoryPath, "schedule.json");
    public static string EventsPath => Path.Combine(DirectoryPath, "reminder-events.jsonl");
    public static ReminderSchedule Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<ReminderSchedule>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void Log(string action, string description)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.AppendAllText(EventsPath, JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.Now, Action = action, Description = description }) + Environment.NewLine);
    }
    public string Format(DateTime time)
    {
        var culture = string.Equals(Language, "no", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("nb-NO")
            : CultureInfo.GetCultureInfo("en-US");
        string date = (DateFormat ?? "text").Trim().ToLowerInvariant() switch
        {
            "dmy" => time.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            "iso" => time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "mdy" => time.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
            _ => time.ToString("ddd dd MMM yyyy", culture)
        };
        string clock = Use24HourClock
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : time.ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"));
        return date + " " + clock;
    }

    /// <summary>
    /// Parse a stored or previously displayed Date/Time (ISO with optional seconds, or Settings formats).
    /// </summary>
    public static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim();
        string[] exact =
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "dd.MM.yyyy HH:mm",
            "dd.MM.yyyy HH:mm:ss",
            "MM/dd/yyyy HH:mm",
            "MM/dd/yyyy h:mm tt",
            "MM/dd/yyyy HH:mm:ss",
            "ddd dd MMM yyyy HH:mm",
            "ddd dd MMM yyyy H:mm",
            "ddd dd MMM yyyy h:mm tt",
            "ddd dd MMM yyyy hh:mm tt"
        };
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("nb-NO") })
        {
            foreach (var pattern in exact)
            {
                if (DateTime.TryParseExact(text, pattern, culture, DateTimeStyles.None, out var exactParsed))
                    return exactParsed;
            }
        }
        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var en))
            return en;
        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("nb-NO"), DateTimeStyles.None, out var no))
            return no;
        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    /// <summary>Format a stored history/install timestamp using current Settings date + clock prefs.</summary>
    public string FormatDateValue(string? value) =>
        TryParseDate(value) is DateTime when ? Format(when) : (value ?? "");
    public DateTime NextOccurrence(DateTime now)
    {
        if (!TimeSpan.TryParseExact(Time, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var at))
            throw new InvalidOperationException("Time must be HH:mm, for example 20:00.");
        int today = ((int)now.DayOfWeek + 6) % 7;
        int delta = Weekday == 7 ? 0 : (Weekday - today + 7) % 7;
        DateTime candidate = now.Date.AddDays(delta).Add(at);
        if (candidate < now) candidate = candidate.AddDays(Weekday == 7 ? 1 : 7);
        return candidate;
    }
}
