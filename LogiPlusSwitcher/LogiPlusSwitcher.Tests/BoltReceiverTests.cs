using DynamicData;
using LogiPlusSwitcher.Core.Bolt;
using LogiPlusSwitcher.Core.Hid;
using LogiPlusSwitcher.Core.HidPp;
using LogiPlusSwitcher.Core.HidPp.Notifications;
using LogiPlusSwitcher.Tests.Support;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class BoltReceiverTests
{
    private static BoltReceiverInfo Info() =>
        new("/test/path", "ABC123", "Logitech Bolt Receiver", "Logitech", 0x0001);

    [Fact]
    public void Start_writes_enable_notifications_then_enumerate_devices()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        receiver.Start();

        var writes = conn.Writes.ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(new byte[] { 0x10, 0xFF, 0x80, 0x00, 0x00, 0x09, 0x00 }, writes[0].ToBytes());
        Assert.Equal(new byte[] { 0x10, 0xFF, 0x80, 0x02, 0x02, 0x00, 0x00 }, writes[1].ToBytes());
        Assert.True(conn.Started);
    }

    [Fact]
    public void Link_established_notification_creates_slot_and_appears_in_cache()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        var frame = HidPpFrame.TryParse([0x10, 0x03, 0x41, 0x10, 0x00, 0x12, 0x34])!.Value;
        conn.Inject(frame);

        var device = receiver.Devices.Lookup(3);
        Assert.True(device.HasValue);
        Assert.True(device.Value.LinkUp);
        Assert.Equal((ushort)0x3412, device.Value.Wpid);
    }

    [Fact]
    public void Link_lost_notification_flips_LinkUp_state()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        // Seed link-up first
        conn.Inject(HidPpFrame.TryParse([0x10, 0x02, 0x41, 0x10, 0x00, 0xAA, 0xBB])!.Value);
        Assert.True(receiver.TryGetDevice(2)!.LinkUp);

        conn.Inject(HidPpFrame.TryParse([0x10, 0x02, 0x41, 0x10, 0x40, 0xAA, 0xBB])!.Value);
        Assert.False(receiver.TryGetDevice(2)!.LinkUp);
    }

    [Fact]
    public void HostSwitch_press_routes_through_cached_feature_index()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        var slot = receiver.EnsureSlot(1);
        slot.ReprogControlsIndex = 0x07;

        DivertedButtonsNotification? raised = null;
        using var sub = receiver.HostSwitchPresses.Subscribe(ev => raised = ev);

        var buffer = new byte[HidPpConstants.LongReportLength];
        buffer[0] = 0x11;
        buffer[1] = 0x01;
        buffer[2] = 0x07;
        buffer[3] = 0x00;
        buffer[4] = 0x00;
        buffer[5] = 0xD3; // Host 3 → index 2
        conn.Inject(HidPpFrame.TryParse(buffer)!.Value);

        Assert.NotNull(raised);
        Assert.Equal(2, raised!.Value.TargetHost);
    }

    [Fact]
    public void HostSwitch_press_ignored_when_no_feature_index_cached()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        var slot = receiver.EnsureSlot(1);
        Assert.Null(slot.ReprogControlsIndex);

        var raised = false;
        using var sub = receiver.HostSwitchPresses.Subscribe(_ => raised = true);

        var buffer = new byte[HidPpConstants.LongReportLength];
        buffer[0] = 0x11;
        buffer[1] = 0x01;
        buffer[2] = 0x07;
        buffer[3] = 0x00;
        buffer[5] = 0xD1;
        conn.Inject(HidPpFrame.TryParse(buffer)!.Value);

        Assert.False(raised);
    }

    [Fact]
    public void Flow_snoop_routes_through_cached_change_host_index()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        var slot = receiver.EnsureSlot(2);
        slot.ChangeHostIndex = 0x09;

        ChangeHostWriteSnoop? raised = null;
        using var sub = receiver.FlowHostSwitches.Subscribe(snoop => raised = snoop);

        // Foreign write: fn=1, swid=1 (not ours which is 0x0E).
        var buffer = new byte[HidPpConstants.LongReportLength];
        buffer[0] = 0x11;
        buffer[1] = 0x02;
        buffer[2] = 0x09;
        buffer[3] = (1 << 4) | 0x01;
        buffer[4] = 0x02; // target host = 2
        conn.Inject(HidPpFrame.TryParse(buffer)!.Value);

        Assert.NotNull(raised);
        Assert.Equal((byte)2, raised!.Value.TargetHost);
    }

    [Fact]
    public void TrySwitchHost_writes_long_setCurrentHost_to_device()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        receiver.EnsureSlot(1).ChangeHostIndex = 0x09;

        var ok = receiver.TrySwitchHost(deviceIndex: 1, targetHost: 2);

        Assert.True(ok);
        var written = conn.Writes.Last();
        Assert.True(written.IsLong);
        Assert.Equal((byte)1, written.DeviceIndex);
        Assert.Equal((byte)0x09, written.FeatureIndex);
        Assert.Equal(1, written.Function);
        Assert.Equal(HidPpConstants.OurSwId, written.SwId);
        Assert.Equal((byte)2, written.Parameters.Span[0]);
    }

    [Fact]
    public void TrySwitchHost_returns_false_when_device_lacks_change_host_support()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        receiver.EnsureSlot(1); // no ChangeHostIndex set

        Assert.False(receiver.TrySwitchHost(deviceIndex: 1, targetHost: 0));
        Assert.Empty(conn.Writes);
    }

    [Fact]
    public async Task UnpairAsync_writes_bolt_pairing_register_and_removes_slot()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);

        // Seed slot 3
        receiver.EnsureSlot(3).LinkUp = true;
        Assert.NotNull(receiver.TryGetDevice(3));

        // Echo back the success reply when we see the unpair write.
        using var responder = conn.RespondWith(written =>
        {
            if (written is { IsLong: true, FeatureIndex: 0x82 } && written.FunctionAndSwId == 0xC1)
            {
                // Receiver echoes the same payload back to confirm.
                var echo = new byte[HidPpConstants.LongReportLength];
                echo[0] = 0x11;
                echo[1] = 0xFF;
                echo[2] = 0x82;
                echo[3] = 0xC1;
                echo[4] = 0x03;
                echo[5] = written.Parameters.Span[2];
                conn.Inject(HidPpFrame.TryParse(echo)!.Value);
            }
        });

        var ok = await receiver.UnpairAsync(3);

        Assert.True(ok);
        Assert.Null(receiver.TryGetDevice(3));

        var sent = conn.Writes.Last();
        Assert.True(sent.IsLong);
        Assert.Equal((byte)0xFF, sent.DeviceIndex);
        Assert.Equal((byte)0x82, sent.FeatureIndex);
        Assert.Equal((byte)0xC1, sent.FunctionAndSwId);
        Assert.Equal((byte)0x03, sent.Parameters.Span[0]); // unpair sub-action
        Assert.Equal((byte)0x03, sent.Parameters.Span[1]); // slot index
    }

    [Fact]
    public async Task UnpairAsync_throws_on_error_reply()
    {
        var conn = new FakeReceiverConnection();
        using var receiver = new BoltReceiver(Info(), conn);
        receiver.EnsureSlot(2);

        using var responder = conn.RespondWith(written =>
        {
            if (written.IsLong && written.FeatureIndex == 0x82)
            {
                // HID++ 1.0 error reply: short report, sub-id 0x8F, then original sub-id, register, error code.
                var err = new byte[HidPpConstants.ShortReportLength];
                err[0] = 0x10;
                err[1] = 0xFF;
                err[2] = 0x8F;
                err[3] = 0x82;
                err[4] = 0xC1;
                err[5] = (byte)HidPpErrorCode.InvalidArgument;
                conn.Inject(HidPpFrame.TryParse(err)!.Value);
            }
        });

        var ex = await Assert.ThrowsAsync<HidPpException>(() => receiver.UnpairAsync(2));
        Assert.Equal(HidPpErrorCode.InvalidArgument, ex.ErrorCode);
    }

    [Fact]
    public async Task UnpairAsync_returns_false_on_timeout()
    {
        var conn = new FakeReceiverConnection(); // no echo
        using var receiver = new BoltReceiver(Info(), conn);
        receiver.EnsureSlot(1);

        var ok = await receiver.UnpairAsync(1, TimeSpan.FromMilliseconds(100));

        Assert.False(ok);
        // Slot still in cache since the unpair wasn't confirmed.
        Assert.NotNull(receiver.TryGetDevice(1));
    }

    [Fact]
    public async Task DiscoverFeaturesAsync_resolves_indices_via_IRoot()
    {
        var conn = new FakeReceiverConnection();

        // Auto-respond to IRoot getFeature(...) with a synthetic index based on the requested feature id.
        using var _ = conn.RespondWith(outFrame =>
        {
            // IRoot getFeature is short, deviceIndex=N, featureIndex=0x00 (IRootIndex), fn=0.
            if (!outFrame.IsShort) return;
            if (outFrame.FeatureIndex != 0x00) return;
            if (outFrame.Function != 0) return;

            var requestedId = (ushort)((outFrame.Parameters.Span[0] << 8) | outFrame.Parameters.Span[1]);
            byte index = requestedId switch
            {
                0x1B04 => 0x07,
                0x1814 => 0x09,
                0x1815 => 0x0B,
                _ => 0x00,
            };

            var reply = HidPpFrame.Short(
                deviceIndex: outFrame.DeviceIndex,
                featureIndex: outFrame.FeatureIndex,
                function: outFrame.Function,
                swId: outFrame.SwId,
                parameters: [index, 0x00, 0x00]);
            conn.Inject(reply);
        });

        using var receiver = new BoltReceiver(Info(), conn);

        await receiver.DiscoverFeaturesAsync(deviceIndex: 1);

        var slot = receiver.TryGetDevice(1)!;
        Assert.Equal((byte?)0x07, slot.ReprogControlsIndex);
        Assert.Equal((byte?)0x09, slot.ChangeHostIndex);
        Assert.Equal((byte?)0x0B, slot.HostsInfoIndex);
    }
}
