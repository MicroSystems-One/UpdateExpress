using System;
using System.Threading;
using System.Threading.Tasks;

namespace UpdateExpress.Services;

/// <summary>
/// Category 1 (B+C): scheduled Defender security intelligence updates.
/// Never reboots Windows. Failures are logged; the installed app is left unchanged on error.
/// </summary>
internal static class DefenderAutomation
{
    private static int _running;

    internal static async Task RunDueJobAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            var policy = UpdatePolicy.Load();
            policy.Normalize();
            var defender = policy.WindowsAutomation.Defender;
            if (!policy.WindowsAutomation.Enabled || !defender.Enabled ||
                string.Equals(defender.Mode, "off", StringComparison.OrdinalIgnoreCase))
            {
                WindowsAutomationStatus.WriteCategory("defender", "Disabled", "Defender automation is turned off in policy.", null);
                return;
            }

            var status = WindowsAutomationStatus.Load();
            DateTime nextDue = status.Defender.NextDue ?? DateTime.Now;
            if (status.Defender.LastRun is DateTime last && status.Defender.NextDue is null)
                nextDue = last.AddMinutes(Math.Max(15, defender.IntervalMinutes));
            if (DateTime.Now < nextDue && status.Defender.LastRun is not null)
                return;

            AgentLog.Write("Defender automation: starting scheduled signature update.");
            ActivityLog.TryWrite("DefenderAutomation", "Started", new { Mode = defender.Mode }, mode: "Automatic", source: "TrayAgent");

            if (string.Equals(defender.Mode, "checkOnly", StringComparison.OrdinalIgnoreCase))
            {
                WindowsAutomationStatus.WriteCategory(
                    "defender",
                    "Check only",
                    "Policy is check-only; automatic install is disabled.",
                    DateTime.Now.AddMinutes(Math.Max(15, defender.IntervalMinutes)));
                return;
            }

            // autoInstall (v1 only allowed mode besides checkOnly/off)
            try
            {
                var result = await Task.Run(() => WindowsUpdateReader.EnsureDefenderSignatures(msg => AgentLog.Write("Defender automation: " + msg)))
                    .ConfigureAwait(false);
                string summary = $"{result.Status}; {result.Source}";
                WindowsAutomationStatus.WriteCategory(
                    "defender",
                    result.Status,
                    summary,
                    DateTime.Now.AddMinutes(Math.Max(15, defender.IntervalMinutes)));
                ActivityLog.TryWrite("DefenderAutomation", result.Status, new { result.Source, result.ErrorCode }, mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Defender automation completed: " + summary);
            }
            catch (Exception ex)
            {
                WindowsAutomationStatus.WriteCategory(
                    "defender",
                    "Failed",
                    ex.Message,
                    DateTime.Now.AddMinutes(Math.Max(15, defender.IntervalMinutes)));
                ActivityLog.TryWrite("DefenderAutomation", "Failed", ex.ToString(), mode: "Automatic", source: "TrayAgent");
                AgentLog.Write("Defender automation failed: " + ex);
            }
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
