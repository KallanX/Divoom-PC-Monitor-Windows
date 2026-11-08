using System.Text.Json;

namespace DivoomPcMonitor.Tests;

public class BehaviorTests
{
    private static readonly string Degree = "0" + DisplayText.Degree + "C";

    [Fact]
    public void FormatsBlankAndZero()
    {
        Assert.Equal("--", DisplayText.Percent(null));
        Assert.Equal("0%", DisplayText.Percent(0));
        Assert.Equal("--", DisplayText.Temperature(null));
        Assert.Equal(Degree, DisplayText.Temperature(0));
    }

    [Fact]
    public void ThermalRejectsSentinelsAndKeepsFreezing()
    {
        Assert.Null(HardwareMath.ThermalCelsiusRounded(null));
        Assert.Null(HardwareMath.ThermalCelsiusRounded(0));
        Assert.Null(HardwareMath.ThermalCelsiusRounded(2330));
        Assert.Equal(0, HardwareMath.ThermalCelsiusRounded(2732));
        Assert.Equal(37, HardwareMath.ThermalCelsiusRounded(3102));
        Assert.Equal(37, HardwareMath.ThermalCelsiusRounded(310));
    }

    [Fact]
    public void ThermalPrefersNamedZonesAndWarmestCpu()
    {
        var zones = new[]
        {
            new ThermalZone { Name = @"\_SB.TZ37", DeciKelvin = 2330 },
            new ThermalZone { Name = @"\_SB.TZ0", DeciKelvin = 3090 },
            new ThermalZone { Name = @"\_SB.TZ99", DeciKelvin = 3102 },
            new ThermalZone { Name = "cpu-pkg", DeciKelvin = 3050 },
            new ThermalZone { Name = "gpu-hot", DeciKelvin = 3200 },
        };

        var cpu = HardwareMath.SelectThermal(zones, "Cpu");
        Assert.NotNull(cpu);
        Assert.Equal("cpu-pkg", cpu.Name);
        Assert.Equal(32, cpu.Celsius);

        var gpu = HardwareMath.SelectThermal(zones, "Gpu");
        Assert.NotNull(gpu);
        Assert.Equal("gpu-hot", gpu.Name);
        Assert.Equal(47, gpu.Celsius);

        var unnamed = new[]
        {
            new ThermalZone { Name = @"\_SB.TZ37", DeciKelvin = 2330 },
            new ThermalZone { Name = @"\_SB.TZ99", DeciKelvin = 3102 },
        };
        var hottest = HardwareMath.SelectThermal(unnamed, "Cpu");
        Assert.NotNull(hottest);
        Assert.Equal(@"\_SB.TZ99", hottest.Name);
        Assert.Equal(37, hottest.Celsius);
        Assert.Null(HardwareMath.SelectThermal(unnamed, "Gpu"));
    }

    [Fact]
    public void PayloadKeepsSixFieldsAndAJsonArray()
    {
        var payload = SystemPayload.Create("0", null, 0, 12, null, 40, 7);
        Assert.Equal(6, payload.ScreenList[0].DispData.Length);
        Assert.Equal("--", payload.ScreenList[0].DispData[0]);
        Assert.Equal("12%", payload.ScreenList[0].DispData[1]);
        Assert.Equal(Degree, payload.ScreenList[0].DispData[2]);
        Assert.Equal("--", payload.ScreenList[0].DispData[3]);
        Assert.Equal("40%", payload.ScreenList[0].DispData[4]);
        Assert.Equal("7%", payload.ScreenList[0].DispData[5]);
        Assert.Equal(0, payload.ScreenList[0].LcdId);

        var json = payload.ToJson();
        Assert.Matches("\"ScreenList\":\\s*\\[", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("ScreenList").ValueKind);
        Assert.Contains("\u00B0", json);
    }

    [Fact]
    public void InvalidLcdIndexThrows()
    {
        Assert.Throws<FormatException>(() => SystemPayload.Create("nope", 1, 1, 1, 1, 1, 1));
    }

