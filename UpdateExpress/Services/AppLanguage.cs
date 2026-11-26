using System;
using System.Collections.Generic;

namespace UpdateExpress.Services;

/// <summary>
/// App UI language helpers. English keys map through the same en→no string table
/// pattern as MainWindow.ApplyLanguage / ReminderSchedule.Language.
/// </summary>
internal static class AppLanguage
{
    public static bool IsNorwegian =>
        string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase);

    /// <summary>English → Norwegian string table (dialog chrome and self-update prompt).</summary>
    private static readonly Dictionary<string, string> Norwegian = new(StringComparer.Ordinal)
    {
        // Dialog button captions — never hardcode these at call sites.
        ["Yes"] = "Ja",
        ["No"] = "Nei",
        ["Cancel"] = "Avbryt",
        ["OK"] = "OK",

        // Self-update prompt titles / balloons / bodies
        ["Update Express update available"] = "Update Express – oppdatering tilgjengelig",
        ["Checking for application updates…"] = "Ser etter programoppdateringer…",
        ["You are up to date (v{0})."] = "Du er oppdatert (v{0}).",
        ["Update Express is up to date.\n\nInstalled version: {0}"] =
            "Update Express er oppdatert.\n\nInstallert versjon: {0}",
        ["Version {0} is available."] = "Versjon {0} er tilgjengelig.",
        ["No release notes provided."] = "Ingen versjonsmerknader.",
        ["A newer Update Express version is available."] = "En nyere versjon av Update Express er tilgjengelig.",
        ["Installed: {0}"] = "Installert: {0}",
        ["Available: {0}"] = "Tilgjengelig: {0}",
        ["Install now"] = "Installer nå",
        ["Install now (closes open Update Express, then download + verify SHA-256 + replace)"] =
            "Installer nå (lukker åpen Update Express, deretter last ned + verifiser SHA-256 + erstatt)",
        ["Open download page"] = "Åpne nedlastingssiden",
        ["Later"] = "Senere",
        ["SHA-256 is missing from the update channel, so Install is unavailable."] =
            "SHA-256 mangler i oppdateringskanalen, så Installer er utilgjengelig.",
        ["No HTTPS download URL is published yet."] = "Ingen HTTPS-nedlastingsadresse er publisert ennå.",
        ["Upgrade cancelled — close Update Express and try again."] =
            "Oppgradering avbrutt — lukk Update Express og prøv igjen.",
        ["Could not open download page: {0}"] = "Kunne ikke åpne nedlastingssiden: {0}",
        ["Application update check failed."] = "Sjekk av programoppdatering feilet.",
        ["Could not check for Update Express updates.\n\n{0}"] =
            "Kunne ikke sjekke etter Update Express-oppdateringer.\n\n{0}",
        ["Unknown error"] = "Ukjent feil",

        // Restart countdown notice
        ["Update Express – Restart"] = "Update Express – Omstart",
        ["Restart coming soon"] = "Omstart om kort tid",
        ["Restart in {0}"] = "Omstart om {0}",
        ["Save your work now. This PC restarts when the countdown reaches 0."] =
            "Lagre arbeidet ditt nå. PC-en starter på nytt når nedtellingen når 0.",
        ["Stop cancels the restart. Postpone moves it 15 minutes out."] =
            "Stop avbryter omstarten. Utsett flytter den 15 minutter frem.",
        ["Stop"] = "Stop",
        ["Restart now"] = "Omstart nå",
        ["Postpone 15 minutes"] = "Utsett 15 minutter",
        ["Update Express: restart now."] = "Update Express: omstart nå.",
    };

    /// <summary>Resolve an English UI key to the active app language.</summary>
    public static string T(string englishKey) =>
        IsNorwegian && Norwegian.TryGetValue(englishKey, out var norwegian)
            ? norwegian
            : englishKey;

    /// <summary>Resolve and format an English UI key for the active app language.</summary>
    public static string Tf(string englishKey, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, T(englishKey), args);

    public static string Yes => T("Yes");
    public static string No => T("No");
    public static string Cancel => T("Cancel");
    public static string Ok => T("OK");

    /// <summary>Legend line using the same button caption as the dialog chrome.</summary>
    public static string ButtonLegend(string buttonKey, string actionEnglishKey) =>
        T(buttonKey) + " = " + T(actionEnglishKey);
}
