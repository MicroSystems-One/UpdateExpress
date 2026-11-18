using System;

namespace UpdateExpress.Services;

/// <summary>
/// Detects N-able PME / dual-patcher conflicts with Update Express automation.
/// </summary>
internal static class PatchConflictGuard
{
    internal sealed record Result(string Level, string Summary, bool ShowBanner);

    internal static Result Evaluate(UpdatePolicy? policy = null, PmeServiceControl.ServiceState? pme = null)
    {
        policy ??= UpdatePolicy.Load();
        policy.Normalize();
        pme ??= PmeServiceControl.ReadState("PME.Agent.PmeService");

        if (!pme.Exists)
            return new Result("none", "", false);

        bool pmeActive = string.Equals(pme.State, "Running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(pme.StartMode, "Automatic", StringComparison.OrdinalIgnoreCase);

        if (!pmeActive)
            return new Result("none", $"PME present ({pme.State}/{pme.StartMode}).", false);

        if (!policy.WindowsAutomation.Enabled)
            return new Result(
                "info",
                $"N-able PME is {pme.State} ({pme.StartMode}). Windows automation in Update Express is off.",
                false);

        return new Result(
            "warn",
            $"N-able PME is {pme.State} (startup {pme.StartMode}) while Update Express automation is on. " +
            "Disable PME / patching for this device in N-able, or turn off Windows automation here, to avoid dual control.",
            true);
    }
}
