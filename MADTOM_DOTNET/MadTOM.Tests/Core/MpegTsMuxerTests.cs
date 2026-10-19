using System;
using MADTOM.Media.Contracts;
using Xunit;

namespace MadTOM.Tests.Core;

public class MpegTsMuxerTests
{
    [Fact]
    public void MpegTsMuxer_ProducesExact188BytePacketsWithSyncBytes()
    {
        var muxer = new MpegTsMuxer(VideoCodecType.H264);

        // Dummy NAL unit: Annex-B start code + slice data
        var nalu = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1E, 0x01, 0x02, 0x03, 0x04 };
        var pts = 90000; // 1 second at 90kHz

        var tsBytes = muxer.MuxAccessUnit(nalu, pts, isKeyframe: true);

        Assert.NotEmpty(tsBytes);
        Assert.Equal(0, tsBytes.Length % 188); // Must be a multiple of 188 bytes!

        int packetCount = tsBytes.Length / 188;
        Assert.True(packetCount >= 3); // At least PAT, PMT, and 1 PES packet

        // Every packet MUST start with sync byte 0x47
        for (int i = 0; i < packetCount; i++)
        {
            Assert.Equal(0x47, tsBytes[i * 188]);
        }

        // First packet is PAT (PID 0)
        int pid0 = ((tsBytes[1] & 0x1F) << 8) | tsBytes[2];
        Assert.Equal(0x0000, pid0);

        // Second packet is PMT (PID 0x0100)
        int pid1 = ((tsBytes[189] & 0x1F) << 8) | tsBytes[190];
        Assert.Equal(0x0100, pid1);

        // Third packet is Video PES (PID 0x0101)
        int pid2 = ((tsBytes[377] & 0x1F) << 8) | tsBytes[378];
        Assert.Equal(0x0101, pid2);
    }

    [Fact]
    public void MpegTsMuxer_HandlesLargeFramesRequiringFragmentation()
    {
        var muxer = new MpegTsMuxer(VideoCodecType.Hevc);

        // 50 KB frame
        var largeFrame = new byte[50_000];
        Random.Shared.NextBytes(largeFrame);

        var tsBytes = muxer.MuxAccessUnit(largeFrame, 180000, isKeyframe: false);

        Assert.Equal(0, tsBytes.Length % 188);
        int packetCount = tsBytes.Length / 188;

        for (int i = 0; i < packetCount; i++)
        {
            Assert.Equal(0x47, tsBytes[i * 188]);
        }
    }
}
