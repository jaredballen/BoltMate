using LogiPlusSwitcher.Core.HidPp;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class HidPpFrameTests
{
    [Fact]
    public void Short_request_serialises_to_seven_bytes_with_packed_function_and_swid()
    {
        var frame = HidPpFrame.Short(
            deviceIndex: 0x01,
            featureIndex: 0x05,
            function: 0x2,
            swId: HidPpConstants.OurSwId,
            parameters: [0xAB, 0xCD]);

        var bytes = frame.ToBytes();

        Assert.Equal(HidPpConstants.ShortReportLength, bytes.Length);
        Assert.Equal(0x10, bytes[0]);
        Assert.Equal(0x01, bytes[1]);
        Assert.Equal(0x05, bytes[2]);
        Assert.Equal(0x2E, bytes[3]); // function=2 in high nibble, swid=0xE in low nibble
        Assert.Equal(0xAB, bytes[4]);
        Assert.Equal(0xCD, bytes[5]);
        Assert.Equal(0x00, bytes[6]);
    }

    [Fact]
    public void Long_request_serialises_to_twenty_bytes_and_pads_parameters()
    {
        var frame = HidPpFrame.Long(
            deviceIndex: 0x02,
            featureIndex: 0x09,
            function: 0x0,
            swId: HidPpConstants.OurSwId,
            parameters: [0x00, 0xD1]);

        var bytes = frame.ToBytes();

        Assert.Equal(HidPpConstants.LongReportLength, bytes.Length);
        Assert.Equal(0x11, bytes[0]);
        Assert.Equal(0x02, bytes[1]);
        Assert.Equal(0x09, bytes[2]);
        Assert.Equal(0x0E, bytes[3]);
        Assert.Equal(0x00, bytes[4]);
        Assert.Equal(0xD1, bytes[5]);
        for (var i = 6; i < bytes.Length; i++)
            Assert.Equal(0x00, bytes[i]);
    }

    [Fact]
    public void TryParse_short_report_round_trips()
    {
        ReadOnlySpan<byte> wire = [0x10, 0xFF, 0x80, 0x00, 0x09, 0x00, 0x00];
        var frame = HidPpFrame.TryParse(wire);

        Assert.NotNull(frame);
        Assert.True(frame!.Value.IsShort);
        Assert.Equal(0xFF, frame.Value.DeviceIndex);
        Assert.Equal(0x80, frame.Value.FeatureIndex);
        Assert.Equal(0x00, frame.Value.FunctionAndSwId);
        Assert.Equal(0, frame.Value.Function);
        Assert.Equal(0, frame.Value.SwId);
    }

    [Fact]
    public void TryParse_long_report_round_trips()
    {
        // Synthetic divertedButtonsEvent for host 2 (CID 0x00D2) on slot 1, feature index 0x05.
        var wire = new byte[HidPpConstants.LongReportLength];
        wire[0] = 0x11;
        wire[1] = 0x01;
        wire[2] = 0x05;
        wire[3] = 0x00; // function 0, swid 0 (= notification from device)
        wire[4] = 0x00;
        wire[5] = 0xD2;

        var frame = HidPpFrame.TryParse(wire);

        Assert.NotNull(frame);
        Assert.True(frame!.Value.IsLong);
        Assert.Equal(0x01, frame.Value.DeviceIndex);
        Assert.Equal(0x05, frame.Value.FeatureIndex);
        Assert.Equal(0xD2, (int)frame.Value.ReadUInt16BigEndian(0));
    }

    [Fact]
    public void TryParse_rejects_unknown_report_id()
    {
        ReadOnlySpan<byte> wire = [0x99, 0x00, 0x00, 0x00];
        Assert.Null(HidPpFrame.TryParse(wire));
    }

    [Fact]
    public void Function_and_swid_pack_and_unpack_symmetrically()
    {
        for (var fn = 0; fn < 16; fn++)
        for (var sw = 0; sw < 16; sw++)
        {
            var frame = HidPpFrame.Short(0x01, 0x02, fn, sw);
            Assert.Equal(fn, frame.Function);
            Assert.Equal(sw, frame.SwId);
        }
    }

    [Fact]
    public void EnableHidppNotifications_short_request_matches_solaar_wire_format()
    {
        // From Solaar / CleverSwitch: enable wireless+software-present notifications on receiver.
        // 10 FF 80 00 00 09 00
        var frame = HidPpFrame.Hidpp10Short(
            deviceIndex: HidPpConstants.DeviceIndexReceiver,
            subId: 0x80,
            parameters: [0x00, 0x00, 0x09, 0x00]);

        var bytes = frame.ToBytes();

        Assert.Equal(new byte[] { 0x10, 0xFF, 0x80, 0x00, 0x00, 0x09, 0x00 }, bytes);
    }

    [Fact]
    public void Function_argument_validates_upper_bound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HidPpFrame.Short(0x01, 0x02, function: 0x10, swId: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HidPpFrame.Short(0x01, 0x02, function: 0, swId: 0x10));
    }
}
