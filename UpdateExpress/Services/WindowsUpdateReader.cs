using System.IO;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using UpdateExpress.Models;
namespace UpdateExpress.Services;
public static class WindowsUpdateReader
{
    // WUA COM objects are created and used on the same dedicated STA thread.
    public static Task<List<UpdateEntry>> RunStaAsync(Func<List<UpdateEntry>> action) => RunStaAsync<List<UpdateEntry>>(action);

    public static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
    private static dynamic Session()
    {
        var type = Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update Agent er ikke tilgjengelig.");
        return Activator.CreateInstance(type)!;
    }
    private static string KBs(dynamic update)
    {
        var ids = new List<string>();
        try { foreach (var id in update.KBArticleIDs) ids.Add("KB" + Convert.ToString(id)); }
        catch (COMException) { }
        return string.Join(", ", ids);
    }
    private static bool ReadBool(dynamic u, string property, bool fallback = false)
    {
        try { return (bool)u.GetType().InvokeMember(property, System.Reflection.BindingFlags.GetProperty, null, u, null); }
        catch { try { return property switch { "IsMandatory" => (bool)u.IsMandatory, "IsBeta" => (bool)u.IsBeta, "IsHidden" => (bool)u.IsHidden, _ => fallback }; } catch { return fallback; } }
    }
    private static string Category(dynamic u, bool driver)
    {
        if (driver) return "Driver / firmware";
        var title = (Convert.ToString(u.Title) ?? "").ToLowerInvariant();
        if (title.Contains("preview") || title.Contains("forhåndsvisning") || title.Contains("vorschau") || ReadBool(u, "IsBeta")) return "Preview / valgfri";
        if (title.Contains("feature update") || title.Contains("funksjonsoppdatering") || title.Contains("enablement package")) return "Versjonsoppgradering";
        return "Programvare / sikkerhet";
    }
    private static bool IsFirmwareUpdate(dynamic update)
    {
        string title = Convert.ToString(update.Title) ?? "";
        if (title.Contains("Firmware", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Fastvare", StringComparison.OrdinalIgnoreCase))
            return true;
        try
        {
            return string.Equals(Convert.ToString(update.DriverClass), "Firmware", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private static (string Label, string Explanation) AssessPriority(object updateObject, string category, bool driver, bool firmware = false)
    {
        // Keep tuple member access statically typed; dynamic COM values otherwise
        // cause the returned ValueTuple to be accessed via runtime binder (.Label fails).
        dynamic update = updateObject;
        // Only MSRC severity is an official severity. A WUA category is NOT an MSRC rating.
        // Never infer Critical/Important from KB, title, IsMandatory, or category names.
        string severity = "";
        try { severity = Convert.ToString(update.MsrcSeverity)?.Trim() ?? ""; } catch { }
        string categories = "";
        try
        {
            var names = new List<string>();
            foreach (var item in update.Categories) names.Add(Convert.ToString(item.Name) ?? "");
            categories = string.Join(", ", names.Where(n => !string.IsNullOrWhiteSpace(n)));
        }
        catch { }
        var metadata = string.IsNullOrWhiteSpace(categories) ? "No WUA categories provided" : "WUA categories: " + categories;
        if (!driver)
        {
            if (severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)) return ("Critical", "Official MSRC severity: Critical. " + metadata);
            if (severity.Equals("Important", StringComparison.OrdinalIgnoreCase)) return ("Important", "Official MSRC severity: Important. " + metadata);
            if (severity.Equals("Moderate", StringComparison.OrdinalIgnoreCase)) return ("Moderate", "Official MSRC severity: Moderate. " + metadata);
            if (severity.Equals("Low", StringComparison.OrdinalIgnoreCase)) return ("Low", "Official MSRC severity: Low (displayed as Moderate / K3). " + metadata);
            if (category == "Preview / valgfri") return ("Optional", "Preview/optional update classification; NOT an official MSRC severity. " + metadata);
        }
        if (driver)
        {
            if (severity.Equals("Critical", StringComparison.OrdinalIgnoreCase)) return ("Critical", "Official MSRC severity: Critical. " + metadata);
            if (severity.Equals("Important", StringComparison.OrdinalIgnoreCase)) return ("Important", "Official MSRC severity: Important. " + metadata);
            if (severity.Equals("Moderate", StringComparison.OrdinalIgnoreCase)) return ("Moderate", "Official MSRC severity: Moderate. " + metadata);
            if (severity.Equals("Low", StringComparison.OrdinalIgnoreCase)) return ("Low", "Official MSRC severity: Low (displayed as Moderate / K3). " + metadata);
            if (firmware)
                return ("Moderate", "Firmware offered by Windows Update. Operational Moderate (K3): recommended to review. Not an official MSRC severity, and never selected automatically. " + metadata);
            return ("Unknown", "No verified WUA/MSRC priority for this driver; WUA IsAssigned is shown separately. " + metadata);
        }
        // KB2267602 has no MSRC severity. Moderate (K3) is an operational grade, not Critical (K1).
        string kb = KBs(update);
        if (OperationalPriority.IsDefenderKb(kb))
            return (OperationalPriority.DefenderLabel, OperationalPriority.DefenderExplanation + " " + metadata);
        return ("Unrated", "No verified MSRC severity or applicable operational classification. " + metadata);
    }
    public static List<UpdateEntry> AdvancedPending(bool includeOptional = false, bool allDrivers = false)
    {
        dynamic session = Session();
        dynamic searcher = session.CreateUpdateSearcher();
        var entries = new List<UpdateEntry>();
        // Separate searches: the WUA 'Type' filter is the key reason drivers were missing in v0.3.3.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Only WUA-assigned driver identities may be preselected. A failed assigned
        // search must fail closed, never silently promote optional drivers.
        var assignedDriverIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            dynamic assigned = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver' and IsAssigned=1");
            for (int i = 0; i < (int)assigned.Updates.Count; i++)
            {
                dynamic item = assigned.Updates.Item(i);
                assignedDriverIds.Add($"{item.Identity.UpdateID}:{item.Identity.RevisionNumber}");
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("WUA assigned driver lookup unavailable: " + ex.Message); }
        // IWindowsDriverUpdate is a COM dispatch interface. Reflection on __ComObject
        // does not expose its members; access them through the dynamic COM binder.
        static string DriverField(dynamic update, string property)
        {
            try
            {
                return property switch
                {
                    "DriverVersion" => Convert.ToString(update.DriverVersion) ?? "",
                    "DriverHardwareID" => Convert.ToString(update.DriverHardwareID) ?? "",
                    "DriverModel" => Convert.ToString(update.DriverModel) ?? "",
                    "DriverManufacturer" => Convert.ToString(update.DriverManufacturer) ?? "",
                    "DriverClass" => Convert.ToString(update.DriverClass) ?? "",
                    "DriverProvider" => Convert.ToString(update.DriverProvider) ?? "",
                    _ => ""
                };
            }
            catch (Exception) { return ""; }
        }
        foreach (var kind in new[] { "Software", "Driver" })
        {
            // BrowseOnly is an additional WUA search criterion; it does not guarantee
            // parity with the Windows Settings optional-update offering.
            // Firmware is not a separate WUA type. Always search every pending driver,
            // then keep unassigned non-firmware offers only when All WUA drivers is on.
            var criteria = new List<string> { $"IsInstalled=0 and IsHidden=0 and Type='{kind}'" };
            if (includeOptional && kind == "Software")
                criteria.Add("IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=1");
            foreach (var criterion in criteria)
            {
              dynamic result = searcher.Search(criterion);
              for (int i = 0; i < (int)result.Updates.Count; i++)
              {
                dynamic u = result.Updates.Item(i);
                string identity = Convert.ToString(u.Identity.UpdateID) ?? "";
                string revision = Convert.ToString(u.Identity.RevisionNumber) ?? "";
                if (!seen.Add(identity + ":" + revision)) continue;
                var category = Category(u, kind == "Driver");
                bool firmware = kind == "Driver" && IsFirmwareUpdate(u);
                bool assignedDriver = kind == "Driver" && !firmware && assignedDriverIds.Contains(identity + ":" + revision);
                if (kind == "Driver" && !allDrivers && !firmware && !assignedDriverIds.Contains(identity + ":" + revision))
                    continue;
                if (kind == "Software" && criterion.Contains("BrowseOnly=1") && category == "Programvare / sikkerhet")
                    category = "Optional / valgfri";
                (string Label, string Explanation) assessment = AssessPriority((object)u, category, kind == "Driver", firmware);
                entries.Add(new UpdateEntry
                {
                    KB = KBs(u), Title = Convert.ToString(u.Title) ?? "", Category = category, Priority = assessment.Label, PriorityExplanation = assessment.Explanation,
                    Status = "Tilgjengelig", Source = "WUA / " + kind,
                    UpdateId = Convert.ToString(u.Identity.UpdateID) ?? "",
                    SafeDefault = category == "Programvare / sikkerhet",
                    DriverAssigned = assignedDriver,
                    DriverClassification = kind != "Driver" ? "" : firmware ? "Firmware" : assignedDriver ? "Recommended" : "Optional",
                    DriverVersionFromWua = kind == "Driver" ? DriverField(u, "DriverVersion") : "",
                    DriverHardwareId = kind == "Driver" ? DriverField(u, "DriverHardwareID") : "",
                    DriverModel = kind == "Driver" ? DriverField(u, "DriverModel") : "",
                    DriverManufacturer = kind == "Driver" ? DriverField(u, "DriverManufacturer") : "",
                    DriverClass = kind == "Driver" ? DriverField(u, "DriverClass") : "",
                    IsSelected = assignedDriver
                });
              }
            }
        }
        // Baseline: only explicit WUA assignment is a recommendation. Firmware is never selected.
        foreach (var entry in entries.Where(e => e.Category == "Driver / firmware"))
        {
            entry.DriverStatus = entry.DriverClassification == "Firmware" ? "Firmware" :
                entry.DriverAssigned ? "Recommended" : "Available";
            entry.DriverReason = entry.DriverClassification == "Firmware"
                ? "Firmware requires explicit confirmation; never auto-selected."
                : entry.DriverAssigned ? "WUA explicitly returned this update with IsAssigned=1."
                : "Installed driver version not verified for this hardware ID.";
        }
        InstalledDriverReader.Classify(entries);
        var featureOffer = FeatureUpdateOfferReader.Read();
        if (featureOffer is not null && !entries.Any(entry =>
                entry.Title.Contains(featureOffer.Title, StringComparison.OrdinalIgnoreCase) ||
                entry.Title.Contains(FeatureRelease(featureOffer.Title), StringComparison.OrdinalIgnoreCase)))
            entries.Add(featureOffer);
        return entries
            .OrderBy(entry => entry.Category == "Versjonsoppgradering" ? 0 : 1)
            .ThenBy(entry => entry.Category)
            .ThenBy(entry => entry.Title)
            .ToList();
    }
    private static string ValueOrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    public static string Diagnostics()
    {
        var lines = new List<string> { "UPDATE EXPRESS v" + ProductVersion.Display + " – DIAGNOSTICS", "Tid: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "Maskin: " + Environment.MachineName, "OS: " + Environment.OSVersion, "" };
        try
        {
            dynamic session = Session(); dynamic searcher = session.CreateUpdateSearcher();
            lines.Add("WUA ServerSelection: " + Convert.ToString(searcher.ServerSelection));
            lines.Add("WUA Online: " + Convert.ToString(searcher.Online));
            lines.Add("WUA CanAutomaticallyUpgradeService: " + Convert.ToString(searcher.CanAutomaticallyUpgradeService));
        }
        catch (Exception ex) { lines.Add("WUA metadata: " + ex.Message); }
        try
        {
            var pme = PmeServiceControl.ReadState("PME.Agent.PmeService");
            lines.Add($"N-able PME: {(pme.Exists ? pme.State + " / " + pme.StartMode : "Not detected")}");
            lines.Add("Driver default query: assigned drivers plus every pending firmware update");
            lines.Add("Driver raw query: IsInstalled=0 and IsHidden=0 and Type='Driver'");
        }
        catch (Exception ex) { lines.Add("PME status check: " + ex.Message); }
        foreach (var path in new[] { @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU" })
        {
            lines.Add("\nPolicy: HKLM\\" + path);
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (key is null) { lines.Add("  (Ingen policy-nøkkel)"); continue; }
                foreach (var name in key.GetValueNames().OrderBy(n => n))
                {
                    var value = key.GetValue(name);
                    lines.Add("  " + name + " = " + (value is string[] arr ? string.Join(", ", arr) : Convert.ToString(value)));
                }
            }
            catch (Exception ex) { lines.Add("  Feil: " + ex.Message); }
        }
        lines.Add("\nOmstartindikatorer (ikke uttømmende):");
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending" })
        {
            try { using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path); lines.Add("  " + path + " = " + (key is null ? "Nei" : "Ja")); }
            catch (Exception ex) { lines.Add("  " + path + " = ukjent: " + ex.Message); }
        }
        // Read-only WUA inventory: do not download, install, accept EULAs or modify settings.
        lines.Add("\n=== WUA RAW SEARCH DIAGNOSTICS (READ ONLY) ===");
        lines.Add("Search results are independent snapshots; entries can occur in multiple queries.");
        try
        {
            dynamic diagnosticSession = Session();
            dynamic diagnosticSearcher = diagnosticSession.CreateUpdateSearcher();
            var queries = new (string Name, string Criteria)[]
            {
                ("Software pending / visible", "IsInstalled=0 and IsHidden=0 and Type='Software'"),
                ("Software pending / browse only", "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=1"),
                ("Software pending / not browse only", "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0"),
                ("Software pending / including hidden", "IsInstalled=0 and Type='Software'"),
                ("Drivers pending / visible", "IsInstalled=0 and IsHidden=0 and Type='Driver'"),
                ("Drivers pending / assigned", "IsInstalled=0 and IsHidden=0 and Type='Driver' and IsAssigned=1")
            };
            foreach (var query in queries)
            {
                lines.Add("\nQUERY: " + query.Name);
                lines.Add("CRITERIA: " + query.Criteria);
                try
                {
                    dynamic results = diagnosticSearcher.Search(query.Criteria);
                    int count = (int)results.Updates.Count;
                    lines.Add("RAW COUNT: " + count);
                    int limit = Math.Min(count, 150);
                    for (int i = 0; i < limit; i++)
                    {
                        dynamic u = results.Updates.Item(i);
                        string title = Convert.ToString(u.Title) ?? "";
                        string id = ""; string revision = "";
                        try { id = Convert.ToString(u.Identity.UpdateID) ?? ""; revision = Convert.ToString(u.Identity.RevisionNumber) ?? ""; } catch { }
                        string browse = "?", hidden = "?", mandatory = "?", beta = "?", installed = "?", severity = "";
                        try { browse = Convert.ToString(u.BrowseOnly) ?? "?"; } catch { }
                        try { hidden = Convert.ToString(u.IsHidden) ?? "?"; } catch { }
                        try { mandatory = Convert.ToString(u.IsMandatory) ?? "?"; } catch { }
                        try { beta = Convert.ToString(u.IsBeta) ?? "?"; } catch { }
                        try { installed = Convert.ToString(u.IsInstalled) ?? "?"; } catch { }
                        try { severity = Convert.ToString(u.MsrcSeverity) ?? ""; } catch { }
                        string categories = "";
                        try { var names = new List<string>(); foreach (var c in u.Categories) names.Add(Convert.ToString(c.Name) ?? ""); categories = string.Join("; ", names); } catch { }
                        lines.Add($"  [{i+1}] {title} | KB={KBs(u)} | UpdateID={id} | Rev={revision} | Installed={installed} | Hidden={hidden} | BrowseOnly={browse} | Mandatory={mandatory} | Beta={beta} | MSRC={severity} | Categories={categories}");
                        if (query.Name == "Drivers pending / visible")
                        {
                            lines.Add($"    InstallationBehavior: CanRequestUserInput={WuaProperty(() => u.InstallationBehavior.CanRequestUserInput)} | RebootBehavior={WuaProperty(() => u.InstallationBehavior.RebootBehavior)} | Impact={WuaProperty(() => u.InstallationBehavior.Impact)} | IsDownloaded={WuaProperty(() => u.IsDownloaded)} | EulaAccepted={WuaProperty(() => u.EulaAccepted)}");
                        }
                    }
                    if (count > limit) lines.Add($"  TRUNCATED: {count-limit} additional entries omitted (max 150 per query)");
                }
                catch (Exception ex) { lines.Add("QUERY ERROR: " + ex); }
            }
        }
        catch (Exception ex) { lines.Add("WUA diagnostic initialization error: " + ex); }
        lines.Add("\n=== DRIVER CLASSIFICATION (READ ONLY) ===");
        try
        {
            foreach (var entry in AdvancedPending(includeOptional: true, allDrivers: true)
                .Where(e => e.Category == "Driver / firmware"))
            {
                lines.Add($"Driver: {entry.DisplayTitle} | Offered: {entry.DriverVersion} | Status: {entry.DriverStatus} | WUA Assigned: {entry.DriverAssigned} | Auto-selected: {entry.IsSelected}");
                lines.Add($"  Hardware ID: {ValueOrUnknown(entry.DriverHardwareId)} | Model: {ValueOrUnknown(entry.DriverModel)} | Manufacturer: {ValueOrUnknown(entry.DriverManufacturer)} | Class: {ValueOrUnknown(entry.DriverClass)}");
                lines.Add($"  Installed version: {ValueOrUnknown(entry.InstalledDriverVersion)} | Reason: {entry.DriverReason}");
            }
        }
        catch (Exception ex) { lines.Add("Driver classification diagnostic error: " + ex.Message); }
        lines.Add("\n=== FEATURE UPDATES ===");
        lines.AddRange(FeatureUpdateOfferReader.DiagnosticLines());
        lines.Add("\nInterpretation: WUA search results can differ from cached Windows Settings offers and may depend on policy/service. No installation performed.");
        lines.Add("\nMerk: WUA-søk følger PC-ens konfigurerte kilde og policy. Ikke alle valgfrie oppdateringer er nødvendigvis tilgjengelige via COM API.");
        return string.Join(Environment.NewLine, lines);
    }
    private static string FeatureRelease(string title)
    {
        var match = Regex.Match(title ?? "", @"\d{2}H[12]", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : title ?? "";
    }
    private static string WuaProperty(Func<object?> get)
    {
        try { return Convert.ToString(get()) ?? "Unknown"; }
        catch (Exception ex) { return "Unavailable (" + ex.GetType().Name + ")"; }
    }

    public static List<UpdateEntry> Pending()
    {
        dynamic session = Session();
        dynamic searcher = session.CreateUpdateSearcher();
        // Uses the machine's configured update source and policy; no installation.
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
        var entries = new List<UpdateEntry>();
        for (int i = 0; i < (int)result.Updates.Count; i++)
        {
            dynamic update = result.Updates.Item(i);
            entries.Add(new UpdateEntry { KB = KBs(update), Title = Convert.ToString(update.Title) ?? "", Status = "Tilgjengelig", Source = "Windows Update Agent" });
        }
        return entries;
    }
    public static List<UpdateEntry> History()
    {
        dynamic session = Session();
        dynamic searcher = session.CreateUpdateSearcher();
        int count = (int)searcher.GetTotalHistoryCount();
        var entries = new List<UpdateEntry>();
        // WUA history can be extensive; display latest 500 entries.
        if (count == 0) return entries;
        dynamic history = searcher.QueryHistory(0, Math.Min(count, 500));
        for (int i = 0; i < (int)history.Count; i++)
        {
            dynamic item = history.Item(i);
            int code = (int)item.ResultCode;
            string title = Convert.ToString(item.Title) ?? "";
            var matches = System.Text.RegularExpressions.Regex.Matches(title, @"KB\d{5,9}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var kbs = matches.Select(m => m.Value.ToUpperInvariant()).Distinct();
            int hr = (int)item.HResult;
            var entry = new UpdateEntry
            {
                KB = string.Join(", ", kbs), Title = title,
                Status = code switch { 2 => "Vellykket", 3 => "Vellykket med feil", 4 => "Mislykket", 5 => "Avbrutt", _ => "Ukjent" },
                Date = ((DateTime)item.Date).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Source = "WUA-historikk", ErrorCode = hr == 0 ? "" : $"0x{unchecked((uint)hr):X8}"
            };
            OperationalPriority.Apply(entry);
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// History for the grid: live WUA events plus events saved before a Windows Update fix.
    /// A failed row is omitted when the same update already succeeded. Other failures
    /// with a later success for the same KB are shown as Fixed.
    /// Fully identical rows (same title, KB, version, and status) collapse to the newest Date/Time.
    /// </summary>
    public static List<UpdateEntry> HistoryForDisplay() =>
        HistoryArchive.MergeForDisplay(History());
    /// <summary>
    /// Category 1 automation / manual install: update Defender security intelligence via Microsoft Update.
    /// Does not reboot Windows.
    /// </summary>
    public static UpdateEntry EnsureDefenderSignatures(Action<string>? report = null) =>
        InstallDefenderSignatures(new UpdateEntry
        {
            KB = "KB2267602",
            Title = "Microsoft Defender Antivirus Security Intelligence"
        }, report);

    // KB2267602 is a Defender intelligence/signature update. Use Defender's supported
    // update mechanism rather than WUA Downloader, which can fail immediately for definitions.
    private static UpdateEntry InstallDefenderSignatures(UpdateEntry selected, Action<string>? report)
    {
        report?.Invoke("Updating Microsoft Defender security intelligence via Microsoft Update…");
        // PowerShell emits the final signature version on its last stdout line.
        const string script = "$ErrorActionPreference='Stop'; $before=(Get-MpComputerStatus -ErrorAction Stop).AntivirusSignatureVersion; Update-MpSignature -UpdateSource MicrosoftUpdateServer -ErrorAction Stop; $after=(Get-MpComputerStatus -ErrorAction Stop).AntivirusSignatureVersion; Write-Output ('BEFORE=' + $before); Write-Output ('AFTER=' + $after)";
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Kunne ikke starte Microsoft Defender-oppdatering.");
        // Read both streams asynchronously to avoid pipe-buffer deadlocks.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string output = outputTask.GetAwaiter().GetResult().Trim();
        string error = errorTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Defender-oppdatering feilet (PowerShell exit {process.ExitCode}). {error} {output}".Trim());
        string installed = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => line.StartsWith("AFTER=", StringComparison.OrdinalIgnoreCase))?.Substring(6).Trim() ?? "";
        string before = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line => line.StartsWith("BEFORE=", StringComparison.OrdinalIgnoreCase))?.Substring(7).Trim() ?? "";
        var requestedMatch = Regex.Match(selected.Title, @"(?:versjon|version)\s+(\d+(?:\.\d+){2,3})", RegexOptions.IgnoreCase);
        if (!Version.TryParse(installed, out var installedVersion))
            throw new InvalidOperationException($"Defender returnerte ingen gyldig signaturversjon. Output: {output}");
        if (requestedMatch.Success && Version.TryParse(requestedMatch.Groups[1].Value, out var requestedVersion) && installedVersion < requestedVersion)
            throw new InvalidOperationException($"Defender rapporterer {installed} etter oppdatering, men valgt oppdatering krever minst {requestedVersion}.");
        bool changed = Version.TryParse(before, out var beforeVersion) && installedVersion > beforeVersion;
        bool alreadyCurrent = !changed && requestedMatch.Success && Version.TryParse(requestedMatch.Groups[1].Value, out var offeredVersion) && installedVersion >= offeredVersion;
        string verification = changed ? "Installed" : alreadyCurrent ? "Already up to date" : "Verification unavailable";
        report?.Invoke($"Defender signatures: {before} -> {installed}; {verification}.");
        return new UpdateEntry { KB = selected.KB, Title = selected.Title, Status = changed ? "Installert" : alreadyCurrent ? "Already up to date" : "Verification unavailable",
            Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Source = $"Update Express — Defender signatures ({before} -> {installed})" };
    }

    public static List<UpdateEntry> InstallSelected(List<UpdateEntry> selectedUpdates, Action<string>? report = null, Action<WuaActivity>? activity = null)
    {
        if (selectedUpdates.Count == 0) throw new InvalidOperationException("Ingen oppdateringer valgt.");
        var entries = new List<UpdateEntry>();
        var defender = selectedUpdates.Where(x => x.KB.Split(',', StringSplitOptions.TrimEntries)
            .Any(kb => kb.Equals("KB2267602", StringComparison.OrdinalIgnoreCase))).ToList();
        var standard = selectedUpdates.Except(defender).ToList();
        // Process Defender separately, with an explicit, supported signature-update API.
        for (int i = 0; i < defender.Count; i++)
        {
            var update = defender[i];
            string kb = string.IsNullOrWhiteSpace(update.KB) ? "KB2267602" : update.KB.Split(',')[0].Trim();
            activity?.Invoke(new WuaActivity("install", kb, i + 1, defender.Count, -1));
            report?.Invoke(WuaProgressText.Stage(WuaProgressText.Norwegian, "install", kb, i + 1, defender.Count, -1));
            entries.Add(InstallDefenderSignatures(update, report));
        }
        if (standard.Count == 0) return entries;
        entries.AddRange(InstallSelectedWua(standard.Select(x => x.UpdateId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), report, activity));
        return entries;
    }

    /// <summary>
    /// Category 2/3: download selected software updates without installing.
    /// Returns the number of updates marked IsDownloaded.
    /// </summary>
    public static int DownloadSelected(List<string> updateIds, Action<string>? report = null)
    {
        var collection = CollectSoftwareUpdates(updateIds, report, out _);
        if ((int)collection.Count == 0)
            throw new InvalidOperationException("No selected updates could be prepared for download.");
        dynamic session = Session();
        dynamic download = WuaJobs.Download(session, collection, report, null);
        int downloadCode = (int)download.ResultCode;
        int downloadedCount = 0;
        for (int i = 0; i < (int)collection.Count; i++)
        {
            try
            {
                if ((bool)collection.Item(i).IsDownloaded) downloadedCount++;
            }
            catch { }
        }
        report?.Invoke($"Download phase completed: {downloadedCount}/{collection.Count} (result={downloadCode}).");
        if (downloadCode != 2 && downloadCode != 3)
            throw new InvalidOperationException($"Download failed: result code {downloadCode}.");
        if (downloadedCount == 0)
            throw new InvalidOperationException("No updates were downloaded.");
        return downloadedCount;
    }

    // Install one explicitly selected driver or firmware update, with a fresh WUA identity
    // and hardware-ID check. Never automatically install or reboot.
    public static List<UpdateEntry> InstallSelectedDriver(UpdateEntry selected, Action<string>? report = null, bool allowInteractive = false, Action<WuaActivity>? activity = null)
    {
        if (selected.Category != "Driver / firmware")
            throw new InvalidOperationException("Only driver and firmware updates can be installed in driver mode.");
        if (string.IsNullOrWhiteSpace(selected.DriverHardwareId))
            throw new InvalidOperationException("Driver hardware ID was not verified.");
        report?.Invoke("Rechecking driver identity through Windows Update…");
        dynamic session = Session();
        dynamic searcher = session.CreateUpdateSearcher();
        const string criteria = "IsInstalled=0 and IsHidden=0 and Type='Driver'";
        // Do not silently substitute another driver or bypass hardware identity checks.
        dynamic search = searcher.Search(criteria);
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        var observed = new List<string>();
        var sameId = new List<(int Index, string Revision, string HardwareId)>();
        for (int i = 0; i < (int)search.Updates.Count; i++)
        {
            dynamic candidate = search.Updates.Item(i);
            string id = Convert.ToString(candidate.Identity.UpdateID) ?? "";
            string revision = Convert.ToString(candidate.Identity.RevisionNumber) ?? "";
            string hw = "";
            try { hw = Convert.ToString(candidate.DriverHardwareID) ?? ""; } catch { }
            observed.Add($"{id} rev={revision} hardware={hw}");
            if (id.Equals(selected.UpdateId, StringComparison.OrdinalIgnoreCase))
                sameId.Add((i, revision, hw));
        }
        report?.Invoke($"WUA driver recheck: {observed.Count} candidate(s), {sameId.Count} matching UpdateID(s).");
        // Persist diagnostic context even when the recheck fails, without losing it
        // behind the generic UI exception. This file is local and append-only.
        string auditDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UpdateExpress");
        try
        {
            Directory.CreateDirectory(auditDir);
            File.AppendAllText(Path.Combine(auditDir, "driver-recheck.log"),
                $"[{DateTimeOffset.Now:O}] criteria={criteria}; requested={selected.UpdateId}; hardware={selected.DriverHardwareId}; count={observed.Count}\n" +
                string.Join("\n", observed) + "\n", Encoding.UTF8);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Driver recheck logging failed: " + ex.Message); }
        if (sameId.Count == 0)
            throw new InvalidOperationException($"Selected driver was not returned by the installation-time WUA search ({observed.Count} result(s)). See C:\\ProgramData\\UpdateExpress\\driver-recheck.log. No installation was attempted.");
        var matchingHardware = sameId.Where(x => x.HardwareId.Equals(selected.DriverHardwareId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matchingHardware.Count != 1)
            throw new InvalidOperationException($"Driver identity recheck failed: {sameId.Count} UpdateID match(es), {matchingHardware.Count} exact hardware-ID match(es). No installation was attempted.");
        dynamic update = search.Updates.Item(matchingHardware[0].Index);
        string canRequestInput = WuaProperty(() => update.InstallationBehavior.CanRequestUserInput);
        string rebootBehavior = WuaProperty(() => update.InstallationBehavior.RebootBehavior);
        string impact = WuaProperty(() => update.InstallationBehavior.Impact);
        string isDownloaded = WuaProperty(() => update.IsDownloaded);
        string behaviorLine = $"Driver install requirements: CanRequestUserInput={canRequestInput}; RebootBehavior={rebootBehavior}; Impact={impact}; IsDownloaded={isDownloaded}";
        report?.Invoke(behaviorLine);
        try { File.AppendAllText(Path.Combine(auditDir, "driver-recheck.log"), behaviorLine + Environment.NewLine, Encoding.UTF8); }
        catch (Exception ex) { Debug.WriteLine("Driver behavior logging failed: " + ex.Message); }
        // Log each WUA stage and the native COM HRESULT. The managed exception
        // HResult (e.g. 0x80131509) is not necessarily the WUA error code.
        void Audit(string stage, string detail)
        {
            string line = $"[{DateTimeOffset.Now:O}] stage={stage}; update={selected.UpdateId}; hardware={selected.DriverHardwareId}; {detail}";
            try { File.AppendAllText(Path.Combine(auditDir, "driver-recheck.log"), line + Environment.NewLine, Encoding.UTF8); }
            catch (Exception logEx) { Debug.WriteLine("Driver audit logging failed: " + logEx.Message); }
            report?.Invoke($"Driver {stage}: {detail}");
        }
        static string HResultHex(int value) => $"0x{unchecked((uint)value):X8}";
        static string ErrorDetails(Exception ex)
        {
            var parts = new List<string>();
            for (Exception? current = ex; current != null; current = current.InnerException)
                parts.Add($"{current.GetType().FullName}: {current.Message} (HRESULT={HResultHex(current.HResult)})");
            return string.Join(" -> ", parts);
        }
        string stageName = "SafetyCheck";
        try
        {
            Audit(stageName, behaviorLine);
            if (!bool.TryParse(canRequestInput, out bool requiresInput))
                throw new InvalidOperationException("Cannot verify driver interaction requirements. No installation attempted.");
            if (requiresInput && !allowInteractive)
                throw new InvalidOperationException("Driver may require interaction. Explicit interactive consent is required; no installation attempted.");
            stageName = "Eula";
            Audit(stageName, "Checking acceptance");
            if (!(bool)update.EulaAccepted) update.AcceptEula();
            collection.Add(update);
            stageName = "Download";
            Audit(stageName, "Starting WUA download");
            dynamic download = WuaJobs.Download(session, collection, report, activity);
            int downloadCode = (int)download.ResultCode;
            dynamic downloadItem = download.GetUpdateResult(0);
            int downloadHr = (int)downloadItem.HResult;
            bool downloaded = (bool)collection.Item(0).IsDownloaded;
            Audit(stageName, $"ResultCode={downloadCode}; ItemHRESULT={HResultHex(downloadHr)}; IsDownloaded={downloaded}");
            if (downloadCode != 2 || !downloaded)
                throw new InvalidOperationException($"WUA driver download failed: result={downloadCode}, HRESULT={HResultHex(downloadHr)}, IsDownloaded={downloaded}");
            stageName = "Install";
            Audit(stageName, "Starting WUA installation");
            dynamic result = WuaJobs.Install(session, collection, report, activity, requiresInput && allowInteractive);
            dynamic item = result.GetUpdateResult(0);
            int code = (int)item.ResultCode;
            int hr = (int)item.HResult;
            bool restart = false; try { restart = (bool)result.RebootRequired; } catch { }
            Audit(stageName, $"ResultCode={code}; ItemHRESULT={HResultHex(hr)}; RebootRequired={restart}");
            return new List<UpdateEntry> { new UpdateEntry {
                Title = selected.Title, Category = selected.Category, UpdateId = selected.UpdateId,
                Status = code == 2 ? "Installert" : code == 3 ? "Delvis fullført" : "Feilet / avbrutt",
                ErrorCode = hr == 0 ? "" : HResultHex(hr),
                Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Source = restart ? "Windows Update Agent — Restart required" : "Windows Update Agent"
            } };
        }
        catch (Exception ex)
        {
            Audit("Failed", $"At={stageName}; {ErrorDetails(ex)}");
            // Preserve native COM details in the activity log; never mistake a
            // generic managed exception HRESULT for the driver installation code.
            throw new InvalidOperationException($"Driver {stageName} failed: {ErrorDetails(ex)}", ex);
        }
    }

    private static dynamic CollectSoftwareUpdates(List<string> updateIds, Action<string>? report, out List<string> rejected)
    {
        if (updateIds.Count == 0) throw new InvalidOperationException("Ingen oppdateringer valgt.");
        report?.Invoke("Searching for selected Windows updates…");
        dynamic session = Session();
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic search = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
        int total = (int)search.Updates.Count;
        report?.Invoke($"WUA search returned {total} software update(s).");
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        var selectedIds = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);
        var foundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        rejected = new List<string>();
        for (int i = 0; i < total; i++)
        {
            dynamic update = search.Updates.Item(i);
            string id = Convert.ToString(update.Identity.UpdateID) ?? "";
            if (!selectedIds.Contains(id)) continue;
            foundIds.Add(id);
            string kb = KBs(update);
            string revision = "?";
            try { revision = Convert.ToString(update.Identity.RevisionNumber) ?? "?"; } catch { }
            // Match by WUA UpdateID, never by translated category/title or KB alone.
            bool installed = false, hidden = false, inputRequired = false;
            try { installed = (bool)update.IsInstalled; } catch { }
            try { hidden = (bool)update.IsHidden; } catch { }
            try { inputRequired = (bool)update.InstallationBehavior.CanRequestUserInput; } catch { }
            string identity = $"{kb} ID={id} revision={revision}";
            if (installed || hidden || inputRequired)
            {
                string reason = installed ? "already installed" : hidden ? "hidden" : "requires interactive input";
                rejected.Add($"{identity}: {reason}");
                report?.Invoke($"Skipped {identity}: {reason}");
                continue;
            }
            try
            {
                if (!(bool)update.EulaAccepted) update.AcceptEula();
                collection.Add(update);
                report?.Invoke($"Matched selected update: {identity}");
            }
            catch (Exception ex)
            {
                rejected.Add($"{identity}: {ex.Message}");
                report?.Invoke($"Skipped {identity}: {ex.Message}");
            }
        }
        foreach (string missing in selectedIds.Except(foundIds, StringComparer.OrdinalIgnoreCase))
        {
            rejected.Add($"ID={missing}: not returned by current WUA search (possibly superseded or already installed)");
            report?.Invoke($"Selected update not found in fresh search: {missing}");
        }
        if ((int)collection.Count == 0)
            throw new InvalidOperationException($"Ingen valgte oppdateringer kan installeres. WUA returnerte {total} oppdatering(er); {foundIds.Count} valgte ID-er ble funnet. " +
                (rejected.Count == 0 ? "Skann på nytt." : "Detaljer: " + string.Join(" | ", rejected.Take(5))));
        return collection;
    }

    private static List<UpdateEntry> InstallSelectedWua(List<string> updateIds, Action<string>? report = null, Action<WuaActivity>? activity = null)
    {
        dynamic collection = CollectSoftwareUpdates(updateIds, report, out _);
        dynamic session = Session();
        dynamic download = WuaJobs.Download(session, collection, report, activity);
        int downloadCode = (int)download.ResultCode;
        int downloadHr = 0;
        try { downloadHr = (int)download.HResult; } catch { }
        report?.Invoke($"WUA download result={downloadCode}, HResult=0x{unchecked((uint)downloadHr):X8}");
        int downloadedCount = 0;
        for (int i = 0; i < (int)collection.Count; i++)
        {
            try
            {
                dynamic itemResult = download.GetUpdateResult(i);
                int itemCode = (int)itemResult.ResultCode;
                int itemHr = (int)itemResult.HResult;
                if ((bool)collection.Item(i).IsDownloaded)
                {
                    downloadedCount++;
                    activity?.Invoke(new WuaActivity("downloaded", WuaJobs.Label(collection, i), i + 1, (int)collection.Count, 100));
                }
                report?.Invoke($"Downloaded {downloadedCount}/{collection.Count} updates — checked {i + 1}/{collection.Count}: {KBs(collection.Item(i))} (result={itemCode}, HRESULT=0x{unchecked((uint)itemHr):X8})");
            }
            catch (Exception ex) { report?.Invoke($"WUA item diagnostic unavailable: {ex.Message}"); }
        }
        report?.Invoke($"Download phase completed: {downloadedCount}/{collection.Count} updates downloaded.");
        if (downloadCode != 2 && downloadCode != 3)
            throw new InvalidOperationException($"Nedlasting feilet: resultatkode {downloadCode}, HResult=0x{unchecked((uint)downloadHr):X8}. Se installasjonsloggen.");
        dynamic ready = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        for (int i = 0; i < (int)collection.Count; i++)
        {
            dynamic u = collection.Item(i);
            if ((bool)u.IsDownloaded) ready.Add(u);
        }
        if ((int)ready.Count == 0) throw new InvalidOperationException("Ingen oppdateringer ble lastet ned.");
        report?.Invoke($"Installing {ready.Count} downloaded update(s)…");
        dynamic result = WuaJobs.Install(session, ready, report, activity, false);
        bool restartRequired = false;
        try { restartRequired = (bool)result.RebootRequired; } catch { }
        report?.Invoke(restartRequired ? "Installation completed — restart required." : "Installation completed — checking results…");
        var entries = new List<UpdateEntry>();
        for (int i = 0; i < (int)ready.Count; i++)
        {
            dynamic u = ready.Item(i);
            dynamic r = result.GetUpdateResult(i);
            int code = (int)r.ResultCode;
            int hr = (int)r.HResult;
            entries.Add(new UpdateEntry { KB = KBs(u), Title = Convert.ToString(u.Title) ?? "", Status = code == 2 ? "Installert" : code == 3 ? "Delvis fullført" : "Feilet / avbrutt", Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Source = restartRequired ?  $"Update Express v{ProductVersion.Display} — Restart required" : $"Update Express v{ProductVersion.Display}", ErrorCode = hr == 0 ? "" : $"0x{unchecked((uint)hr):X8}" });
        }
        return entries;
    }
    public static List<UpdateEntry> PendingWithIds()
    {
        dynamic session = Session();
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
        var entries = new List<UpdateEntry>();
        for (int i = 0; i < (int)result.Updates.Count; i++)
        {
            dynamic u = result.Updates.Item(i);
            entries.Add(new UpdateEntry { KB = KBs(u), Title = Convert.ToString(u.Title) ?? "", Status = "Tilgjengelig", Source = "Windows Update Agent", UpdateId = Convert.ToString(u.Identity.UpdateID) ?? "" });
        }
        return entries;
    }
}
