using StampCut.Core.FFmpeg;
using StampCut.Core.Models;
using Xunit;

namespace StampCut.Core.Tests;

public class HardwareTests
{
    [Fact]
    public void Recommendation_DriverTooOld()
    {
        var caps = new HardwareCapabilities
        {
            Encoders = { HardwareEncoder.MediaFoundation },
            GpuName = "NVIDIA GeForce GTX 1080", NvidiaDriver = "552.44",
            NvencFailure = "Driver does not support the required nvenc API version. Required: 13.0 Found: 12.2 Minimum required Nvidia driver for nvenc is 570.0 or newer",
            NvencRequiredDriver = "570.0",
        };
        Assert.Contains("GTX 1080", caps.Recommendation);
        Assert.Contains("552.44", caps.Recommendation);
        Assert.Contains("570.0 или новее", caps.Recommendation);
        Assert.Equal(HardwareEncoder.MediaFoundation, caps.Resolve(HardwareEncoder.Auto));
    }

    [Fact]
    public void Recommendation_EmptyWhenNvencWorksOrNoNvidia()
    {
        Assert.Equal("", new HardwareCapabilities { Encoders = { HardwareEncoder.Nvenc }, NvencFailure = "x" }.Recommendation);
        Assert.Equal("", new HardwareCapabilities { NvencFailure = "Cannot load nvcuda.dll" }.Recommendation);
    }

    [Fact]
    public void Resolve_PrefersNvencThenQsvThenAmfThenMf()
    {
        var all = new HardwareCapabilities { Encoders = { HardwareEncoder.MediaFoundation, HardwareEncoder.Amf, HardwareEncoder.Qsv, HardwareEncoder.Nvenc } };
        Assert.Equal(HardwareEncoder.Nvenc, all.Resolve(HardwareEncoder.Auto));
        Assert.Equal(HardwareEncoder.None, all.Resolve(HardwareEncoder.None));
        Assert.Equal(HardwareEncoder.None, new HardwareCapabilities().Resolve(HardwareEncoder.Nvenc));
    }
}
