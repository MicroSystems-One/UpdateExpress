using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UpdateExpress.Services;
using Xunit;
using Snapshot = UpdateExpress.Services.PcHardwareReader.Snapshot;

namespace UpdateExpress.Tests;

public class PcHardwareReaderTests
{
    private const long Gb = 1L << 30;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo Norwegian = CultureInfo.GetCultureInfo("nb-NO");

    private static Snapshot FullSnapshot() => new()
    {
        ProcessorName = "Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz",
        ProcessorMhz = 1896,
        MemoryInstalledBytes = 16 * Gb,
        MemoryVisibleBytes = 16 * Gb - 300 * (1L << 20),
        MemoryUsedBytes = 9 * Gb + Gb / 2,
        Drives = new[]
        {
            new PcHardwareReader.DriveUse("C:", 476 * Gb, 201 * Gb),
            new PcHardwareReader.DriveUse("D:", 931 * Gb, 100 * Gb)
        },
        ThermalZones = new[] { new PcHardwareReader.ThermalZone(@"\_TZ.THM0", 47.4) },
        GpuAdapters = new[] { new PcHardwareReader.GpuAdapter("NVIDIA GeForce RTX 3060", 52.0) }
    };

    [Theory]
    [InlineData("Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz", 1896, "Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz")]
    [InlineData("13th Gen Intel(R) Core(TM) i7-1355U", 2611, "13th Gen Intel(R) Core(TM) i7-1355U @ 2.61 GHz")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor   ", 3800, "AMD Ryzen 7 5800X 8-Core Processor @ 3.80 GHz")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", null, "AMD Ryzen 7 5800X 8-Core Processor")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", 0, "AMD Ryzen 7 5800X 8-Core Processor")]
    [InlineData("", 3800, "")]
    [InlineData(null, 3800, "")]
    public void FormatProcessor_AddsTheSpeedOnlyWhenTheNameHasNone(string? name, int? mhz, string expected)
    {
        Assert.Equal(expected, PcHardwareReader.FormatProcessor(name, mhz, Invariant));
    }

    [Fact]
    public void FormatProcessor_Norwegian_UsesDecimalComma()
    {
        Assert.Equal("13th Gen Intel(R) Core(TM) i7-1355U @ 2,61 GHz",
            PcHardwareReader.FormatProcessor("13th Gen Intel(R) Core(TM) i7-1355U", 2611, Norwegian));
    }

