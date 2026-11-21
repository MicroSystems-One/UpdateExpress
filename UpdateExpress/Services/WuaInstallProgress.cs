using System.Runtime.InteropServices;

namespace UpdateExpress.Services;

public readonly record struct WuaActivity(string Phase, string Kb, int Number, int Total, int Percent);

public static class WuaProgressText
{
    public static bool Norwegian
    {
        get
        {
            try { return string.Equals(ReminderSchedule.Load().Language, "no", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }
    }

    public static string PleaseWait(bool norwegian) => norwegian
        ? "Laste ned og installere oppdateringer kan ta litt tid, vennligst vent."
        : "Downloading and installing updates can take a while. Please wait.";

    public static string Stage(bool norwegian, string phase, string kb, int number, int total, int percent)
    {
        string label = string.IsNullOrWhiteSpace(kb) ? "…" : kb;
        string pct = percent >= 0 ? $" — {percent}%" : "";
        return phase switch
        {
            "download" => norwegian
                ? $"Laster ned ({label}) {number} av {total} oppdateringer{pct}"
                : $"Downloading ({label}) {number} of {total} updates{pct}",
            "install" => norwegian
                ? $"Installerer ({label}) {number} av {total} oppdateringer{pct}"
                : $"Installing ({label}) {number} of {total} updates{pct}",
            "downloaded" => norwegian
                ? $"Lastet ned ({label}) {number} av {total} oppdateringer"
                : $"Downloaded ({label}) {number} of {total} updates",
            _ => label
        };
    }
}

public static class WuaJobs
{
    public static dynamic Download(dynamic session, dynamic collection, Action<string>? report, Action<WuaActivity>? activity)
    {
        int total = Count(collection);
        bool norwegian = WuaProgressText.Norwegian;
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = collection;
        var sink = new DownloadSink();
        dynamic job;
        try { job = downloader.BeginDownload(sink, sink, null); }
        catch (Exception ex)
        {
            report?.Invoke("Live download progress unavailable: " + ex.Message);
            Publish("download", collection, 0, total, -1, norwegian, report, activity);
            return downloader.Download();
        }
        int lastIndex = int.MinValue, lastPercent = int.MinValue;
        Wait(() => sink.Completed || Completed(job), () =>
        {
            ReadDownload(job, sink, out int index, out int percent);
            if (index == lastIndex && percent == lastPercent) return;
            lastIndex = index;
            lastPercent = percent;
            Publish("download", collection, index, total, percent, norwegian, report, activity);
        });
        return downloader.EndDownload(job);
    }

    public static dynamic Install(dynamic session, dynamic collection, Action<string>? report, Action<WuaActivity>? activity, bool allowPrompts)
    {
        int total = Count(collection);
        bool norwegian = WuaProgressText.Norwegian;
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = collection;
        installer.AllowSourcePrompts = allowPrompts;
        installer.ForceQuiet = !allowPrompts;
        var sink = new InstallSink();
        dynamic job;
        try { job = installer.BeginInstall(sink, sink, null); }
        catch (Exception ex)
        {
            report?.Invoke("Live install progress unavailable: " + ex.Message);
            Publish("install", collection, 0, total, -1, norwegian, report, activity);
            return installer.Install();
        }
        int lastIndex = int.MinValue, lastPercent = int.MinValue;
        Wait(() => sink.Completed || Completed(job), () =>
        {
            ReadInstall(job, sink, out int index, out int percent);
            if (index == lastIndex && percent == lastPercent) return;
            lastIndex = index;
            lastPercent = percent;
            Publish("install", collection, index, total, percent, norwegian, report, activity);
        });
        return installer.EndInstall(job);
    }

    public static string Label(dynamic collection, int index)
    {
        try
        {
            int total = Count(collection);
            if (index < 0 || index >= total) return "";
            dynamic update = collection.Item(index);
            var ids = new List<string>();
            try { foreach (var id in update.KBArticleIDs) ids.Add("KB" + Convert.ToString(id)); }
            catch (COMException) { }
            if (ids.Count > 0) return ids[0];
            string title = Convert.ToString(update.Title) ?? "";
            return title.Length > 48 ? title[..48] + "…" : title;
        }
        catch { return ""; }
    }

    private static void Publish(string phase, dynamic collection, int index, int total, int percent, bool norwegian, Action<string>? report, Action<WuaActivity>? activity)
    {
        int number = Math.Clamp(index + 1, 1, Math.Max(total, 1));
        string kb = Label(collection, index);
        var update = new WuaActivity(phase, kb, number, total, percent);
        try { activity?.Invoke(update); } catch { }
        try { report?.Invoke(WuaProgressText.Stage(norwegian, phase, kb, number, total, percent)); } catch { }
    }

    private static void Wait(Func<bool> done, Action tick)
    {
        try { tick(); } catch { }
        while (!done())
        {
            try { System.Windows.Forms.Application.DoEvents(); } catch { }
            Thread.Sleep(250);
            try { tick(); } catch { }
        }
        try { tick(); } catch { }
    }

    private static int Count(dynamic collection)
    {
        try { return (int)collection.Count; }
        catch { return 0; }
    }

    private static bool Completed(dynamic job)
    {
        try { return (bool)job.IsCompleted; }
        catch { return false; }
    }

    private static void ReadDownload(dynamic job, DownloadSink sink, out int index, out int percent)
    {
        index = sink.Index;
        percent = sink.Percent;
        try
        {
            dynamic progress = job.GetProgress();
            index = Convert.ToInt32(progress.CurrentUpdateIndex);
            percent = PercentOf(progress, current: true);
        }
        catch { }
    }

    private static void ReadInstall(dynamic job, InstallSink sink, out int index, out int percent)
    {
        index = sink.Index;
        percent = sink.Percent;
        try
        {
            dynamic progress = job.GetProgress();
            index = Convert.ToInt32(progress.CurrentUpdateIndex);
            percent = PercentOf(progress, current: true);
        }
        catch { }
    }

    private static int PercentOf(dynamic progress, bool current)
    {
        try
        {
            int currentPercent = Convert.ToInt32(progress.CurrentUpdatePercentComplete);
            if (currentPercent >= 0) return Math.Clamp(currentPercent, 0, 100);
        }
        catch { }
        try
        {
            double done = Convert.ToDouble(current ? progress.CurrentUpdateBytesDownloaded : progress.TotalBytesDownloaded);
            double all = Convert.ToDouble(current ? progress.CurrentUpdateBytesToDownload : progress.TotalBytesToDownload);
            if (all > 0) return (int)Math.Clamp(done / all * 100, 0, 100);
        }
        catch { }
        try { return Math.Clamp(Convert.ToInt32(progress.PercentComplete), 0, 100); }
        catch { return -1; }
    }

    [ComImport, Guid("C574DE85-7358-43F6-AAE8-8697E62D8BA7"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWuaDownloadJob { }

    [ComImport, Guid("FA565B23-498C-47A0-979D-E7D5B1813360"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWuaDownloadCompletedArgs { }

    [ComImport, Guid("324FF2C6-4981-4B04-9412-57481745AB24"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWuaDownloadProgressArgs { }

    [ComImport, Guid("77254866-9F5B-4C8E-B9E2-C77A8530D64B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWuaDownloadCompleted
    {
        void Invoke(IWuaDownloadJob job, IWuaDownloadCompletedArgs args);
    }

    [ComImport, Guid("8C3F1CDD-6173-4591-AEBD-A56A53CA77C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWuaDownloadProgress
    {
        void Invoke(IWuaDownloadJob job, IWuaDownloadProgressArgs args);
    }

    [ComImport, Guid("5C209F0B-BAD5-432A-9556-4699BED2638A"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWuaInstallJob { }

    [ComImport, Guid("250E2106-8EFB-4705-9653-EF13C581B6A1"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWuaInstallCompletedArgs { }

    [ComImport, Guid("E4F14E1E-689D-4218-A0B9-BC189C484A01"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IWuaInstallProgressArgs { }

    [ComImport, Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWuaInstallCompleted
    {
        void Invoke(IWuaInstallJob job, IWuaInstallCompletedArgs args);
    }

    [ComImport, Guid("E01402D5-F8DA-43BA-A012-38894BD048F1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWuaInstallProgress
    {
        void Invoke(IWuaInstallJob job, IWuaInstallProgressArgs args);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class DownloadSink : IWuaDownloadProgress, IWuaDownloadCompleted
    {
        public volatile bool Completed;
        public int Index;
        public int Percent = -1;
        void IWuaDownloadProgress.Invoke(IWuaDownloadJob job, IWuaDownloadProgressArgs args) => Read(((dynamic)args).Progress);
        void IWuaDownloadCompleted.Invoke(IWuaDownloadJob job, IWuaDownloadCompletedArgs args) => Completed = true;
        private void Read(dynamic progress)
        {
            try
            {
                Index = Convert.ToInt32(progress.CurrentUpdateIndex);
                Percent = PercentOf(progress, current: true);
            }
            catch { }
        }
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class InstallSink : IWuaInstallProgress, IWuaInstallCompleted
    {
        public volatile bool Completed;
        public int Index;
        public int Percent = -1;
        void IWuaInstallProgress.Invoke(IWuaInstallJob job, IWuaInstallProgressArgs args) => Read(((dynamic)args).Progress);
        void IWuaInstallCompleted.Invoke(IWuaInstallJob job, IWuaInstallCompletedArgs args) => Completed = true;
        private void Read(dynamic progress)
        {
            try
            {
                Index = Convert.ToInt32(progress.CurrentUpdateIndex);
                Percent = PercentOf(progress, current: true);
            }
            catch { }
        }
    }
}
