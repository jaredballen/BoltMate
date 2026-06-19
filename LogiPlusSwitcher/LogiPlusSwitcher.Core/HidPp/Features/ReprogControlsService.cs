using System.Buffers.Binary;

namespace LogiPlusSwitcher.Core.HidPp.Features;

/// <summary>
/// HID++ 2.0 REPROG_CONTROLS_V4 (feature 0x1B04) — enumerate and reconfigure
/// reprogrammable controls (Easy-Switch buttons, gesture buttons, etc.).
/// </summary>
public sealed class ReprogControlsService
{
    private readonly HidPpClient _client;

    public ReprogControlsService(HidPpClient client)
    {
        _client = client;
    }

    /// <summary>
    /// Returns the number of reprogrammable controls exposed by the device.
    /// </summary>
    public async Task<int> GetControlCountAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var reply = await _client.RequestAsync(
            deviceIndex: deviceIndex,
            featureIndex: featureIndex,
            function: 0x00,
            useLongReport: false,
            cancellationToken: ct).ConfigureAwait(false);

        return reply.Parameters.Span[0];
    }

    /// <summary>
    /// Reads metadata for a single control by table index (0..count-1).
    /// </summary>
    public async Task<ControlInfo> GetCidInfoAsync(byte deviceIndex, byte featureIndex, byte controlIndex, CancellationToken ct = default)
    {
        ReadOnlyMemory<byte> request = new byte[] { controlIndex };
        var reply = await _client.RequestAsync(
            deviceIndex: deviceIndex,
            featureIndex: featureIndex,
            function: 0x1,
            parameters: request,
            useLongReport: false,
            cancellationToken: ct).ConfigureAwait(false);

        // Solaar layout for getCidInfo reply (long report payload, but first
        // bytes carry the data on short replies too):
        //   [cid_msb, cid_lsb, task_msb, task_lsb, flags, pos, group, groupMask, addFlags]
        var p = reply.Parameters.Span;
        return new ControlInfo(
            ControlIndex: controlIndex,
            ControlId: BinaryPrimitives.ReadUInt16BigEndian(p.Slice(0, 2)),
            TaskId: BinaryPrimitives.ReadUInt16BigEndian(p.Slice(2, 2)),
            Flags: (ControlFlags)p[4],
            Position: p.Length > 5 ? p[5] : (byte)0,
            Group: p.Length > 6 ? p[6] : (byte)0,
            GroupMask: p.Length > 7 ? p[7] : (byte)0,
            AdditionalFlags: p.Length > 8 ? p[8] : (byte)0);
    }

    /// <summary>
    /// Enumerates every reprogrammable control on the device.
    /// </summary>
    public async Task<IReadOnlyList<ControlInfo>> ListControlsAsync(byte deviceIndex, byte featureIndex, CancellationToken ct = default)
    {
        var count = await GetControlCountAsync(deviceIndex, featureIndex, ct).ConfigureAwait(false);
        var result = new ControlInfo[count];
        for (byte i = 0; i < count; i++)
            result[i] = await GetCidInfoAsync(deviceIndex, featureIndex, i, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Sets the divert / persistent-divert flags for a single CID. When divert
    /// is set, button presses fire <c>divertedButtonsEvent</c> instead of
    /// triggering the device's internal action — this is the mechanism by
    /// which we observe Easy-Switch presses BEFORE the host change executes.
    /// </summary>
    /// <param name="bfield">Bitfield per Solaar: 0x01 divertValid + 0x02 divertSet
    /// (= temporary divert, recommended); 0x04 + 0x08 for persistent divert
    /// that survives device power-cycle; 0x10 + 0x20 for raw-XY divert.</param>
    public Task SetCidReportingAsync(byte deviceIndex, byte featureIndex, ushort controlId, byte bfield, CancellationToken ct = default)
    {
        Span<byte> request = stackalloc byte[5];
        BinaryPrimitives.WriteUInt16BigEndian(request, controlId);
        request[2] = bfield;
        // bytes 3-4: remap CID (0 = no remap)
        request[3] = 0;
        request[4] = 0;

        return _client.RequestAsync(
            deviceIndex: deviceIndex,
            featureIndex: featureIndex,
            function: 0x3,
            parameters: request.ToArray(),
            useLongReport: true,
            cancellationToken: ct);
    }

    /// <summary>
    /// Common bitfield combinations for <see cref="SetCidReportingAsync"/>.
    /// </summary>
    public static class DivertModes
    {
        /// <summary>Divert valid + divert clear: device handles control normally.</summary>
        public const byte Normal = 0x01;

        /// <summary>Divert valid + divert set: device emits divertedButtonsEvent instead of acting.</summary>
        public const byte Diverted = 0x03;

        /// <summary>Persistent divert valid + set: divert survives device power cycle.</summary>
        public const byte DivertedPersistent = 0x0F;
    }
}

/// <summary>
/// Single entry in a device's reprogrammable-control table.
/// </summary>
/// <param name="ControlIndex">Zero-based index in the device's table.</param>
/// <param name="ControlId">Logitech-assigned CID (e.g. 0x00D1 for Host_Switch_Channel_1).</param>
/// <param name="TaskId">Default task the control performs when not diverted.</param>
/// <param name="Flags">Capability flags — see <see cref="ControlFlags"/>.</param>
public sealed record ControlInfo(
    byte ControlIndex,
    ushort ControlId,
    ushort TaskId,
    ControlFlags Flags,
    byte Position,
    byte Group,
    byte GroupMask,
    byte AdditionalFlags)
{
    /// <summary>True if the control can be diverted (temporarily).</summary>
    public bool IsDivertable => (Flags & ControlFlags.Divertable) != 0;

    /// <summary>True if the control supports persistent divert (survives power cycle).</summary>
    public bool IsPersistentlyDivertable => (Flags & ControlFlags.PersistentlyDivertable) != 0;

    /// <summary>True if this control is one of the three Easy-Switch buttons.</summary>
    public bool IsHostSwitch => EasySwitchCids.IsHostSwitch(ControlId);
}

[Flags]
public enum ControlFlags : byte
{
    None = 0x00,
    MouseButton = 0x01,
    FunctionKey = 0x02,
    Hotkey = 0x04,
    FnTogglable = 0x08,
    Reprogrammable = 0x10,
    Divertable = 0x20,
    PersistentlyDivertable = 0x40,
    Virtual = 0x80,
}
