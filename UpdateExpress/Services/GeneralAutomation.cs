using System.Threading.Tasks;
using System.Windows.Threading;

namespace UpdateExpress.Services;

/// <summary>Category 3: general Windows software download + prompt (weekly by default).</summary>
internal static class GeneralAutomation
{
    internal static Task RunDueJobAsync(Dispatcher dispatcher) =>
        DownloadPromptAutomation.RunGeneralDueJobAsync(dispatcher);

    internal static void ApplyQueuedInstall() =>
        DownloadPromptAutomation.ApplyQueuedInstall("general", "General");
}
