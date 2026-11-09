using System.Threading.Tasks;
using System.Windows.Threading;

namespace UpdateExpress.Services;

/// <summary>Category 2: Critical/Important download + prompt (delegates to shared engine).</summary>
internal static class CriticalAutomation
{
    internal static void SetBalloon(System.Action<string, string> balloon) => DownloadPromptAutomation.SetBalloon(balloon);

    internal static Task RunDueJobAsync(Dispatcher dispatcher) =>
        DownloadPromptAutomation.RunCriticalDueJobAsync(dispatcher);

    internal static void ApplyQueuedInstall() =>
        DownloadPromptAutomation.ApplyQueuedInstall("critical", "Critical");
}
