using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using UpdateExpress.Models;

namespace UpdateExpress.Services;

/// <summary>
/// Update until current: scan, download, and install Windows software updates,
/// restart when Windows requires it, then repeat until none of those updates remain.
/// Drivers, firmware, and feature updates stay outside the cycle.
/// <see cref="UnattendedUntilClear"/> reuses <see cref="IsInCycle"/> for overnight
/// clearing rescans (Applications are handled separately there).
/// </summary>
internal static class UpdateUntilCurrent
{
    internal const int DefaultMaxRounds = 8;
    internal const int RebootCountdownSeconds = 90;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static Mutex? _mutex;
    /// <summary>Non-reentrant same-process guard (Mutex alone is recursive for the owning thread).</summary>
    private static int _entered;

    private static string PathFile => Path.Combine(InstallationManager.InstallDir, "update-until-current.json");

    internal static bool IsRunning => Volatile.Read(ref _entered) != 0;

    internal static bool IsInCycle(UpdateEntry entry) =>
        UpdateCategoryRules.IsCriticalSoftware(entry)
        || UpdateCategoryRules.IsGeneralSoftware(entry)
        || (UpdateCategoryRules.IsDefenderSignature(entry) && !string.IsNullOrWhiteSpace(entry.UpdateId));

    internal static UpdateUntilCurrentState Load()
    {
        try
        {
            if (!File.Exists(PathFile)) return new UpdateUntilCurrentState();
            return JsonSerializer.Deserialize<UpdateUntilCurrentState>(File.ReadAllText(PathFile)) ?? new UpdateUntilCurrentState();
        }
        catch
        {
            return new UpdateUntilCurrentState();
        }
    }

    internal static void Save(UpdateUntilCurrentState state)
    {
        Directory.CreateDirectory(InstallationManager.InstallDir);
        File.WriteAllText(PathFile, JsonSerializer.Serialize(state, Json));
    }

    internal static void Start()
    {
        Save(new UpdateUntilCurrentState
        {
            Active = true,
            Round = 0,
            MaxRounds = DefaultMaxRounds,
            Phase = "starting",
            Message = "Starting.",
            StartedAt = DateTime.Now,
            ResumeAfterReboot = false,
            LastResumeLaunch = null
        });
        AgentLog.Write("Update until current started.");
        ActivityLog.TryWrite("UpdateUntilCurrent", "Started", new { MaxRounds = DefaultMaxRounds }, mode: "Manual", source: "MainWindow");
    }

    internal static void Stop(string message)
    {
        var state = Load();
        state.Active = false;
        state.ResumeAfterReboot = false;
        state.Phase = "stopped";
        state.Message = message;
        Save(state);
        AgentLog.Write("Update until current stopped: " + message);
    }

    /// <summary>
    /// Acquire the cycle runner. Combines a cross-process mutex with a non-reentrant
    /// in-process flag so a second start on the same UI thread cannot nest while the
    /// first await is still in flight.
    /// </summary>
    internal static bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _entered, 1, 0) != 0)
            return false;

        var mutex = new Mutex(false, CycleMutexName);
        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                Interlocked.Exchange(ref _entered, 0);
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died while holding the mutex; we own it now.
        }
        catch
        {
            mutex.Dispose();
            Interlocked.Exchange(ref _entered, 0);
            return false;
        }

        _mutex = mutex;
        return true;
    }

    internal static void Exit()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
        Interlocked.Exchange(ref _entered, 0);
    }

    /// <summary>Clear the tray resume throttle once the elevated runner actually starts.</summary>
    internal static void ClearResumeLaunchMarker()
    {
        var state = Load();
        if (state.LastResumeLaunch is null) return;
        state.LastResumeLaunch = null;
        Save(state);
    }

    /// <summary>
    /// After sign-in / wake, open the elevated app so a reboot (or interrupted cycle) can continue.
    /// Throttle is short so sleep/hibernate does not strand an active cycle for 15 minutes.
    /// </summary>
    internal static bool TryLaunchResume()
    {
        var state = Load();
        if (!state.Active) return false;
        // Prefer explicit reboot flag; also recover interrupted installing/scanning after unexpected power loss.
        // "scanning" is what RunUpdateUntilCurrentAsync persists; keep "searching" for older state files.
        bool shouldResume = state.ResumeAfterReboot
            || string.Equals(state.Phase, "reboot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "installing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "scanning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.Phase, "searching", StringComparison.OrdinalIgnoreCase);
        if (!shouldResume) return false;
        // Live installer still holds the cycle mutex — do not spawn another UAC / Main window.
        if (string.Equals(state.Phase, "installing", StringComparison.OrdinalIgnoreCase)
            && IsCycleMutexHeld())
            return false;
        if (state.LastResumeLaunch is DateTime last && DateTime.Now - last < TimeSpan.FromMinutes(5))
            return false;
        state.ResumeAfterReboot = true;
        state.LastResumeLaunch = DateTime.Now;
        Save(state);
        RmmStatusExport.WriteBestEffort("until-current-resume-launch", force: true);
        AgentLog.Write("Update until current: launching elevated app to continue after restart/interrupt.");
        ElevationHelper.RestartElevated("--installed --update-until-current");
        return true;
    }

    private const string CycleMutexName = @"Local\MicroSystems.UpdateExpress.UpdateUntilCurrent";

    /// <summary>True when another process (or this one) currently owns the cycle runner mutex.</summary>
    private static bool IsCycleMutexHeld()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, CycleMutexName);
            if (!mutex.WaitOne(0))
                return true;
            mutex.ReleaseMutex();
            return false;
        }
        catch (AbandonedMutexException)
        {
            try { mutex?.ReleaseMutex(); } catch { /* ignore */ }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { mutex?.Dispose(); } catch { /* ignore */ }
        }
    }
}

internal sealed class UpdateUntilCurrentState
{
    public bool Active { get; set; }
    public int Round { get; set; }
    public int MaxRounds { get; set; } = UpdateUntilCurrent.DefaultMaxRounds;
    public string Phase { get; set; } = "idle";
    public string Message { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public bool ResumeAfterReboot { get; set; }
    public DateTime? LastResumeLaunch { get; set; }
}
