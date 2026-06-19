using LogiPlusSwitcher.Core.HidPp;
using LogiPlusSwitcher.Core.HidPp.Notifications;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class NotificationParserTests
{
    [Fact]
    public void DjPairing_link_lost_parses_correctly()
    {
        // 10 02 41 10 40 ab cd  — slot 2, Bolt link, link-lost flag, WPID 0xCDAB (little-endian).
        var frame = HidPpFrame.TryParse([0x10, 0x02, 0x41, 0x10, 0x40, 0xAB, 0xCD])!.Value;

        Assert.True(DjPairingNotification.TryParse(frame, out var note));
        Assert.Equal((byte)0x02, note.DeviceIndex);
        Assert.True(note.IsBolt);
        Assert.True(note.LinkLost);
        Assert.False(note.LinkEstablished);
        Assert.Equal((ushort)0xCDAB, note.Wpid);
    }

    [Fact]
    public void DjPairing_link_established_parses_correctly()
    {
        var frame = HidPpFrame.TryParse([0x10, 0x01, 0x41, 0x10, 0x00, 0x12, 0x34])!.Value;

        Assert.True(DjPairingNotification.TryParse(frame, out var note));
        Assert.True(note.LinkEstablished);
        Assert.Equal((ushort)0x3412, note.Wpid);
    }

    [Fact]
    public void DjPairing_rejects_non_0x41_frames()
    {
        var frame = HidPpFrame.TryParse([0x10, 0x02, 0x40, 0x02, 0x00, 0x00, 0x00])!.Value;
        Assert.False(DjPairingNotification.TryParse(frame, out _));
    }

    [Fact]
    public void DjPairing_rejects_receiver_address()
    {
        var frame = HidPpFrame.TryParse([0x10, 0xFF, 0x41, 0x10, 0x40, 0x00, 0x00])!.Value;
        Assert.False(DjPairingNotification.TryParse(frame, out _));
    }

    [Fact]
    public void DivertedButtons_host_switch_press_decodes_to_target_host_2()
    {
        // Long report on slot 1, feature index 0x07 (our cached 0x1B04 idx),
        // fn=0 swid=0, CID = 0x00D3 (Host_Switch_Channel_3 -> host index 2).
        var bytes = new byte[HidPpConstants.LongReportLength];
        bytes[0] = HidPpConstants.ReportIdLong;
        bytes[1] = 0x01;
        bytes[2] = 0x07;
        bytes[3] = 0x00;
        bytes[4] = 0x00;
        bytes[5] = 0xD3;

        var frame = HidPpFrame.TryParse(bytes)!.Value;
        Assert.True(DivertedButtonsNotification.TryParse(frame, reprogControlsFeatureIndex: 0x07, out var note));
        Assert.False(note.IsReleaseEvent);
        Assert.True(note.ContainsHostSwitch);
        Assert.Equal(2, note.TargetHost);
        Assert.Equal((ushort?)0x00D3, note.Cid1);
    }

    [Fact]
    public void DivertedButtons_release_event_is_recognised()
    {
        var bytes = new byte[HidPpConstants.LongReportLength];
        bytes[0] = HidPpConstants.ReportIdLong;
        bytes[1] = 0x01;
        bytes[2] = 0x07;
        bytes[3] = 0x00;
        // all CID slots zero

        var frame = HidPpFrame.TryParse(bytes)!.Value;
        Assert.True(DivertedButtonsNotification.TryParse(frame, reprogControlsFeatureIndex: 0x07, out var note));
        Assert.True(note.IsReleaseEvent);
        Assert.Null(note.TargetHost);
    }

    [Fact]
    public void DivertedButtons_rejects_wrong_feature_index()
    {
        var bytes = new byte[HidPpConstants.LongReportLength];
        bytes[0] = HidPpConstants.ReportIdLong;
        bytes[1] = 0x01;
        bytes[2] = 0x07;
        bytes[3] = 0x00;
        bytes[4] = 0x00;
        bytes[5] = 0xD1;

        var frame = HidPpFrame.TryParse(bytes)!.Value;
        Assert.False(DivertedButtonsNotification.TryParse(frame, reprogControlsFeatureIndex: 0x08, out _));
    }

    [Fact]
    public void ChangeHostWriteSnoop_detects_LogiPlus_setCurrentHost_write()
    {
        // Logi Options+ writing host 1 to mouse on slot 2 via feature index 0x09.
        // Long report, function=1, sw_id=1 (Logi+'s sw_id), parameters [host, 0, 0].
        var bytes = new byte[HidPpConstants.LongReportLength];
        bytes[0] = HidPpConstants.ReportIdLong;
        bytes[1] = 0x02;
        bytes[2] = 0x09;
        bytes[3] = 0x11; // fn=1 swid=1
        bytes[4] = 0x01; // target host

        var frame = HidPpFrame.TryParse(bytes)!.Value;
        Assert.True(ChangeHostWriteSnoop.TryParse(frame, changeHostFeatureIndex: 0x09, ourSwId: HidPpConstants.OurSwId, out var snoop));
        Assert.Equal((byte)0x02, snoop.DeviceIndex);
        Assert.Equal((byte)0x01, snoop.TargetHost);
        Assert.Equal((byte)0x01, snoop.SwId);
    }

    [Fact]
    public void ChangeHostWriteSnoop_ignores_our_own_writes()
    {
        var bytes = new byte[HidPpConstants.LongReportLength];
        bytes[0] = HidPpConstants.ReportIdLong;
        bytes[1] = 0x02;
        bytes[2] = 0x09;
        bytes[3] = (byte)((1 << 4) | HidPpConstants.OurSwId); // fn=1, swid=ours
        bytes[4] = 0x01;

        var frame = HidPpFrame.TryParse(bytes)!.Value;
        Assert.False(ChangeHostWriteSnoop.TryParse(frame, changeHostFeatureIndex: 0x09, ourSwId: HidPpConstants.OurSwId, out _));
    }
}