    [Fact]
    public void ClockSelectAlwaysUses625()
    {
        var json = DivoomDeviceClient.ClockJson("300240719", "0", "0", "42");
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Channel/SetClockSelectId", document.RootElement.GetProperty("Command").GetString());
        Assert.Equal(625, document.RootElement.GetProperty("ClockId").GetInt32());
        Assert.Equal(300240719, document.RootElement.GetProperty("DeviceId").GetInt64());
        Assert.DoesNotContain("42", json);
    }

    [Fact]
    public void HoldKeepsZeroAndIgnoresALaterMiss()
    {
        var last = new ReadingHold();
        Assert.Equal(0, last.Update("cpu_util", 0));
        Assert.Equal(0, last.Update("cpu_util", null));
        Assert.Null(last.Update("cpu_temp", null));
    }

    [Fact]
    public void PciDropsTheDomain()
    {
        Assert.Equal("01:00.0", HardwareMath.ShortenPci("00000000:01:00.0"));
        Assert.Equal("0c:00.0", HardwareMath.ShortenPci("0000:0C:00.0"));
        Assert.Equal("01:00.0", HardwareMath.ShortenPci("01:00.0"));
    }

    [Fact]
    public void NvidiaMatchesPciAndDoesNotGuess()
    {
        var nvidia = HardwareMath.ParseNvidiaCsv("00000000:01:00.0, 12, 55\n00000000:02:00.0, 80, 70\n", "02:00.0");
        Assert.Equal(80, nvidia.Util);
        Assert.Equal(70, nvidia.Temp);

        var missing = HardwareMath.ParseNvidiaCsv("00000000:01:00.0, 12, 55\n", "03:00.0");
        Assert.Null(missing.Util);
        Assert.Null(missing.Temp);

        var only = HardwareMath.ParseNvidiaCsv("00000000:01:00.0, 0, 36", "");
        Assert.Equal(0, only.Util);
        Assert.Equal(36, only.Temp);
    }

    [Fact]
    public void LinuxDiskFormulaRanksTheAllowedDisk()
    {
        var best = HardwareMath.HighestDisk(
            0,
            new Dictionary<string, double> { ["nvme0n1"] = 1000, ["sda"] = 1000, ["loop0"] = 1000 },
            1,
            new Dictionary<string, double> { ["nvme0n1"] = 1500, ["sda"] = 1100, ["loop0"] = 2000 },
            ["nvme0n1", "sda"]);
        Assert.Equal("nvme0n1", best.Name);
        Assert.Equal(50, best.Util);

        var limited = HardwareMath.HighestDisk(
            0,
            new Dictionary<string, double> { ["nvme0n1"] = 0, ["sda"] = 0 },
            1,
            new Dictionary<string, double> { ["nvme0n1"] = 1000, ["sda"] = 10 },
            ["sda"]);
        Assert.Equal("sda", limited.Name);
        Assert.Equal(1, limited.Util);

        var unknown = HardwareMath.HighestDisk(1, new Dictionary<string, double> { ["sda"] = 0 }, 1, new Dictionary<string, double> { ["sda"] = 0 }, ["sda"]);
        Assert.Null(unknown.Name);
        Assert.Null(unknown.Util);
    }

    [Fact]
    public void WindowsDiskUsesIdleTime()
    {
        var before = new Dictionary<string, DiskSample>
        {
            ["C:"] = new() { Idle = 29188463609, Timestamp = 134386291002456168 },
            ["D:"] = new() { Idle = 0, Timestamp = 134386291002456168 },
        };
        var after = new Dictionary<string, DiskSample>
        {
            ["C:"] = new() { Idle = 29188463609 + 2500000, Timestamp = 134386291002456168 + 10000000 },
            ["D:"] = new() { Idle = 0, Timestamp = 134386291002456168 + 10000000 },
        };

        var busy = HardwareMath.BusierLogicalDisk(before, after, ["C:", "D:"]);
        Assert.Equal("D:", busy.Name);
        Assert.Equal(100, busy.Util);

        var onlyC = HardwareMath.BusierLogicalDisk(before, after, ["c:"]);
        Assert.Equal("C:", onlyC.Name);
        Assert.Equal(75, onlyC.Util);
    }

