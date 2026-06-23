using BoltMate.Core.Services;
using BoltMate.Core.HidPp;
using BoltMate.Tests.Support;
using Xunit;

namespace BoltMate.Tests;

public class HidPpClientTests
{
    [Fact]
    public async Task Request_matches_reply_by_device_and_fn_swid()
    {
        var conn = new FakeReceiverConnection();
        // Auto-reply: when the client writes a request, echo back a "reply"
        // with the same device, feature, fn|swid byte and a recognisable payload.
        using var _ = conn.RespondWith(outFrame =>
        {
            var reply = HidPpFrame.Short(
                outFrame.DeviceIndex, outFrame.FeatureIndex,
                outFrame.Function, outFrame.SwId,
                parameters: [0xAB, 0xCD]);
            conn.Inject(reply);
        });

        using var client = new HidPpClient(conn);
        var reply = await client.RequestAsync(deviceIndex: 0x01, featureIndex: 0x05, function: 0x2);

        Assert.Equal(0x01, reply.DeviceIndex);
        Assert.Equal(0x05, reply.FeatureIndex);
        Assert.Equal(0xAB, reply.Parameters.Span[0]);
        Assert.Equal(0xCD, reply.Parameters.Span[1]);
    }

    [Fact]
    public async Task Error_reply_throws_HidPpException_with_decoded_code()
    {
        var conn = new FakeReceiverConnection();
        using var _ = conn.RespondWith(outFrame =>
        {
            // HID++ 2.0 error: feature_index 0x8F, function|swid echoed, payload [origFeatureIdx, errorCode, ...].
            var error = HidPpFrame.Short(
                deviceIndex: outFrame.DeviceIndex,
                featureIndex: 0x8F,
                function: outFrame.Function,
                swId: outFrame.SwId,
                parameters: [outFrame.FeatureIndex, (byte)HidPpErrorCode.Unsupported]);
            conn.Inject(error);
        });

        using var client = new HidPpClient(conn);

        var ex = await Assert.ThrowsAsync<HidPpException>(() =>
            client.RequestAsync(deviceIndex: 0x01, featureIndex: 0x05, function: 0x1));

        Assert.Equal(HidPpErrorCode.Unsupported, ex.ErrorCode);
        Assert.Equal((byte)0x01, ex.DeviceIndex);
        Assert.Equal((byte)0x05, ex.FeatureIndex);
    }

    [Fact]
    public async Task Timeout_throws_HidPpException()
    {
        var conn = new FakeReceiverConnection(); // no auto-reply
        using var client = new HidPpClient(conn);

        var ex = await Assert.ThrowsAsync<HidPpException>(() =>
            client.RequestAsync(
                deviceIndex: 0x01, featureIndex: 0x05, function: 0x0,
                timeout: TimeSpan.FromMilliseconds(50)));

        Assert.Contains("No reply", ex.Message);
    }

    [Fact]
    public void Frame_with_foreign_sw_id_is_surfaced_as_notification()
    {
        var conn = new FakeReceiverConnection();
        using var client = new HidPpClient(conn);

        HidPpFrame? observed = null;
        using var sub = client.Notifications.Subscribe(frame => observed = frame);

        // Logi Options+ style: sw_id = 1 (not ours which is 0x0E)
        var foreign = HidPpFrame.Long(deviceIndex: 0x02, featureIndex: 0x09, function: 0x1, swId: 0x01);
        conn.Inject(foreign);

        Assert.NotNull(observed);
        Assert.Equal((byte)0x02, observed.Value.DeviceIndex);
        Assert.Equal(0x01, observed.Value.SwId);
    }

    [Fact]
    public async Task Frames_with_our_sw_id_but_no_pending_request_are_surfaced_as_notifications()
    {
        var conn = new FakeReceiverConnection();
        using var client = new HidPpClient(conn);

        HidPpFrame? observed = null;
        using var sub = client.Notifications.Subscribe(frame => observed = frame);

        // Stale reply (our sw_id) with no pending request — should NOT silently
        // get dropped; route to notifications so it's at least observable.
        var orphan = HidPpFrame.Short(deviceIndex: 0x03, featureIndex: 0x07, function: 0x4, swId: client.SwId);
        conn.Inject(orphan);

        // Give the event handler a tick.
        await Task.Yield();
        Assert.NotNull(observed);
    }
}
