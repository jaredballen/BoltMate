using LogiPlusSwitcher.Core.HidPp;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class HidPp10FrameTests
{
    [Fact]
    public void BuildBoltUnpairFrame_matches_solaar_wire_format()
    {
        var bytes = HidPp10.BuildBoltUnpairFrame(slot: 3).ToBytes();
        // 11 FF 82 C1 03 03 00...
        Assert.Equal(HidPpConstants.LongReportLength, bytes.Length);
        Assert.Equal(0x11, bytes[0]);
        Assert.Equal(0xFF, bytes[1]);
        Assert.Equal(0x82, bytes[2]);
        Assert.Equal(0xC1, bytes[3]);
        Assert.Equal(0x03, bytes[4]); // unpair sub-action
        Assert.Equal(0x03, bytes[5]); // slot
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void BuildBoltUnpairFrame_rejects_out_of_range_slots(byte slot)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HidPp10.BuildBoltUnpairFrame(slot));
    }

    [Fact]
    public void BuildReadReceiverInfoFrame_writes_sub_register_and_extra_byte()
    {
        var bytes = HidPp10.BuildReadReceiverInfoFrame(subRegister: 0x53, extraByte: 0x01).ToBytes();
        // 10 FF 83 B5 53 01 00
        Assert.Equal(HidPpConstants.ShortReportLength, bytes.Length);
        Assert.Equal(0x10, bytes[0]);
        Assert.Equal(0xFF, bytes[1]);
        Assert.Equal(0x83, bytes[2]);
        Assert.Equal(0xB5, bytes[3]);
        Assert.Equal(0x53, bytes[4]);
        Assert.Equal(0x01, bytes[5]);
    }

    [Fact]
    public void BuildReadBoltUniqueIdFrame_wire()
    {
        var bytes = HidPp10.BuildReadBoltUniqueIdFrame().ToBytes();
        Assert.Equal(new byte[] { 0x10, 0xFF, 0x83, 0xFB, 0x00, 0x00, 0x00 }, bytes);
    }

    [Fact]
    public void BuildWriteBoltDeviceNameFrame_truncates_overly_long_names()
    {
        // 13-char name should be truncated to 12.
        var bytes = HidPp10.BuildWriteBoltDeviceNameFrame(slot: 1, "ThirteenChars").ToBytes();
        Assert.Equal(0x11, bytes[0]);
        Assert.Equal(0xFF, bytes[1]);
        Assert.Equal(0x82, bytes[2]);
        Assert.Equal(0xB5, bytes[3]);
        Assert.Equal(0x61, bytes[4]); // 0x60 + 1 (slot)
        Assert.Equal(0x01, bytes[5]);
        Assert.Equal(12, bytes[6]); // length capped to 12
        // Bytes 7..18 = "ThirteenChar" (12 chars)
        Assert.Equal("ThirteenChar", System.Text.Encoding.ASCII.GetString(bytes, 7, 12));
    }

    [Fact]
    public void BuildWriteBoltDeviceNameFrame_rejects_empty_name()
    {
        Assert.Throws<ArgumentException>(() =>
            HidPp10.BuildWriteBoltDeviceNameFrame(slot: 1, string.Empty));
    }

    [Fact]
    public void Hidpp10Long_rejects_zero_length_parameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            HidPpFrame.Hidpp10Long(deviceIndex: 0xFF, subId: 0x82, parameters: ReadOnlySpan<byte>.Empty));
    }
}
