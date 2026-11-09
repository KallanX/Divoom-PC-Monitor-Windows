namespace DivoomPcMonitor.Tests;

public class LiveSensorTests
{
    [Fact]
    public async Task SamplerReadsMemoryWithoutPosting()
    {
        using var sensors = new WindowsSensorSource();
        var gpus = WindowsHardware.ListGpus();
        var selection = HardwareMath.Resolve(new MonitorConfig(), gpus, WindowsHardware.ReadCpuVendor);
        var sample = await sensors.MeasureAsync(selection.Gpu, ["C:"], CancellationToken.None);
        Assert.NotNull(sample.MemoryUtil);
        Assert.InRange(sample.MemoryUtil.Value, 0, 100);
        Assert.NotNull(sample.CpuUtil);
        Assert.InRange(sample.CpuUtil.Value, 0, 100);
        Assert.NotNull(sample.CpuTemp);
        Assert.InRange(sample.CpuTemp.Value, 0, 125);
        if (gpus.Count > 0)
        {
            Assert.NotNull(sample.GpuUtil);
            Assert.InRange(sample.GpuUtil.Value, 0, 100);
            Assert.NotNull(sample.GpuTemp);
            Assert.InRange(sample.GpuTemp.Value, 0, 125);
        }
    }
}
