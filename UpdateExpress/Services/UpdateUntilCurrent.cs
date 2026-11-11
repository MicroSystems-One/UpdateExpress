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

        var mutex = new Mutex(false, @"Local\MicroSystems.UpdateExpress.UpdateUntilCurrent");
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

    /// <summary>After sign-in, open the elevated app once so a reboot can continue the cycle.</summary>
    internal static bool TryLaunchResume()
    {
        var state = Load();
        if (!state.Active || !state.ResumeAfterReboot) return false;
        if (state.LastResumeLaunch is DateTime last && DateTime.Now - last < TimeSpan.FromMinutes(15))
            return false;
        state.LastResumeLaunch = DateTime.Now;
        Save(state);
        AgentLog.Write("Update until current: launching elevated app to continue after restart.");
        ElevationHelper.RestartElevated("--installed --update-until-current");
        return true;
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
