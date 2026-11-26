using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UpdateExpress.Services;

/// <summary>
/// Processor, memory, local storage and temperatures for the "This PC" window.
/// Everything comes from Windows itself: no driver is installed and no elevation is asked for.
/// A value Windows does not report stays null and is shown as unavailable, never estimated.
/// Reading can take a while (performance counters), so call <see cref="Read"/> off the UI thread.
/// </summary>
internal static class PcHardwareReader
{
    internal sealed record DriveUse(string Name, long TotalBytes, long UsedBytes);

    internal sealed record ThermalZone(string Name, double Celsius);

    internal sealed record GpuAdapter(string Name, double? Celsius);

    internal sealed record Snapshot
    {
        public string ProcessorName { get; init; } = "";
        public int? ProcessorMhz { get; init; }
        /// <summary>Memory fitted in the PC according to its firmware tables.</summary>
        public long? MemoryInstalledBytes { get; init; }
        /// <summary>Memory Windows can use; a little less than installed.</summary>
        public long? MemoryVisibleBytes { get; init; }
        public long? MemoryUsedBytes { get; init; }
        public IReadOnlyList<DriveUse> Drives { get; init; } = Array.Empty<DriveUse>();
        public IReadOnlyList<ThermalZone> ThermalZones { get; init; } = Array.Empty<ThermalZone>();
        public IReadOnlyList<GpuAdapter> GpuAdapters { get; init; } = Array.Empty<GpuAdapter>();
    }

    /// <summary>One row of the window: the text, its tooltip, and whether it is a real reading.</summary>
    internal sealed record Row(string Value, string Tooltip, bool Available);

    internal sealed record Display(Row Processor, Row Memory, Row Storage, Row CpuTemperature, Row GpuTemperature);

    internal static Snapshot Read()
    {
        var memory = Try(ReadMemoryStatus);
        return new Snapshot
        {
            ProcessorName = Try(ReadProcessorName) ?? "",
            ProcessorMhz = Try(ReadProcessorMhz),
            MemoryInstalledBytes = Try(ReadInstalledMemory),
            MemoryVisibleBytes = memory?.Total,
            MemoryUsedBytes = memory?.Used,
            Drives = Try(ReadDrives) ?? Array.Empty<DriveUse>(),
            ThermalZones = Try(ReadThermalZones) ?? Array.Empty<ThermalZone>(),
            GpuAdapters = Try(ReadGpuAdapters) ?? Array.Empty<GpuAdapter>()
        };
    }

    internal static string Unavailable(bool norwegian) => norwegian ? "utilgjengelig" : "unavailable";

    internal static Display Describe(Snapshot s, bool norwegian)
    {
        var culture = norwegian ? CultureInfo.GetCultureInfo("nb-NO") : CultureInfo.InvariantCulture;
        string none = Unavailable(norwegian);

        string processor = FormatProcessor(s.ProcessorName, s.ProcessorMhz, culture);
        var processorRow = processor.Length == 0
            ? new Row(none, norwegian ? "Windows oppgir ikke prosessornavnet." : "Windows does not report the processor name.", false)
            : new Row(processor, processor, true);

        return new Display(processorRow, DescribeMemory(s, norwegian, culture), DescribeStorage(s, norwegian, culture),
            DescribeCpuTemperature(s, norwegian, culture), DescribeGpuTemperature(s, norwegian, culture));
    }