    [Fact]
    public void GpuEngineUsesTheBusierEngineOnThePinnedAdapter()
    {
        var samples = new[]
        {
            new EngineSample { InstanceName = "pid_1_luid_0x00000000_0x00010c1b_phys_0_eng_0_engtype_3d", CookedValue = 10 },
            new EngineSample { InstanceName = "pid_2_luid_0x00000000_0x00010c1b_phys_0_eng_0_engtype_3d", CookedValue = 15 },
            new EngineSample { InstanceName = "pid_2_luid_0x00000000_0x00010c1b_phys_0_eng_1_engtype_compute", CookedValue = 40 },
            new EngineSample { InstanceName = "pid_3_luid_0x00000000_0x00099999_phys_0_eng_0_engtype_3d", CookedValue = 90 },
        };

        Assert.Equal(40, HardwareMath.GpuUsage(samples, "luid_0x00000000_0x00010C1B"));
        Assert.Equal(90, HardwareMath.GpuUsage(samples, "luid_0x00000000_0x00099999"));
        Assert.Null(HardwareMath.GpuUsage(samples, "luid_0x00000000_0x00000001"));
    }

    [Fact]
    public void DefaultGpuPrefersTheFirstDiscreteCard()
    {
        var chosen = HardwareMath.SelectDefault(
        [
            new GpuCard { Vendor = "Intel", Pci = "00:02.0", Name = "UHD" },
            new GpuCard { Vendor = "NVIDIA", Pci = "01:00.0", Name = "RTX 3080" },
            new GpuCard { Vendor = "AMD", Pci = "03:00.0", Name = "Navi" },
        ]);
        Assert.NotNull(chosen);
        Assert.Equal("01:00.0", chosen.Pci);

        var only = HardwareMath.SelectDefault([new GpuCard { Vendor = "Qualcomm", Name = "Adreno", Luid = "luid_0x00000000_0x00010c1b" }]);
        Assert.NotNull(only);
        Assert.Equal("Adreno", only.Name);
    }

    [Fact]
    public void PinnedGpuDropsTheDomainAndIgnoresABadAddress()
    {
        var pinned = HardwareMath.Resolve(new MonitorConfig
        {
            CpuVendor = "AMD",
            GpuVendor = "NVIDIA",
            GpuPci = "0000:01:00.0",
            GpuName = "RTX",
        }, [new GpuCard { Vendor = "Intel", Pci = "00:02.0", Name = "UHD" }]);
        Assert.Equal("AMD", pinned.CpuVendor);
        Assert.Equal("01:00.0", pinned.Gpu.Pci);
        Assert.Equal("RTX", pinned.Gpu.Name);
        Assert.Equal("NVIDIA", pinned.Gpu.Vendor);

        var cleared = HardwareMath.FromConfig(new MonitorConfig { GpuPci = "not-a-bus", GpuName = "RTX" });
        Assert.NotNull(cleared);
        Assert.Equal("", cleared.Pci);
        Assert.Null(HardwareMath.FromConfig(new MonitorConfig()));
    }

    [Fact]
    public void NvidiaZeroIsNotReplacedByTheEngineCounter()
    {
        var reading = HardwareMath.CombineGpu(
            new GpuCard { Vendor = "NVIDIA", Pci = "01:00.0", Luid = "luid_0x00000000_0x00010c1b" },
            [new EngineSample { InstanceName = "pid_1_luid_0x00000000_0x00010c1b_phys_0_eng_0_engtype_3d", CookedValue = 80 }],
            new VendorReading(),
            new NvidiaRow { Util = 0, Temp = 36, Status = "match", Pci = "01:00.0" });
        Assert.Equal(0, reading.Util);
        Assert.Equal(36, reading.Temp);
    }

    [Fact]
    public void SetupStepsFollowTheDeviceShape()
    {
        Assert.Equal("Device,Drive,Review", string.Join(',', HardwareMath.PlanSteps(0, pixoo: true, gpuCount: 1)));
        Assert.Equal("Device,LcdGroup,Lcd,Drive,Gpu,Review", string.Join(',', HardwareMath.PlanSteps(2, pixoo: false, gpuCount: 2)));
    }

