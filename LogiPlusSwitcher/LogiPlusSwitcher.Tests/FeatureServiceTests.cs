using LogiPlusSwitcher.Core.HidPp;
using LogiPlusSwitcher.Core.HidPp.Features;
using LogiPlusSwitcher.Tests.Support;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class FeatureServiceTests
{
    [Fact]
    public async Task BatteryService_decodes_charge_and_charging_status()
    {
        var conn = new FakeReceiverConnection();
        using var sub = conn.RespondWith(outFrame =>
        {
            // BatteryService.GetStatusAsync sends fn 0x1 on the battery feature index.
            if (outFrame.Function == 0x1)
            {
                var reply = HidPpFrame.Short(
                    outFrame.DeviceIndex, outFrame.FeatureIndex, outFrame.Function, outFrame.SwId,
                    parameters: [
                        65,    // 65% SoC
                        0x04,  // level mask (good)
                        0x01,  // status: charging
                    ]);
                conn.Inject(reply);
            }
        });

        using var client = new HidPpClient(conn);
        var battery = new BatteryService(client);

        var status = await battery.GetStatusAsync(deviceIndex: 1, featureIndex: 0x0B);

        Assert.NotNull(status);
        Assert.Equal((byte?)65, status!.Value.Percent);
        Assert.True(status.Value.Charging);
        Assert.False(status.Value.Full);
    }

    [Fact]
    public async Task BatteryService_decodes_unknown_percent_when_byte_is_FF()
    {
        var conn = new FakeReceiverConnection();
        using var sub = conn.RespondWith(outFrame =>
        {
            var reply = HidPpFrame.Short(
                outFrame.DeviceIndex, outFrame.FeatureIndex, outFrame.Function, outFrame.SwId,
                parameters: [0xFF, 0x00, 0x00]);
            conn.Inject(reply);
        });

        using var client = new HidPpClient(conn);
        var battery = new BatteryService(client);

        var status = await battery.GetStatusAsync(1, 0x0B);
        Assert.NotNull(status);
        Assert.Null(status!.Value.Percent);
    }

    [Fact]
    public async Task BatteryService_returns_null_on_hidpp_error()
    {
        var conn = new FakeReceiverConnection();
        using var sub = conn.RespondWith(outFrame =>
        {
            var error = HidPpFrame.Short(
                outFrame.DeviceIndex, featureIndex: 0x8F,
                function: outFrame.Function, swId: outFrame.SwId,
                parameters: [outFrame.FeatureIndex, (byte)HidPpErrorCode.Unsupported]);
            conn.Inject(error);
        });

        using var client = new HidPpClient(conn);
        var battery = new BatteryService(client);

        Assert.Null(await battery.GetStatusAsync(1, 0x0B));
    }

    [Fact]
    public async Task DeviceInfoService_decodes_serial()
    {
        var conn = new FakeReceiverConnection();
        using var sub = conn.RespondWith(outFrame =>
        {
            // fn 0x2 = getDeviceSerialNumber
            if (outFrame.Function == 0x2)
            {
                var serialBytes = System.Text.Encoding.ASCII.GetBytes("ABCDEF123456");
                var padded = new byte[3];
                serialBytes.AsSpan(0, 3).CopyTo(padded); // short report carries 3 bytes
                var reply = HidPpFrame.Short(outFrame.DeviceIndex, outFrame.FeatureIndex, outFrame.Function, outFrame.SwId, padded);
                conn.Inject(reply);
            }
        });

        using var client = new HidPpClient(conn);
        var info = new DeviceInfoService(client);
        var serial = await info.GetSerialAsync(1, 0x05);
        Assert.Equal("ABC", serial);
    }

    [Fact]
    public async Task DeviceNameService_reads_total_length()
    {
        var conn = new FakeReceiverConnection();
        using var sub = conn.RespondWith(outFrame =>
        {
            if (outFrame.Function == 0x0)
            {
                var reply = HidPpFrame.Short(
                    outFrame.DeviceIndex, outFrame.FeatureIndex, outFrame.Function, outFrame.SwId,
                    parameters: [12, 0, 0]);
                conn.Inject(reply);
            }
        });

        using var client = new HidPpClient(conn);
        var name = new DeviceNameService(client);
        Assert.Equal(12, await name.GetNameCountAsync(1, 0x05));
    }

    [Fact]
    public async Task DeviceFriendlyNameService_returns_empty_on_zero_length()
    {
        var conn = new FakeReceiverConnection();
        using var sub = conn.RespondWith(outFrame =>
        {
            if (outFrame.Function == 0x0)
            {
                var reply = HidPpFrame.Short(
                    outFrame.DeviceIndex, outFrame.FeatureIndex, outFrame.Function, outFrame.SwId,
                    parameters: [0, 0, 0]);
                conn.Inject(reply);
            }
        });

        using var client = new HidPpClient(conn);
        var friendly = new DeviceFriendlyNameService(client);
        Assert.Equal(string.Empty, await friendly.GetAsync(1, 0x07));
    }

    [Fact]
    public async Task DeviceFriendlyNameService_setName_chunks_payload_with_offset()
    {
        var conn = new FakeReceiverConnection();
        var writes = new List<HidPpFrame>();
        using var sub = conn.RespondWith(outFrame =>
        {
            if (outFrame.Function == 0x2)
            {
                writes.Add(outFrame);
                // Echo success
                var reply = HidPpFrame.Long(
                    outFrame.DeviceIndex, outFrame.FeatureIndex, outFrame.Function, outFrame.SwId,
                    parameters: outFrame.Parameters.Span);
                conn.Inject(reply);
            }
        });

        using var client = new HidPpClient(conn);
        var friendly = new DeviceFriendlyNameService(client);

        // 20-char name forces two chunks (15 + 5).
        await friendly.SetFriendlyNameAsync(1, 0x07, "AAAAAAAAAAAAAAA-bbbbb");

        Assert.Equal(2, writes.Count);
        Assert.Equal((byte)0, writes[0].Parameters.Span[0]);       // first chunk offset
        Assert.Equal((byte)15, writes[1].Parameters.Span[0]);      // second chunk offset
    }
}