    private static Row DescribeMemory(Snapshot s, bool norwegian, CultureInfo culture)
    {
        long? total = s.MemoryInstalledBytes ?? s.MemoryVisibleBytes;
        if (s.MemoryUsedBytes is not long used || total is not long of || of <= 0 || used < 0)
            return new Row(Unavailable(norwegian), norwegian ? "Windows oppgir ikke minnebruken." : "Windows does not report memory use.", false);

        string value = FormatUsedOf(used, of, norwegian, culture);
        string tip;
        if (s.MemoryInstalledBytes is long installed)
        {
            tip = norwegian
                ? $"I bruk: {FormatSize(used, culture)}. Installert: {FormatSize(installed, culture)}."
                : $"In use: {FormatSize(used, culture)}. Installed: {FormatSize(installed, culture)}.";
            if (s.MemoryVisibleBytes is long visible)
                tip += norwegian
                    ? $" Tilgjengelig for Windows: {FormatSize(visible, culture)}."
                    : $" Usable by Windows: {FormatSize(visible, culture)}.";
        }
        else
        {
            tip = norwegian
                ? "PC-en oppgir ikke installert minne. Totalen er minnet Windows kan bruke."
                : "The PC does not report installed memory. The total is the memory Windows can use.";
        }
        return new Row(value, tip, true);
    }

    private static Row DescribeStorage(Snapshot s, bool norwegian, CultureInfo culture)
    {
        if (s.Drives.Count == 0)
            return new Row(Unavailable(norwegian), norwegian ? "Fant ingen lokale disker." : "No local drives were found.", false);

        long total = s.Drives.Sum(d => d.TotalBytes);
        long used = s.Drives.Sum(d => d.UsedBytes);
        string each = string.Join("\n", s.Drives.Select(d => $"{d.Name} {FormatUsedOf(d.UsedBytes, d.TotalBytes, norwegian, culture)}"));
        string tip = (norwegian ? "Faste lokale disker:\n" : "Fixed local drives:\n") + each;
        return new Row(FormatUsedOf(used, total, norwegian, culture), tip, true);
    }

    private static Row DescribeCpuTemperature(Snapshot s, bool norwegian, CultureInfo culture)
    {
        var plausible = s.ThermalZones.Where(z => IsPlausibleTemperature(z.Celsius)).ToList();
        var zone = PickCpuZone(plausible);
        if (zone is null)
        {
            return new Row(Unavailable(norwegian), norwegian
                ? "Windows oppgir ingen termisk sone (ACPI) på denne PC-en. Å lese prosessorens egen sensor krever en kjernedriver, og Update Express installerer ingen."
                : "Windows reports no ACPI thermal zone on this PC. Reading the processor's own sensor needs a kernel driver, and Update Express installs none.", false);
        }

        string value = FormatCelsius(zone.Celsius, culture) + (norwegian ? " (termisk sone)" : " (thermal zone)");
        string tip = norwegian
            ? $"Kilde: ACPI termisk sone {zone.Name}, slik Windows oppgir den. Dette er ikke en måling per kjerne."
            : $"Source: ACPI thermal zone {zone.Name}, as Windows reports it. This is not a per-core reading.";
        if (plausible.Count > 1)
        {
            tip += (norwegian ? "\nAlle soner:\n" : "\nAll zones:\n") +
                   string.Join("\n", plausible.Select(z => $"{z.Name}: {FormatCelsius(z.Celsius, culture)}"));
        }
        return new Row(value, tip, true);
    }

    private static Row DescribeGpuTemperature(Snapshot s, bool norwegian, CultureInfo culture)
    {
        var reporting = s.GpuAdapters
            .Where(a => a.Celsius is double c && IsPlausibleTemperature(c))
            .OrderByDescending(a => a.Celsius)
            .ToList();
        if (reporting.Count == 0)
        {
            string names = string.Join(", ", s.GpuAdapters.Select(a => a.Name).Where(n => n.Length > 0).Distinct());
            string tip = norwegian
                ? "Skjermdriveren oppgir ingen temperatur til Windows. Det er vanlig for integrert grafikk og enkle skjermdrivere."
                : "The graphics driver reports no temperature to Windows. That is common for integrated graphics and basic display drivers.";
            if (names.Length > 0) tip += "\n" + names;
            return new Row(Unavailable(norwegian), tip, false);
        }

        string source = norwegian
            ? "Kilde: skjermdriveren, slik Windows oppgir den."
            : "Source: the graphics driver, as Windows reports it.";
        string list = string.Join("\n", reporting.Select(a => $"{(a.Name.Length > 0 ? a.Name : "GPU")}: {FormatCelsius(a.Celsius!.Value, culture)}"));
        return new Row(FormatCelsius(reporting[0].Celsius!.Value, culture), source + "\n" + list, true);
    }