    [Fact]
    public void ConfigRoundTripKeepsOneDriveAndAPercentInTheName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "divoom-config-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "config.env");
            ConfigStore.Write(path, "1", "192.168.1.20", "0", "0", "625", "C:", "AMD", new GpuCard { Vendor = "NVIDIA", Pci = "01:00.0", Name = "GPU 100%" });
            var config = ConfigStore.Read(path);
            Assert.Single(config.HardDrive);
            Assert.Equal("C:", config.HardDrive[0]);
            Assert.Equal("GPU 100%", config.GpuName);
            Assert.False(File.Exists(path + ".tmp"));
            var text = File.ReadAllText(path);
            Assert.StartsWith("[DEFAULT]\n", text);
            Assert.DoesNotContain("\r", text);
            Assert.Contains("gpu_name=GPU 100%\n", text);

            ConfigStore.Write(
                path,
                "1",
                "192.168.1.20",
                "0",
                "1",
                "625",
                new[] { "C:", "D:" },
                "Intel",
                new GpuCard { Vendor = "Intel", Pci = "00:02.0", Name = "UHD", Luid = "luid_0x00000000_0x00000001" });
            config = ConfigStore.Read(path);
            Assert.Equal("C:,D:", string.Join(',', config.HardDrive));
            Assert.Equal("1", config.LcdIndex);
            Assert.Equal("luid_0x00000000_0x00000001", config.GpuLuid);

            var legacy = Path.Combine(directory, "legacy.env");
            File.WriteAllText(legacy, "DeviceID=300240719\r\nDeviceIP=192.168.1.232\r\nLcdIndependence=0\r\nLcdIndex=0\r\nLcdClockId=625\r\nSelectedDriveLetters=C:,D:\r\n");
            var old = ConfigStore.Read(legacy);
            Assert.Equal("192.168.1.232", old.DeviceIp);
            Assert.Equal("C:,D:", string.Join(',', old.HardDrive));
            Assert.Equal("", old.GpuVendor);