    [Theory]
    [InlineData(0L, "0 MB")]
    [InlineData(512L * 1024 * 1024, "512 MB")]
    [InlineData(Gb, "1 GB")]
    [InlineData(9 * Gb + Gb / 2, "9.5 GB")]
    [InlineData(16 * Gb, "16 GB")]
    [InlineData(476 * Gb, "476 GB")]
    [InlineData(1024 * Gb, "1 TB")]
    [InlineData(1407 * Gb, "1.37 TB")]
    [InlineData(-5L, "0 MB")]
    public void FormatSize_UsesBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(expected, PcHardwareReader.FormatSize(bytes, Invariant));
    }

    [Fact]
    public void Describe_English_ShowsUsedOfTotal()
    {
        var display = PcHardwareReader.Describe(FullSnapshot(), norwegian: false);

        Assert.Equal("Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz", display.Processor.Value);
        Assert.Equal("9.5 GB used of 16 GB", display.Memory.Value);
        // Both fixed drives added together; each one is in the tooltip.
        Assert.Equal("301 GB used of 1.37 TB", display.Storage.Value);
        Assert.Contains("C: 201 GB used of 476 GB", display.Storage.Tooltip);
        Assert.Contains("D: 100 GB used of 931 GB", display.Storage.Tooltip);
        Assert.Equal("47 °C (thermal zone)", display.CpuTemperature.Value);
        Assert.Contains(@"\_TZ.THM0", display.CpuTemperature.Tooltip);
        Assert.Equal("52 °C", display.GpuTemperature.Value);
        Assert.Contains("NVIDIA GeForce RTX 3060", display.GpuTemperature.Tooltip);
        Assert.All(new[] { display.Processor, display.Memory, display.Storage, display.CpuTemperature, display.GpuTemperature },
            row => Assert.True(row.Available));
    }

    [Fact]
    public void Describe_Norwegian_UsesNorwegianWordsAndDecimalComma()
    {
        var display = PcHardwareReader.Describe(FullSnapshot(), norwegian: true);

        Assert.Equal("9,5 GB brukt av 16 GB", display.Memory.Value);
        Assert.Equal("301 GB brukt av 1,37 TB", display.Storage.Value);
        Assert.Equal("47 °C (termisk sone)", display.CpuTemperature.Value);
        Assert.Equal("52 °C", display.GpuTemperature.Value);
    }

    [Fact]
    public void Describe_NothingRead_SaysUnavailable_AndShowsNoNumber()
    {
        foreach (bool norwegian in new[] { false, true })
        {
            var display = PcHardwareReader.Describe(new Snapshot(), norwegian);
            string expected = norwegian ? "utilgjengelig" : "unavailable";

            foreach (var row in new[] { display.Processor, display.Memory, display.Storage, display.CpuTemperature, display.GpuTemperature })
            {
                Assert.Equal(expected, row.Value);
                Assert.False(row.Available);
                Assert.False(string.IsNullOrWhiteSpace(row.Tooltip));
            }
        }
    }

    [Fact]
    public void Memory_WithoutInstalledSize_UsesWhatWindowsCanUse_AndSaysSo()
    {
        var snapshot = FullSnapshot() with { MemoryInstalledBytes = null, MemoryVisibleBytes = 8 * Gb, MemoryUsedBytes = 3 * Gb };

        var memory = PcHardwareReader.Describe(snapshot, norwegian: false).Memory;

        Assert.Equal("3 GB used of 8 GB", memory.Value);
        Assert.Contains("does not report installed memory", memory.Tooltip);
    }

    [Fact]
    public void Memory_WithoutUse_IsUnavailable()
    {
        var snapshot = FullSnapshot() with { MemoryUsedBytes = null };

        Assert.False(PcHardwareReader.Describe(snapshot, norwegian: false).Memory.Available);
    }

    [Theory]
    [InlineData(0.0)]      // 0 K offset left in, or a driver that reports nothing
    [InlineData(-273.15)]  // a raw value of zero
    [InlineData(-5.0)]
    [InlineData(130.0)]
    [InlineData(3276.7)]   // an unconnected sensor
    public void ImplausibleTemperatures_AreShownAsUnavailable(double celsius)
    {
        var snapshot = FullSnapshot() with
        {
            ThermalZones = new[] { new PcHardwareReader.ThermalZone(@"\_TZ.TZ00", celsius) },
            GpuAdapters = new[] { new PcHardwareReader.GpuAdapter("GPU", celsius) }
        };

        var display = PcHardwareReader.Describe(snapshot, norwegian: false);

        Assert.Equal("unavailable", display.CpuTemperature.Value);
        Assert.Equal("unavailable", display.GpuTemperature.Value);
    }

    [Fact]
    public void GpuThatReportsNoTemperature_IsUnavailable_AndNamedInTheTooltip()
    {
        var snapshot = FullSnapshot() with
        {
            GpuAdapters = new[] { new PcHardwareReader.GpuAdapter("Microsoft Basic Display Adapter", null) }
        };

        var gpu = PcHardwareReader.Describe(snapshot, norwegian: false).GpuTemperature;

        Assert.Equal("unavailable", gpu.Value);
        Assert.False(gpu.Available);
        Assert.Contains("Microsoft Basic Display Adapter", gpu.Tooltip);
    }

    [Fact]
    public void SeveralGpus_ShowTheHottest_AndListAllThatReport()
    {
        var snapshot = FullSnapshot() with
        {
            GpuAdapters = new[]
            {
                new PcHardwareReader.GpuAdapter("Intel(R) UHD Graphics", null),
                new PcHardwareReader.GpuAdapter("NVIDIA GeForce RTX 3060", 61.5),
                new PcHardwareReader.GpuAdapter("AMD Radeon RX 6600", 48.0)
            }
        };

        var gpu = PcHardwareReader.Describe(snapshot, norwegian: false).GpuTemperature;

        Assert.Equal("62 °C", gpu.Value);
        Assert.Contains("NVIDIA GeForce RTX 3060: 62 °C", gpu.Tooltip);
        Assert.Contains("AMD Radeon RX 6600: 48 °C", gpu.Tooltip);
        Assert.DoesNotContain("UHD", gpu.Tooltip);
    }

    [Fact]
    public void PickCpuZone_PrefersAZoneNamedForTheCpu_OtherwiseTheHottest()
    {
        var named = new[]
        {
            new PcHardwareReader.ThermalZone(@"\_TZ.BAT0", 55),
            new PcHardwareReader.ThermalZone(@"\_TZ.CPUZ", 41),
            new PcHardwareReader.ThermalZone(@"\_TZ.cpu1", 44)
        };
        var unnamed = new[]
        {
            new PcHardwareReader.ThermalZone(@"\_TZ.TZ00", 28),
            new PcHardwareReader.ThermalZone(@"\_TZ.TZ01", 51)
        };

        Assert.Equal(@"\_TZ.cpu1", PcHardwareReader.PickCpuZone(named)!.Name);
        Assert.Equal(@"\_TZ.TZ01", PcHardwareReader.PickCpuZone(unnamed)!.Name);
        Assert.Null(PcHardwareReader.PickCpuZone(Array.Empty<PcHardwareReader.ThermalZone>()));
    }

    [Fact]
    public void TenthsOfKelvin_ConvertToCelsius()
    {
        Assert.Equal(27.05, PcHardwareReader.TenthsOfKelvinToCelsius(3002), 2);
        Assert.Equal("27 °C", PcHardwareReader.FormatCelsius(PcHardwareReader.TenthsOfKelvinToCelsius(3002), Invariant));
    }

    [Fact]
    public void Read_OnThisMachine_ReturnsConsistentValues_AndNoImplausibleTemperatureIsShown()
    {
        var snapshot = PcHardwareReader.Read();

        Assert.True(snapshot.MemoryVisibleBytes > 0);
        Assert.InRange(snapshot.MemoryUsedBytes ?? -1, 1, snapshot.MemoryVisibleBytes!.Value);
        Assert.NotEmpty(snapshot.Drives);
        Assert.All(snapshot.Drives, d => Assert.InRange(d.UsedBytes, 0, d.TotalBytes));

        var display = PcHardwareReader.Describe(snapshot, norwegian: false);
        foreach (var row in new[] { display.CpuTemperature, display.GpuTemperature })
            Assert.True(row.Value == "unavailable" || row.Value.Contains(" °C"), row.Value);
    }

    [Fact]
    public void ThisPcWindow_ShowsHardwareBeforeNetwork_FilledFromABackgroundRead()
    {
        RunSta(() =>
        {
            int readerThread = -1;
            int uiThread = Environment.CurrentManagedThreadId;
            var window = new PcInfoWindow(
                norwegian: false,
                new MachineIdentityReader.Identity("Contoso", "Model 1", "P1", "S1", "PC-1"),
                "Windows 10 Pro 22H2",
                "19045.1",
                "1 day",
                "",
                new LocalNetworkReader.Info("10.0.0.5", "255.0.0.0", "10.0.0.1", "10.0.0.1", "—"),
                hardwareReader: () =>
                {
                    readerThread = Environment.CurrentManagedThreadId;
                    return FullSnapshot() with { ThermalZones = Array.Empty<PcHardwareReader.ThermalZone>() };
                })
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                ShowActivated = false
            };

            var failsafe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            failsafe.Tick += (_, _) => { failsafe.Stop(); window.Close(); };
            var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            List<string> texts = new();
            int copyButtonsInHardware = -1;
            poll.Tick += (_, _) =>
            {
                if (!window.HardwareLoaded) return;
                poll.Stop();
                texts = Descendants<TextBlock>(window).Select(t => t.Text).ToList();
                var cpuTemperatureRow = (StackPanel)VisualTreeHelper.GetParent(
                    Descendants<TextBlock>(window).Single(t => t.Text == "CPU temperature"));
                copyButtonsInHardware = Descendants<Button>(cpuTemperatureRow).Count();
                window.Close();
            };
            window.Loaded += (_, _) => { failsafe.Start(); poll.Start(); };
            window.ShowDialog();
            failsafe.Stop();

            Assert.True(window.HardwareLoaded, "The hardware rows were never filled.");
            Assert.NotEqual(-1, readerThread);
            Assert.NotEqual(uiThread, readerThread);

            int hardware = texts.IndexOf("Hardware");
            Assert.True(texts.IndexOf("Device") < hardware, "Hardware should come after Device.");
            Assert.True(hardware < texts.IndexOf("Network"), "Hardware should come before Network.");
            Assert.Equal(
                new[]
                {
                    "Processor", "Intel(R) Core(TM) i5-8350U CPU @ 1.70GHz",
                    "Memory", "9.5 GB used of 16 GB",
                    "Local storage", "301 GB used of 1.37 TB",
                    "CPU temperature", "unavailable",
                    "GPU temperature", "52 °C"
                },
                texts.Skip(hardware + 1).Take(10));
            Assert.Equal(0, copyButtonsInHardware);
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in Descendants<T>(child)) yield return deeper;
        }
    }

    private static void RunSta(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