    /// <summary>A zone named for the CPU when there is one, otherwise the hottest zone.</summary>
    internal static ThermalZone? PickCpuZone(IReadOnlyList<ThermalZone> zones)
    {
        if (zones.Count == 0) return null;
        var named = zones.Where(z => z.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)).ToList();
        return (named.Count > 0 ? named : zones).OrderByDescending(z => z.Celsius).First();
    }

    /// <summary>Above freezing and below 130 °C; anything else is a sensor that is not connected.</summary>
    internal static bool IsPlausibleTemperature(double celsius) => celsius > 0 && celsius < 130;

    internal static double TenthsOfKelvinToCelsius(long tenthsOfKelvin) => tenthsOfKelvin / 10.0 - 273.15;

    internal static string FormatCelsius(double celsius, CultureInfo culture) =>
        Math.Round(celsius, MidpointRounding.AwayFromZero).ToString("0", culture) + " °C";

    /// <summary>The processor name with its speed, unless the name already carries one.</summary>
    internal static string FormatProcessor(string? name, int? mhz, CultureInfo culture)
    {
        string text = Regex.Replace(name ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) return "";
        if (mhz is not int speed || speed <= 0) return text;
        if (Regex.IsMatch(text, @"\d\s*[GM]Hz", RegexOptions.IgnoreCase)) return text;
        return text + " @ " + (speed / 1000.0).ToString("0.00", culture) + " GHz";
    }

    /// <summary>Binary units, as File Explorer shows them.</summary>
    internal static string FormatSize(long bytes, CultureInfo culture)
    {
        const double Gb = 1L << 30;
        if (bytes < 0) bytes = 0;
        double gb = bytes / Gb;
        if (gb >= 1024) return (gb / 1024).ToString("0.##", culture) + " TB";
        if (gb >= 100) return Math.Round(gb).ToString("0", culture) + " GB";
        if (gb >= 1) return gb.ToString("0.#", culture) + " GB";
        return Math.Round(bytes / (double)(1L << 20)).ToString("0", culture) + " MB";
    }

    internal static string FormatUsedOf(long used, long total, bool norwegian, CultureInfo culture) =>
        FormatSize(used, culture) + (norwegian ? " brukt av " : " used of ") + FormatSize(total, culture);

    private static T? Try<T>(Func<T?> read)
    {
        try { return read(); }
        catch { return default; }
    }

    private static string? ReadProcessorName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("ProcessorNameString") as string;
    }

    private static int? ReadProcessorMhz()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return key?.GetValue("~MHz") as int?;
    }

    private static long? ReadInstalledMemory() =>
        GetPhysicallyInstalledSystemMemory(out long kilobytes) && kilobytes > 0 ? kilobytes * 1024 : null;

    private static (long Total, long Used)? ReadMemoryStatus()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return null;
        return ((long)status.ullTotalPhys, (long)(status.ullTotalPhys - status.ullAvailPhys));
    }

    private static IReadOnlyList<DriveUse> ReadDrives()
    {
        var drives = new List<DriveUse>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0) continue;
                drives.Add(new DriveUse(drive.Name.TrimEnd('\\'), drive.TotalSize, drive.TotalSize - drive.TotalFreeSpace));
            }
            catch { }
        }
        return drives;
    }

    private const string ThermalCategory = "Thermal Zone Information";

    /// <summary>
    /// The ACPI thermal zones Windows publishes as performance counters. Readable without
    /// administrator rights. Many desktops and virtual machines have none.
    /// </summary>
    private static IReadOnlyList<ThermalZone> ReadThermalZones()
    {
        var zones = new List<ThermalZone>();
        if (!PerformanceCounterCategory.Exists(ThermalCategory)) return zones;
        var category = new PerformanceCounterCategory(ThermalCategory);
        foreach (string instance in category.GetInstanceNames())
        {
            double? celsius = ReadCounter("High Precision Temperature", instance) is long tenths && tenths > 0
                ? TenthsOfKelvinToCelsius(tenths)
                : ReadCounter("Temperature", instance) is long kelvin && kelvin > 0
                    ? kelvin - 273.15
                    : null;
            if (celsius is double value) zones.Add(new ThermalZone(instance, value));
        }
        return zones;
    }

    private static long? ReadCounter(string counter, string instance)
    {
        try
        {
            using var reader = new PerformanceCounter(ThermalCategory, counter, instance, readOnly: true);
            return reader.RawValue;
        }
        catch { return null; }
    }

    private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    private const int KMTQAITYPE_ADAPTERTYPE = 15;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    private const uint AdapterTypeSoftwareDevice = 0x4;
    private const int MaxAdapters = 16;

    /// <summary>
    /// What Task Manager shows as GPU temperature: the adapter performance data the graphics
    /// driver hands to Windows (WDDM 2.4 and later). Zero means the driver reports none.
    /// </summary>
    private static IReadOnlyList<GpuAdapter> ReadGpuAdapters()
    {
        var adapters = new List<GpuAdapter>();
        int infoSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        IntPtr buffer = Marshal.AllocHGlobal(infoSize * MaxAdapters);
        try
        {
            var request = new D3DKMT_ENUMADAPTERS2 { NumAdapters = MaxAdapters, pAdapters = buffer };
            if (D3DKMTEnumAdapters2(ref request) != 0) return adapters;
            var seen = new HashSet<long>();
            for (int i = 0; i < request.NumAdapters && i < MaxAdapters; i++)
            {
                var info = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(buffer + i * infoSize);
                try
                {
                    long luid = ((long)info.AdapterLuidHigh << 32) | info.AdapterLuidLow;
                    if (!seen.Add(luid)) continue;
                    uint? type = Query(info.hAdapter, KMTQAITYPE_ADAPTERTYPE, sizeof(uint), p => (uint?)Marshal.ReadInt32(p));
                    if (type is uint flags && (flags & AdapterTypeSoftwareDevice) != 0) continue;

                    string name = Query(info.hAdapter, KMTQAITYPE_ADAPTERREGISTRYINFO, 4 * 260 * sizeof(char),
                        p => Marshal.PtrToStringUni(p, 260)) ?? "";
                    int end = name.IndexOf('\0');
                    if (end >= 0) name = name[..end];

                    uint? deciCelsius = Query(info.hAdapter, KMTQAITYPE_ADAPTERPERFDATA, Marshal.SizeOf<D3DKMT_ADAPTER_PERFDATA>(),
                        p => (uint?)Marshal.PtrToStructure<D3DKMT_ADAPTER_PERFDATA>(p).Temperature);
                    adapters.Add(new GpuAdapter(name.Trim(), deciCelsius is uint value && value > 0 ? value / 10.0 : null));
                }
                finally
                {
                    var close = new D3DKMT_CLOSEADAPTER { hAdapter = info.hAdapter };
                    D3DKMTCloseAdapter(ref close);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return adapters;
    }

    private static T? Query<T>(uint adapter, int type, int size, Func<IntPtr, T?> read)
    {
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(data, i, 0);
            var query = new D3DKMT_QUERYADAPTERINFO
            {
                hAdapter = adapter,
                Type = type,
                pPrivateDriverData = data,
                PrivateDriverDataSize = (uint)size
            };
            return D3DKMTQueryAdapterInfo(ref query) == 0 ? read(data) : default;
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTERINFO
    {
        public uint hAdapter;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint NumOfSources;
        public int bPresentMoveRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2
    {
        public uint NumAdapters;
        public IntPtr pAdapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        /// <summary>Tenths of a degree Celsius.</summary>
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 request);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO request);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER request);
}