            var broken = Path.Combine(directory, "broken.env");
            File.WriteAllText(broken, "[DEFAULT]\nlcd_index=0\n");
            Assert.Throws<ConfigException>(() => ConfigStore.Read(broken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InstalledConfigIsFoundByWalkingUpAndIsNotRewritten()
    {
        var root = Path.Combine(Path.GetTempPath(), "divoom-walk-" + Guid.NewGuid().ToString("n"));
        var start = Path.Combine(root, "tests", "DivoomPcMonitor.Tests");
        Directory.CreateDirectory(start);
        var configPath = Path.Combine(root, "config.env");
        File.WriteAllText(configPath, "[DEFAULT]\ndevice_id=1\ndevice_ip=10.1.2.3\nlcd_independence=0\nlcd_index=0\nlcd_clock_id=625\nhard_drive=C:\n");
        var before = File.GetLastWriteTimeUtc(configPath);
        var length = new FileInfo(configPath).Length;
        try
        {
            var found = ConfigLocator.Find(start, Path.Combine(root, "tests"));
            Assert.Equal(Path.GetFullPath(configPath), found is null ? null : Path.GetFullPath(found));
            var config = ConfigStore.Read(configPath);
            Assert.Equal("10.1.2.3", config.DeviceIp);
            Assert.Equal("C:", config.HardDrive[0]);
            Assert.Equal(before, File.GetLastWriteTimeUtc(configPath));
            Assert.Equal(length, new FileInfo(configPath).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DefaultVersionIsZeroOneZeroAndIntervalBelowOneIsRejected()
    {
        Assert.Equal("0.1.0", Product.Version);
        var engine = new MonitorEngine(new FakeSensors(), new FakePoster());
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.IntervalSeconds = 0);
    }

    [Fact]
    public void LoginCommandStartsInTheBackgroundWithoutElevation()
    {
        Assert.Equal("\"C:\\Apps\\Divoom PC Monitor\\DivoomPcMonitor.exe\" --background", LoginStartup.CommandLine(@"C:\Apps\Divoom PC Monitor\DivoomPcMonitor.exe"));
    }

    [Fact]
    public async Task EngineWaitsTheFullIntervalAfterTheUpdateAndCanSkipThePost()
    {
        var directory = Path.Combine(Path.GetTempPath(), "divoom-engine-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.env");
        ConfigStore.Write(path, "1", "192.168.1.20", "0", "0", "625", "C:", "Qualcomm", new GpuCard
        {
            Vendor = "Qualcomm",
            Name = "Adreno",
            Luid = "luid_0x00000000_0x00010c1b",
        });

        var sensors = new FakeSensors
        {
            Next = new RawSample { CpuUtil = 0, CpuTemp = null, GpuUtil = 12, GpuTemp = null, MemoryUtil = 40, DiskUtil = 0, DiskName = "C:" },
        };
        var poster = new FakePoster();
        var waits = new List<TimeSpan>();
        var engine = new MonitorEngine(sensors, poster, delay: (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        })
        {
            IntervalSeconds = 30,
            Testing = true,
        };

        using var cancel = new CancellationTokenSource();
        engine.Updated += _ => cancel.Cancel();
        await engine.RunAsync(() => path, cancel.Token);

        Assert.Empty(poster.Posts);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(waits));
        Assert.Equal(0, sensors.SeenDrives is null ? -1 : sensors.SeenDrives.Count == 1 && sensors.SeenDrives[0] == "C:" ? 0 : -1);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task EngineHoldsTheLastReadingAndPostsIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "divoom-engine-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.env");
        ConfigStore.Write(path, "9", "10.0.0.8", "0", "0", "625", "C:", "AMD", new GpuCard { Vendor = "AMD", Pci = "01:00.0", Name = "Navi" });

        var sensors = new FakeSensors();
        var samples = new Queue<RawSample>();
        samples.Enqueue(new RawSample { CpuUtil = 10, CpuTemp = 40, GpuUtil = 5, GpuTemp = 50, MemoryUtil = 20, DiskUtil = 1, DiskName = "C:", CpuTempSource = "ACPI thermal zone TZ0" });
        samples.Enqueue(new RawSample());
        sensors.Queue = samples;
        var poster = new FakePoster();
        var cycles = 0;
        var engine = new MonitorEngine(sensors, poster, delay: (_, _) =>
        {
            cycles++;
            return Task.CompletedTask;
        })
        {
            IntervalSeconds = 2,
        };

        using var cancel = new CancellationTokenSource();
        engine.Updated += reading =>
        {
            if (cycles >= 1)
            {
                Assert.Equal(10, reading.CpuUtil);
                Assert.Equal(40, reading.CpuTemp);
                cancel.Cancel();
            }
        };
        await engine.RunAsync(() => path, cancel.Token);

        Assert.Equal(2, poster.Posts.Count);
        Assert.Equal("10%", poster.Posts[1].CpuUtilText);
        Assert.Equal("40\u00B0C", poster.Posts[1].CpuTempText);
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task ABadConfigDoesNotStopTheLoop()
    {
        var waits = new List<TimeSpan>();
        var poster = new FakePoster();
        using var cancel = new CancellationTokenSource();
        var engine = new MonitorEngine(new FakeSensors(), poster, delay: (delay, _) =>
        {
            waits.Add(delay);
            cancel.Cancel();
            return Task.CompletedTask;
        })
        {
            IntervalSeconds = 7,
        };
        await engine.RunAsync(() => Path.Combine(Path.GetTempPath(), "missing-divoom-config.env"), cancel.Token);
        Assert.Empty(poster.Posts);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(waits));
    }

    private sealed class FakeSensors : ISensorSource
    {
        public RawSample Next { get; set; } = new();
        public Queue<RawSample>? Queue { get; set; }
        public IReadOnlyList<string>? SeenDrives { get; private set; }

        public Task<RawSample> MeasureAsync(GpuCard gpu, IReadOnlyList<string> drives, CancellationToken cancellationToken)
        {
            SeenDrives = drives;
            var sample = Queue is { Count: > 0 } ? Queue.Dequeue() : Next;
            return Task.FromResult(sample);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePoster : IDevicePoster
    {
        public List<DisplayReading> Posts { get; } = [];

        public Task<bool> SendSystemAsync(MonitorConfig config, DisplayReading reading, bool verbose, CancellationToken cancellationToken)
        {
            Posts.Add(reading);
            return Task.FromResult(true);
        }

        public Task<bool> SelectClockAsync(string deviceId, string deviceIp, string lcdIndependence, string lcdIndex, string lcdClockId, bool verbose, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }
    }
}
