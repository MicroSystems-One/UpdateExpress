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
        // Collapse verified lookalike WUA driver offers before Search / Installation Manager.
        // Only UpdateId and Title+DriverVersion+HardwareId — never drop a different HWID.
        entries = DedupePendingDrivers(entries);
        // Drivers that already installed with Restart required stay hidden while reboot is pending.
        // Do not hard-drop Older/Same vs PnP — Extension INF packages share HWID with base drivers.
        entries = DriverRebootHoldStore.FilterAvailable(entries);
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

    // Collapse only when package identity is the same UpdateId, or the same Title+Version+HardwareId.
    // Distinct HardwareIds stay — InstallSelectedDriver installs one UpdateId per exact HWID check.
    internal static List<UpdateEntry> DedupePendingDrivers(List<UpdateEntry> entries)
    {
        var drivers = entries.Where(e => e.Category == "Driver / firmware").ToList();
        if (drivers.Count <= 1) return entries;
        var others = entries.Where(e => e.Category != "Driver / firmware").ToList();

        static UpdateEntry Prefer(IEnumerable<UpdateEntry> group) =>
            group
                .OrderByDescending(d => d.DriverAssigned)
                .ThenByDescending(d => string.Equals(d.DriverClassification, "Firmware", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(d => !string.IsNullOrWhiteSpace(d.DriverHardwareId))
                .ThenBy(d => d.UpdateId, StringComparer.OrdinalIgnoreCase)
                .First();

        // 1) Same UpdateId (revision-insensitive) → one row. Rows without UpdateId stay as-is.
        var byUpdateId = drivers
            .Where(d => !string.IsNullOrWhiteSpace(d.UpdateId))
            .GroupBy(d => d.UpdateId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(Prefer)
            .Concat(drivers.Where(d => string.IsNullOrWhiteSpace(d.UpdateId)))
            .ToList();

        // 2) Same Title + WUA DriverVersion + HardwareId → one row (verified same target).
        var collapsed = byUpdateId
            .GroupBy(d =>
                    $"{(d.Title ?? "").Trim()}\u001f{(d.DriverVersionFromWua ?? "").Trim()}\u001f{(d.DriverHardwareId ?? "").Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Select(Prefer)
            .ToList();

        return others.Concat(collapsed).ToList();
    }
    private static string ValueOrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value;

    /// <summary>
    /// Explicit Diagnostics only. Extended WUA searches are not used by normal scan,
    /// Silent, Tray, or RMM paths. Read-only: no download, install, EULA, or source changes.
    /// </summary>
    public static string Diagnostics()
    {
        var totalSw = Stopwatch.StartNew();
        var lines = new List<string>
        {
            "UPDATE EXPRESS v" + ProductVersion.Display + " – DIAGNOSTICS",
            "Tid: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "Maskin: " + Environment.MachineName,
            "OS: " + Environment.OSVersion
        };
        lines.AddRange(WindowsBuildDiagnosticLines());
        lines.Add("");

        string serverSelectionRaw = "?";
        string onlineRaw = "?";
        string canUpgradeRaw = "?";
        string serviceIdRaw = "";
        try
        {
            dynamic session = Session();
            dynamic searcher = session.CreateUpdateSearcher();
            try { searcher.Online = true; } catch { /* keep configured Online if assignment fails */ }
            serverSelectionRaw = Convert.ToString(searcher.ServerSelection) ?? "?";
            onlineRaw = Convert.ToString(searcher.Online) ?? "?";
            canUpgradeRaw = Convert.ToString(searcher.CanAutomaticallyUpgradeService) ?? "?";
            try { serviceIdRaw = Convert.ToString(searcher.ServiceID) ?? ""; } catch { serviceIdRaw = ""; }
        }
        catch (Exception ex) { lines.Add("WUA metadata: " + ex.Message); }

        lines.Add("=== DISCOVERY / POLICY SNAPSHOT ===");
        lines.Add("Discovery Method: WUA COM");
        lines.Add("Optional Included: Diagnostics always probes BrowseOnly (Diagnostics button only; not scan/Silent/Tray/RMM)");
        lines.Add("Server Selection: " + FormatServerSelection(serverSelectionRaw));
        lines.Add("Service ID: " + FormatServiceId(serviceIdRaw));
        lines.Add("WUA Online: " + onlineRaw);
        lines.Add("WUA CanAutomaticallyUpgradeService: " + canUpgradeRaw);
        lines.Add("Preview Auto-Install Policy: Blocked by application design");
        lines.Add("Insider Auto-Install Policy: Not supported by application");
        lines.Add("Settings Offer Parity: Not guaranteed");
        lines.Add("Note: Application policy above is Update Express design, not a verified machine Insider/WU audit.");
        lines.Add("");

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

        lines.Add("\n=== WUA RAW SEARCH DIAGNOSTICS (READ ONLY) ===");
        lines.Add("Each query logs ISearchResult.ResultCode / Warnings / raw count / elapsed ms before app filtering.");
        lines.Add("A search that throws no exception is not automatically Succeeded — use ResultCode.");
        lines.Add("Failed or SucceededWithErrors with RAW COUNT 0 does not mean the PC is fully up to date.");
        lines.Add("Search results are independent snapshots; the same UpdateID:Revision can appear in multiple queries.");

        // Keyed snapshots reused for set compare + Preview/KB title scan (no repeated Search calls).
        var snapshots = new Dictionary<string, List<DiagnosticUpdateRow>>(StringComparer.OrdinalIgnoreCase);
        // ResultCode: WUA OperationResultCode, -1 when unreadable, -2 when Search threw.
        // WarningCount: -1 when ISearchResult.Warnings could not be read.
        var queryOutcomes = new List<(string Name, string Outcome, int Count, long Ms, int ResultCode, int WarningCount)>();

        var queries = new (string Name, string Criteria)[]
        {
            ("A pending / visible (no Type)", "IsInstalled=0 and IsHidden=0"),
            ("B software pending / visible", "IsInstalled=0 and IsHidden=0 and Type='Software'"),
            ("C software pending / browse only", "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=1"),
            ("D software pending / not browse only", "IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0"),
            ("Software pending / including hidden", "IsInstalled=0 and Type='Software'"),
            ("Drivers pending / visible", "IsInstalled=0 and IsHidden=0 and Type='Driver'"),
            ("Drivers pending / assigned", "IsInstalled=0 and IsHidden=0 and Type='Driver' and IsAssigned=1")
        };

        try
        {
            dynamic diagnosticSession = Session();
            dynamic diagnosticSearcher = diagnosticSession.CreateUpdateSearcher();
            try { diagnosticSearcher.Online = true; } catch { }
            lines.Add("Searcher Online for diagnostic queries: " + WuaProperty(() => diagnosticSearcher.Online));
            lines.Add("Searcher ServerSelection: " + FormatServerSelection(Convert.ToString(diagnosticSearcher.ServerSelection) ?? "?"));
            try { lines.Add("Searcher ServiceID: " + FormatServiceId(Convert.ToString(diagnosticSearcher.ServiceID) ?? "")); }
            catch { lines.Add("Searcher ServiceID: default / not explicitly set"); }

            foreach (var query in queries)
            {
                lines.Add("\nQUERY: " + query.Name);
                lines.Add("CRITERIA: " + query.Criteria);
                var rowList = new List<DiagnosticUpdateRow>();
                snapshots[query.Name] = rowList;
                var sw = Stopwatch.StartNew();
                try
                {
                    dynamic results = diagnosticSearcher.Search(query.Criteria);
                    sw.Stop();
                    int resultCode = -1;
                    try { resultCode = (int)results.ResultCode; } catch { }
                    string outcome = FormatOperationResultCode(resultCode);
                    string warnings = FormatSearchWarnings(results);
                    int warningCount = SearchWarningCount(results);
                    int count = 0;
                    try { count = (int)results.Updates.Count; } catch { }
                    lines.Add("RESULT CODE: " + outcome + " (raw=" + resultCode + ")");
                    lines.Add("WARNINGS: " + warnings);
                    lines.Add("RAW COUNT (pre-app-filter): " + count);
                    lines.Add("ELAPSED MS: " + sw.ElapsedMilliseconds);
                    if (resultCode != 2)
                        lines.Add("NOTE: ResultCode is not Succeeded. Do not treat this empty/partial result as proof the machine is fully up to date.");

                    // Cache every COM row for set compare / Preview scan; truncate only the printed report.
                    const int displayLimit = 150;
                    for (int i = 0; i < count; i++)
                    {
                        dynamic u = results.Updates.Item(i);
                        var row = CaptureDiagnosticRow(u);
                        rowList.Add(row);
                        if (i >= displayLimit) continue;
                        lines.Add($"  [{i + 1}] {row.Title} | KB={row.Kb} | UpdateID={row.UpdateId} | Rev={row.Revision} | Installed={row.Installed} | Hidden={row.Hidden} | BrowseOnly={row.BrowseOnly} | AutoSelectOnWebSites={row.AutoSelectOnWebSites} | Mandatory={row.Mandatory} | Assigned={row.Assigned} | DeploymentAction={row.DeploymentAction} | RebootRequired={row.RebootRequired} | Beta={row.Beta} | MSRC={row.Severity} | Categories={row.Categories} | ServiceID={row.ServiceId}");
                        if (query.Name == "Drivers pending / visible")
                        {
                            lines.Add($"    InstallationBehavior: CanRequestUserInput={WuaProperty(() => u.InstallationBehavior.CanRequestUserInput)} | RebootBehavior={WuaProperty(() => u.InstallationBehavior.RebootBehavior)} | Impact={WuaProperty(() => u.InstallationBehavior.Impact)} | IsDownloaded={WuaProperty(() => u.IsDownloaded)} | EulaAccepted={WuaProperty(() => u.EulaAccepted)}");
                        }
                    }
                    if (count > displayLimit)
                        lines.Add($"  REPORT TRUNCATED: {count - displayLimit} additional entries omitted from display (max {displayLimit}); full cache used for set compare / Preview scan ({rowList.Count} rows).");
                    queryOutcomes.Add((query.Name, outcome, count, sw.ElapsedMilliseconds, resultCode, warningCount));
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    string hr = FormatHResult(ex);
                    lines.Add("QUERY ERROR: " + ex);
                    lines.Add("HRESULT: " + hr);
                    lines.Add("RESULT CODE: Failed (exception; search did not complete)");
                    lines.Add("ELAPSED MS: " + sw.ElapsedMilliseconds);
                    lines.Add("NOTE: Search failed. RAW COUNT 0 after failure does not mean the PC is fully up to date.");
                    queryOutcomes.Add((query.Name, "Failed (exception)", 0, sw.ElapsedMilliseconds, -2, 0));
                }
            }
        }
        catch (Exception ex) { lines.Add("WUA diagnostic initialization error: " + ex); }

        lines.Add("\n=== SET COMPARISON (cached UpdateID:RevisionNumber; no extra Search) ===");
        var setA = ToIdSet(snapshots, "A pending / visible (no Type)");
        var setB = ToIdSet(snapshots, "B software pending / visible");
        var setC = ToIdSet(snapshots, "C software pending / browse only");
        var setD = ToIdSet(snapshots, "D software pending / not browse only");
        lines.Add($"A count={setA.Count}  B count={setB.Count}  C count={setC.Count}  D(BrowseOnly=0) count={setD.Count}");
        lines.Add($"Only in A (not B): {CountOnlyIn(setA, setB)}");
        lines.Add($"Only in B (not A): {CountOnlyIn(setB, setA)}");
        lines.Add($"Only in C (not B): {CountOnlyIn(setC, setB)}  ← BrowseOnly adds IDs beyond default Software when >0");
        lines.Add($"Only in B (not C): {CountOnlyIn(setB, setC)}  ← BrowseOnly=1 narrows vs full Software when >0");
        lines.Add($"Intersection B∩C: {setB.Count - CountOnlyIn(setB, setC)}");

        lines.Add("\n=== PREVIEW / OPTIONAL TITLE SCAN (cached rows; no extra Search) ===");
        bool previewHit = false;
        var previewHitKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in snapshots)
        {
            foreach (var row in kv.Value)
            {
                if (LooksLikePreviewOrOptionalOffer(row))
                {
                    previewHit = true;
                    previewHitKeys.Add(string.IsNullOrWhiteSpace(row.UpdateId) ? row.Title + "|" + row.Kb : row.IdentityKey);
                    lines.Add($"HIT in {kv.Key}: {row.Title} | {row.Kb} | BrowseOnly={row.BrowseOnly} | UpdateID={row.UpdateId}");
                }
            }
        }
        if (!previewHit)
        {
            lines.Add("No Preview/optional-looking titles in cached WUA COM diagnostic rows.");
            lines.Add("If Windows Settings still shows an optional/Preview banner, treat as Observed WUA COM / Windows Settings offer discrepancy.");
            lines.Add("Update Express application filtering is not indicated when COM raw counts are already empty.");
        }

        lines.Add("\n=== QUERY TIMING SUMMARY ===");
        foreach (var q in queryOutcomes)
            lines.Add($"  {q.Name}: {q.Outcome}; count={q.Count}; {q.Ms} ms");

        lines.Add("\n=== DRIVER CLASSIFICATION (READ ONLY) ===");
        lines.Add("Runs the ordinary application scan (AdvancedPending, optional + all drivers): its own WUA searches plus the installed-driver inventory.");
        lines.Add("Those searches are separate from the diagnostic searches above and do not report ResultCode.");
        var driverClassificationSw = Stopwatch.StartNew();
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
        driverClassificationSw.Stop();
        lines.Add("SECTION ELAPSED MS: " + driverClassificationSw.ElapsedMilliseconds);
        lines.Add("\n=== FEATURE UPDATES ===");
        var featureUpdateSw = Stopwatch.StartNew();
        lines.AddRange(FeatureUpdateOfferReader.DiagnosticLines());
        featureUpdateSw.Stop();
        lines.Add("SECTION ELAPSED MS: " + featureUpdateSw.ElapsedMilliseconds);
        lines.Add("\n=== MANUAL PREVIEW / OPTIONAL INSTALL LIMIT ===");
        lines.Add("CollectSoftwareUpdates re-searches default Software only (no BrowseOnly). SafeDefault gates Install Selected and automation.");
        lines.Add("Preview / optional updates that are not SafeDefault must be installed from Windows Update when COM install is unavailable.");
        lines.Add("Install Selected is not extended for Preview/optional in this release (separate future approval).");
        totalSw.Stop();
        lines.Add("\nTOTAL DIAGNOSTICS ELAPSED MS: " + totalSw.ElapsedMilliseconds);
        lines.Add("\nInterpretation: Observed WUA COM / Windows Settings offer discrepancy is possible; Settings may show offers not returned by these WUA searches. No installation performed.");
        lines.Add("Merk: WUA-søk følger PC-ens konfigurerte kilde og policy. Ikke alle valgfrie oppdateringer er nødvendigvis tilgjengelige via COM API.");

        int succeeded = queryOutcomes.Count(q => q.ResultCode == 2);
        int succeededWithErrors = queryOutcomes.Count(q => q.ResultCode == 3);
        int notRun = queries.Length - queryOutcomes.Count;
        int failed = queryOutcomes.Count - succeeded - succeededWithErrors + notRun;
        int searchesWithWarnings = queryOutcomes.Count(q => q.WarningCount > 0);
        int totalWarnings = queryOutcomes.Where(q => q.WarningCount > 0).Sum(q => q.WarningCount);
        int warningsUnreadable = queryOutcomes.Count(q => q.WarningCount < 0);
        int rawRows = queryOutcomes.Sum(q => q.Count);
        var uniqueUpdates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rows in snapshots.Values)
            foreach (var row in rows)
                if (!string.IsNullOrWhiteSpace(row.UpdateId))
                    uniqueUpdates.Add(row.IdentityKey);
        long searchMs = queryOutcomes.Sum(q => q.Ms);
        long otherMs = Math.Max(0, totalSw.ElapsedMilliseconds - searchMs - driverClassificationSw.ElapsedMilliseconds - featureUpdateSw.ElapsedMilliseconds);

        lines.Add("\n=== DIAGNOSTICS SUMMARY ===");
        lines.Add($"Diagnostic WUA searches: {queries.Length} planned, {queryOutcomes.Count} run");
        lines.Add($"  Succeeded: {succeeded}");
        lines.Add($"  SucceededWithErrors: {succeededWithErrors}");
        lines.Add($"  Failed / aborted / not run: {failed}");
        lines.Add($"Searches with warnings: {searchesWithWarnings} (warnings in total: {totalWarnings})" +
                  (warningsUnreadable > 0 ? $"; warnings unreadable for {warningsUnreadable} search(es)" : ""));
        lines.Add($"Updates returned, all searches (raw rows before app filtering): {rawRows}");
        lines.Add($"Unique updates (UpdateID:RevisionNumber): {uniqueUpdates.Count}");
        lines.Add($"Updates returned by search C (BrowseOnly=1, unique): {setC.Count}");
        lines.Add($"Preview/optional-looking updates (unique): {previewHitKeys.Count}");
        lines.Add($"Total elapsed: {totalSw.ElapsedMilliseconds} ms");
        lines.Add($"  Diagnostic searches: {searchMs} ms");
        lines.Add($"  Driver classification (ordinary application scan: own WUA searches + installed-driver inventory): {driverClassificationSw.ElapsedMilliseconds} ms");
        lines.Add($"  Feature update check: {featureUpdateSw.ElapsedMilliseconds} ms");
        lines.Add($"  Other (WUA session setup, policy/registry/PME reads, row capture): {otherMs} ms");
        lines.Add(succeeded == queries.Length
            ? "All diagnostic searches succeeded. The counts above are what WUA COM returned for these criteria; Windows Settings was not queried."
            : "Not every diagnostic search succeeded. A count of 0 is not proof that the PC is fully up to date.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Diagnostics only. Reads the OS build identity from the CurrentVersion registry key.
    /// A value that is missing is reported as missing, never substituted.
    /// </summary>
    private static List<string> WindowsBuildDiagnosticLines()
    {
        var lines = new List<string>();
        try
        {
            using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null)
            {
                lines.Add("OS Build: unavailable (CurrentVersion registry key not found)");
                return lines;
            }
            string Value(string name)
            {
                string text = Convert.ToString(key.GetValue(name), System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";
                return text.Length == 0 ? "(not set)" : text;
            }
            string build = Value("CurrentBuildNumber");
            if (build == "(not set)") build = Value("CurrentBuild");
            lines.Add($"OS Build: {build}.{Value("UBR")} (CurrentBuild.UBR)");
            lines.Add("DisplayVersion: " + Value("DisplayVersion"));
            lines.Add("EditionID: " + Value("EditionID"));
            lines.Add("InstallationType: " + Value("InstallationType"));
        }
        catch (Exception ex) { lines.Add("OS Build: unavailable (" + ex.Message + ")"); }
        return lines;
    }

    private sealed class DiagnosticUpdateRow
    {
        public string Title = "";
        public string Kb = "";
        public string UpdateId = "";
        public string Revision = "";
        public string Installed = "?";
        public string Hidden = "?";
        public string BrowseOnly = "?";
        public string AutoSelectOnWebSites = "?";
        public string Mandatory = "?";
        public string Assigned = "?";
        public string DeploymentAction = "?";
        public string RebootRequired = "?";
        public string Beta = "?";
        public string Severity = "";
        public string Categories = "";
        public string ServiceId = "?";
        public string IdentityKey => UpdateId + ":" + Revision;
    }

    private static DiagnosticUpdateRow CaptureDiagnosticRow(dynamic u)
    {
        var row = new DiagnosticUpdateRow
        {
            Title = Convert.ToString(u.Title) ?? "",
            Kb = KBs(u)
        };
        try { row.UpdateId = Convert.ToString(u.Identity.UpdateID) ?? ""; row.Revision = Convert.ToString(u.Identity.RevisionNumber) ?? ""; } catch { }
        try { row.BrowseOnly = Convert.ToString(u.BrowseOnly) ?? "?"; } catch { }
        try { row.Hidden = Convert.ToString(u.IsHidden) ?? "?"; } catch { }
        try { row.Mandatory = Convert.ToString(u.IsMandatory) ?? "?"; } catch { }
        try { row.Beta = Convert.ToString(u.IsBeta) ?? "?"; } catch { }
        try { row.Installed = Convert.ToString(u.IsInstalled) ?? "?"; } catch { }
        try { row.Severity = Convert.ToString(u.MsrcSeverity) ?? ""; } catch { }
        try { row.AutoSelectOnWebSites = Convert.ToString(u.AutoSelectOnWebSites) ?? "?"; } catch { }
        try { row.Assigned = Convert.ToString(u.IsAssigned) ?? "?"; } catch { }
        try { row.DeploymentAction = Convert.ToString(u.DeploymentAction) ?? "?"; } catch { }
        try { row.RebootRequired = Convert.ToString(u.RebootRequired) ?? "?"; } catch { }
        try { row.ServiceId = Convert.ToString(u.ServiceID) ?? "?"; } catch { row.ServiceId = "n/a on IUpdate"; }
        try
        {
            var names = new List<string>();
            foreach (var c in u.Categories) names.Add(Convert.ToString(c.Name) ?? "");
            row.Categories = string.Join("; ", names);
        }
        catch { }
        return row;
    }

    private static string FormatServerSelection(string raw)
    {
        if (string.Equals(raw, "0", StringComparison.Ordinal) ||
            string.Equals(raw, "ssDefault", StringComparison.OrdinalIgnoreCase))
            return "0 (Default – configured Windows Update source)";
        if (string.Equals(raw, "1", StringComparison.Ordinal)) return "1 (Managed server)";
        if (string.Equals(raw, "2", StringComparison.Ordinal)) return "2 (Windows Update)";
        if (string.Equals(raw, "3", StringComparison.Ordinal)) return "3 (Others)";
        return ValueOrUnknown(raw) + " (see IUpdateSearcher.ServerSelection)";
    }

    private static string FormatServiceId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            raw.Equals("00000000-0000-0000-0000-000000000000", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("{}", StringComparison.OrdinalIgnoreCase))
            return "default / not explicitly set";
        return raw;
    }

    // WUA OperationResultCode: 0 NotStarted, 1 InProgress, 2 Succeeded, 3 SucceededWithErrors, 4 Failed, 5 Aborted.
    private static string FormatOperationResultCode(int code) => code switch
    {
        0 => "NotStarted",
        1 => "InProgress",
        2 => "Succeeded",
        3 => "SucceededWithErrors",
        4 => "Failed",
        5 => "Aborted",
        -1 => "Unavailable",
        _ => "Other (" + code + ")"
    };

    private static int SearchWarningCount(dynamic results)
    {
        try { return (int)results.Warnings.Count; }
        catch { return -1; }
    }

    private static string FormatSearchWarnings(dynamic results)
    {
        try
        {
            dynamic warnings = results.Warnings;
            int count = (int)warnings.Count;
            if (count == 0) return "(none)";
            var parts = new List<string>();
            int limit = Math.Min(count, 20);
            for (int i = 0; i < limit; i++)
            {
                dynamic w = warnings.Item(i);
                string msg = "";
                string hr = "";
                try { msg = Convert.ToString(w.Message) ?? ""; } catch { }
                try { hr = $"0x{unchecked((uint)(int)w.HResult):X8}"; } catch { hr = "?"; }
                parts.Add($"[{i + 1}] HR={hr} {msg}".Trim());
            }
            if (count > limit) parts.Add($"… +{count - limit} more");
            return string.Join(" | ", parts);
        }
        catch (Exception ex) { return "Unavailable (" + ex.GetType().Name + ")"; }
    }

    private static string FormatHResult(Exception ex)
    {
        if (ex is COMException com) return $"0x{unchecked((uint)com.ErrorCode):X8}";
        return $"0x{unchecked((uint)ex.HResult):X8}";
    }

    private static HashSet<string> ToIdSet(Dictionary<string, List<DiagnosticUpdateRow>> snapshots, string name)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!snapshots.TryGetValue(name, out var rows)) return set;
        foreach (var row in rows)
            if (!string.IsNullOrWhiteSpace(row.UpdateId))
                set.Add(row.IdentityKey);
        return set;
    }

    private static int CountOnlyIn(HashSet<string> left, HashSet<string> right)
    {
        int n = 0;
        foreach (var id in left)
            if (!right.Contains(id)) n++;
        return n;
    }

    private static bool LooksLikePreviewOrOptionalOffer(DiagnosticUpdateRow row)
    {
        string title = row.Title ?? "";
        string kb = row.Kb ?? "";
        if (kb.Contains("5132057", StringComparison.OrdinalIgnoreCase)) return true;
        return title.Contains("Preview", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Forhåndsvisning", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Vorschau", StringComparison.OrdinalIgnoreCase);
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
            string historyUpdateId = "";
            try { historyUpdateId = Convert.ToString(item.UpdateIdentity.UpdateID) ?? ""; } catch { }
            var entry = new UpdateEntry
            {
                KB = string.Join(", ", kbs), Title = title, UpdateId = historyUpdateId,
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
        return new UpdateEntry
        {
            KB = selected.KB,
            Title = selected.Title,
            UpdateId = selected.UpdateId,
            Status = changed ? "Installert" : alreadyCurrent ? "Already up to date" : "Verification unavailable",
            Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Source = $"Update Express — Defender signatures ({before} -> {installed})"
        };
    }

    /// <summary>
    /// Map a requested update to its WUA/Defender install result.
    /// InstallSelectedWua must populate UpdateId; KB/title are fallbacks for KB-less
    /// packages (e.g. PowerShell LTS). Positional index is only used when both sides
    /// lack UpdateId — never when identities conflict or results were filtered/reordered.
    /// A no-UpdateId single-result fallback is only allowed when the caller requested
    /// exactly one update (requestedCount == 1); results.Count alone is not unambiguous
    /// because CollectSoftwareUpdates / download filtering can drop earlier selections.
    /// </summary>
    public static UpdateEntry? MatchInstallResult(
        IReadOnlyList<UpdateEntry> results,
        UpdateEntry requested,
        int index = -1,
        int requestedCount = -1)
    {
        if (results == null || results.Count == 0 || requested == null) return null;
        if (!string.IsNullOrWhiteSpace(requested.UpdateId))
        {
            var byId = results.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.UpdateId) &&
                x.UpdateId.Equals(requested.UpdateId, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;
        }
        var requestedKbs = SplitKbs(requested.KB);
        if (requestedKbs.Count > 0)
        {
            var byKb = results.FirstOrDefault(x =>
            {
                var resultKbs = SplitKbs(x.KB);
                return resultKbs.Count > 0 && requestedKbs.Any(kb =>
                    resultKbs.Any(r => r.Equals(kb, StringComparison.OrdinalIgnoreCase)));
            });
            if (byKb != null) return byKb;
        }
        if (!string.IsNullOrWhiteSpace(requested.Title))
        {
            string wantTitle = requested.Title.Trim();
            var byTitle = results.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.Title) &&
                x.Title.Trim().Equals(wantTitle, StringComparison.OrdinalIgnoreCase));
            if (byTitle != null) return byTitle;
        }
        // Only when the caller selected exactly one update and WUA returned one result.
        // results.Count == 1 alone is not safe: skipped downloads can leave a later
        // selection as the sole result while index 0 is a different requested update.
        if (requestedCount == 1 && results.Count == 1)
        {
            var only = results[0];
            if (string.IsNullOrWhiteSpace(only.UpdateId))
                return only;
        }
        if (index >= 0 && index < results.Count)
        {
            var positional = results[index];
            // Reject positional matches when either side has an UpdateId (lists can omit
            // skipped/failed downloads, so index alignment is not guaranteed).
            if (string.IsNullOrWhiteSpace(requested.UpdateId) && string.IsNullOrWhiteSpace(positional.UpdateId))
                return positional;
        }
        return null;
    }

    private static List<string> SplitKbs(string? kb) =>
        string.IsNullOrWhiteSpace(kb)
            ? new List<string>()
            : kb.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

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

    /// <summary>
    /// Drivers automation: download selected Type='Driver' updates without installing.
    /// Does not substitute another driver when an ID is missing.
    /// </summary>
    public static int DownloadSelectedDrivers(List<string> updateIds, Action<string>? report = null)
    {
        if (updateIds.Count == 0) throw new InvalidOperationException("No driver updates selected.");
        report?.Invoke("Searching for selected driver updates…");
        dynamic session = Session();
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic search = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Driver'");
        int total = (int)search.Updates.Count;
        report?.Invoke($"WUA driver search returned {total} update(s).");
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        var selectedIds = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);
        var foundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < total; i++)
        {
            dynamic update = search.Updates.Item(i);
            string id = Convert.ToString(update.Identity.UpdateID) ?? "";
            if (!selectedIds.Contains(id)) continue;
            foundIds.Add(id);
            try
            {
                if (!(bool)update.EulaAccepted) update.AcceptEula();
                collection.Add(update);
                report?.Invoke($"Matched selected driver: {id}");
            }
            catch (Exception ex)
            {
                report?.Invoke($"Skipped driver {id}: {ex.Message}");
            }
        }
        foreach (string missing in selectedIds.Except(foundIds, StringComparer.OrdinalIgnoreCase))
            report?.Invoke($"Selected driver not found in fresh search: {missing}");
        if ((int)collection.Count == 0)
            throw new InvalidOperationException("No selected drivers could be prepared for download.");
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
        report?.Invoke($"Driver download phase completed: {downloadedCount}/{collection.Count} (result={downloadCode}).");
        if (downloadCode != 2 && downloadCode != 3)
            throw new InvalidOperationException($"Driver download failed: result code {downloadCode}.");
        if (downloadedCount == 0)
            throw new InvalidOperationException("No drivers were downloaded.");
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
        // Missing from IsInstalled=0 can mean installed, hidden, or superseded by another offer.
        // Only claim Already up to date when WUA affirms IsInstalled=1 for this UpdateId (+ HWID).
        if (sameId.Count == 0)
        {
            bool installedAffirmed = false;
            try
            {
                dynamic installedSearch = searcher.Search("IsInstalled=1 and Type='Driver'");
                for (int i = 0; i < (int)installedSearch.Updates.Count; i++)
                {
                    dynamic candidate = installedSearch.Updates.Item(i);
                    string id = Convert.ToString(candidate.Identity.UpdateID) ?? "";
                    if (!id.Equals(selected.UpdateId, StringComparison.OrdinalIgnoreCase)) continue;
                    string hw = "";
                    try { hw = Convert.ToString(candidate.DriverHardwareID) ?? ""; } catch { }
                    if (string.IsNullOrWhiteSpace(selected.DriverHardwareId) ||
                        hw.Equals(selected.DriverHardwareId, StringComparison.OrdinalIgnoreCase))
                    {
                        installedAffirmed = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Driver installed-state recheck failed: " + ex.Message);
            }
            string stage = installedAffirmed ? "AlreadyInstalled" : "NoLongerPending";
            string status = installedAffirmed ? "Already up to date" : "Verification unavailable";
            report?.Invoke(installedAffirmed
                ? "Selected driver is already installed (WUA IsInstalled=1)."
                : "Selected driver is no longer pending and was not found as installed.");
            try
            {
                File.AppendAllText(Path.Combine(auditDir, "driver-recheck.log"),
                    $"[{DateTimeOffset.Now:O}] stage={stage}; update={selected.UpdateId}; hardware={selected.DriverHardwareId}; pendingCandidates={observed.Count}; status={status}\n",
                    Encoding.UTF8);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Driver already-installed logging failed: " + ex.Message); }
            return new List<UpdateEntry> { new UpdateEntry {
                Title = selected.Title, Category = selected.Category, UpdateId = selected.UpdateId,
                Status = status,
                Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Source = "Windows Update Agent"
            } };
        }
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
            string updateId = "";
            try { updateId = Convert.ToString(u.Identity.UpdateID) ?? ""; } catch { }
            entries.Add(new UpdateEntry
            {
                KB = KBs(u),
                Title = Convert.ToString(u.Title) ?? "",
                UpdateId = updateId,
                Status = code == 2 ? "Installert" : code == 3 ? "Delvis fullført" : "Feilet / avbrutt",
                Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Source = restartRequired ? $"Update Express v{ProductVersion.Display} — Restart required" : $"Update Express v{ProductVersion.Display}",
                ErrorCode = hr == 0 ? "" : $"0x{unchecked((uint)hr):X8}"
            });
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
