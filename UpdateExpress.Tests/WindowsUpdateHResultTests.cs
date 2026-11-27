using UpdateExpress.Services;
using Xunit;

namespace UpdateExpress.Tests;

public sealed class WindowsUpdateHResultTests
{
    [Fact]
    public void BestFromException_prefers_embedded_WUA_code_over_managed_InvalidOperation()
    {
        var inner = new InvalidOperationException(
            "WUA driver download failed: result=4, HRESULT=0x80240034, IsDownloaded=False");
        var outer = new InvalidOperationException("Driver Download failed: " + inner.Message, inner);

        Assert.Equal("0x80240034", WindowsUpdateHResult.BestFromException(outer));
    }

    [Fact]
    public void BestFromText_prefers_WUA_code_when_state_has_wrapper_and_tip()
    {
        string blob =
            "Failed 0x80131509 0x80240034 — WU_E_DOWNLOAD_FAILED. The update failed to download";
        Assert.Equal("0x80240034", WindowsUpdateHResult.BestFromText(blob));
    }

    [Fact]
    public void FormatForFailedState_uses_WUA_code_in_state()
    {
        var ex = new InvalidOperationException(
            "Driver Download failed: System.InvalidOperationException: WUA driver download failed: result=4, HRESULT=0x80240034, IsDownloaded=False (HRESULT=0x80131509)");
        Assert.Equal("Failed 0x80240034", WindowsUpdateHResult.FormatForFailedState(ex));
    }

    [Fact]
    public void BestFromException_returns_managed_code_when_only_guard_failure()
    {
        var ex = new InvalidOperationException("Driver identity recheck failed: 0 UpdateID match(es).");
        Assert.Equal(WindowsUpdateHResult.ManagedInvalidOperation, WindowsUpdateHResult.BestFromException(ex));
    }
}