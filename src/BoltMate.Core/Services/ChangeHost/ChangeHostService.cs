using BoltMate.Core.HidPp;

namespace BoltMate.Core.Services;

/// <summary>
/// HID++ 2.0 CHANGE_HOST (feature 0x1814).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description><c>getHostInfo</c> (fn 0x00) — returns <c>(numHosts, currentHost)</c>.</description></item>
/// <item><description><c>setCurrentHost</c> (fn 0x10) — fire-and-forget; the device disconnects and switches without replying.</description></item>
/// </list>
/// Verified write-only by Solaar (<c>settings_templates.ChangeHost</c> uses <c>no_reply=True</c>).
/// </remarks>
public sealed class ChangeHostService(IHidPpClient client) : IChangeHostService
{
    private readonly IHidPpClient _client = client;

    /// <summary>
    /// Reads the current host of <paramref name="deviceIndex"/>.
    /// </summary>
    public async Task<HostInfo> GetHostInfoAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var reply = await _client.RequestAsync(
            deviceIndex: deviceIndex,
            featureIndex: featureIndex,
            function: 0x00,
            useLongReport: false,
            cancellationToken: ct).ConfigureAwait(false);

        var span = reply.Parameters.Span;
        return new HostInfo(NumberOfHosts: span[0], CurrentHost: span[1]);
    }

    /// <summary>
    /// Sends a fire-and-forget host switch command. The device will disconnect
    /// from this host immediately — there is no reply.
    /// </summary>
    /// <param name="deviceIndex">Receiver slot of the paired device.</param>
    /// <param name="featureIndex">Per-device index resolved via <see cref="RootService"/>.</param>
    /// <param name="targetHost">Zero-indexed host (0..numHosts-1).</param>
    public void SetCurrentHost(byte deviceIndex, byte featureIndex, byte targetHost)
    {
        // fn 0x1 setCurrentHost — write-only. Solaar wire: long report,
        // params = [targetHost, 0, 0]. The function nibble is 1, not 0x10 —
        // function ids in HID++ 2.0 are 4-bit; "0x10" in some docs refers to
        // the packed function|sw_id byte for fn=1, swid=0.
        ReadOnlySpan<byte> parameters = stackalloc byte[] { targetHost, 0x00, 0x00 };
        _client.SendLongOneWay(deviceIndex, featureIndex, function: 0x1, parameters);
    }
}

/// <summary>
/// Result of <see cref="ChangeHostService.GetHostInfoAsync"/>.
/// </summary>
public readonly record struct HostInfo(byte NumberOfHosts, byte CurrentHost);
