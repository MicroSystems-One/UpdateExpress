using System.Runtime.CompilerServices;
using UpdateExpress.Services;

namespace UpdateExpress.Tests;

internal static class TestAssemblySetup
{
    /// <summary>
    /// Reading automation state moves what an older version left in ProgramData. On a PC that
    /// runs an installed Update Express that would take its live state away, so no test may
    /// trigger it; the migration itself is tested on temp folders.
    /// </summary>
    [ModuleInitializer]
    internal static void KeepInstalledStateAlone()
    {
        AutomationStatePaths.SharedStateMaintenanceDisabled = true;
        // The same for the rights on the PC's own policy folder, should a test run elevated.
        PolicyFolderGuard.Disabled = true;
    }
}
