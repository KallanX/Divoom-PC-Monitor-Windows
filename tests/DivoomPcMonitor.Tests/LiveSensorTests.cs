namespace DivoomPcMonitor.Tests;

public class LiveSensorTests
{
    [Fact]
    public async Task SamplerReadsMemoryWithoutPosting()
    {
        using var sensors = new WindowsSensorSource();
        var sample = await sensors.MeasureAsync(new GpuCard { Vendor = "Qualcomm", Name = "Adreno", Luid = "luid_0x00000000_0x00000000" }, ["C:"], CancellationToken.None);
        Assert.NotNull(sample.MemoryUtil);
        Assert.InRange(sample.MemoryUtil.Value, 0, 100);
        Assert.NotNull(sample.CpuUtil);
        Assert.InRange(sample.CpuUtil.Value, 0, 100);
    }
}
