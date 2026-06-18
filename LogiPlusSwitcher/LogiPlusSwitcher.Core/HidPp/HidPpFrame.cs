using System.Buffers.Binary;

namespace LogiPlusSwitcher.Core.HidPp;

/// <summary>
/// A single HID++ wire frame (short, long, or DJ). Immutable.
/// </summary>
/// <remarks>
/// Layout (HID++ 2.0): <c>[ReportId][DeviceIndex][FeatureIndex][Function|SwId][Parameters...]</c>.
/// For HID++ 1.0 messages the <see cref="FeatureIndex"/> position carries the sub-id and
/// <see cref="FunctionAndSwId"/> carries the first parameter byte; callers must interpret
/// accordingly using <see cref="ReportId"/> and context.
/// </remarks>
public readonly struct HidPpFrame
{
    /// <summary>Wire report id (0x10, 0x11, or 0x20).</summary>
    public byte ReportId { get; }

    /// <summary>Target/source device slot (0xFF receiver, 1..6 paired device).</summary>
    public byte DeviceIndex { get; }

    /// <summary>HID++ 2.0 feature table index or HID++ 1.0 sub-id.</summary>
    public byte FeatureIndex { get; }

    /// <summary>Packed function (high nibble) and software id (low nibble).</summary>
    public byte FunctionAndSwId { get; }

    /// <summary>Parameter payload (length depends on report id).</summary>
    public ReadOnlyMemory<byte> Parameters { get; }

    /// <summary>HID++ 2.0 function number (0..15).</summary>
    public int Function => (FunctionAndSwId >> 4) & 0x0F;

    /// <summary>HID++ 2.0 software identifier (0..15).</summary>
    public int SwId => FunctionAndSwId & 0x0F;

    public bool IsShort => ReportId == HidPpConstants.ReportIdShort;
    public bool IsLong => ReportId == HidPpConstants.ReportIdLong;
    public bool IsDj => ReportId == HidPpConstants.ReportIdDj;

    private HidPpFrame(byte reportId, byte deviceIndex, byte featureIndex, byte functionAndSwId, ReadOnlyMemory<byte> parameters)
    {
        ReportId = reportId;
        DeviceIndex = deviceIndex;
        FeatureIndex = featureIndex;
        FunctionAndSwId = functionAndSwId;
        Parameters = parameters;
    }

    /// <summary>
    /// Builds a short (7-byte) HID++ 2.0 request.
    /// </summary>
    public static HidPpFrame Short(byte deviceIndex, byte featureIndex, int function, int swId, ReadOnlySpan<byte> parameters = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(function);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(function, 0x0F);
        ArgumentOutOfRangeException.ThrowIfNegative(swId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(swId, 0x0F);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.Length, HidPpConstants.ShortParameterLength);

        var padded = new byte[HidPpConstants.ShortParameterLength];
        parameters.CopyTo(padded);
        return new HidPpFrame(HidPpConstants.ReportIdShort, deviceIndex, featureIndex,
            (byte)((function << 4) | (swId & 0x0F)), padded);
    }

    /// <summary>
    /// Builds a long (20-byte) HID++ 2.0 request.
    /// </summary>
    public static HidPpFrame Long(byte deviceIndex, byte featureIndex, int function, int swId, ReadOnlySpan<byte> parameters = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(function);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(function, 0x0F);
        ArgumentOutOfRangeException.ThrowIfNegative(swId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(swId, 0x0F);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.Length, HidPpConstants.LongParameterLength);

        var padded = new byte[HidPpConstants.LongParameterLength];
        parameters.CopyTo(padded);
        return new HidPpFrame(HidPpConstants.ReportIdLong, deviceIndex, featureIndex,
            (byte)((function << 4) | (swId & 0x0F)), padded);
    }

    /// <summary>
    /// Builds a HID++ 1.0 short request (e.g. receiver register read/write). The
    /// <paramref name="subId"/> goes in the FeatureIndex slot and parameters fill
    /// the remaining 4 bytes (FunctionAndSwId + 3 payload bytes).
    /// </summary>
    public static HidPpFrame Hidpp10Short(byte deviceIndex, byte subId, ReadOnlySpan<byte> parameters = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(parameters.Length, 4);
        Span<byte> wire = stackalloc byte[5];
        wire[0] = subId;
        parameters.CopyTo(wire[1..]);

        var padded = new byte[HidPpConstants.ShortParameterLength];
        wire[2..].CopyTo(padded);
        return new HidPpFrame(HidPpConstants.ReportIdShort, deviceIndex, subId, wire[1], padded);
    }

    /// <summary>
    /// Serialises the frame to its wire form (7, 20, or 15 bytes including the report id).
    /// </summary>
    public byte[] ToBytes()
    {
        var totalLength = ReportId switch
        {
            HidPpConstants.ReportIdShort => HidPpConstants.ShortReportLength,
            HidPpConstants.ReportIdLong => HidPpConstants.LongReportLength,
            HidPpConstants.ReportIdDj => HidPpConstants.DjReportLength,
            _ => throw new InvalidOperationException($"Unknown report id 0x{ReportId:X2}")
        };

        var buffer = new byte[totalLength];
        buffer[0] = ReportId;
        buffer[1] = DeviceIndex;
        buffer[2] = FeatureIndex;
        buffer[3] = FunctionAndSwId;
        Parameters.Span.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    /// <summary>
    /// Parses an incoming wire report. Pads or truncates parameters to the
    /// expected length for the report id; returns null if the report id is unknown
    /// or the buffer is too small.
    /// </summary>
    public static HidPpFrame? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        var reportId = data[0];
        var (expectedLength, paramLength) = reportId switch
        {
            HidPpConstants.ReportIdShort => (HidPpConstants.ShortReportLength, HidPpConstants.ShortParameterLength),
            HidPpConstants.ReportIdLong => (HidPpConstants.LongReportLength, HidPpConstants.LongParameterLength),
            HidPpConstants.ReportIdDj => (HidPpConstants.DjReportLength, HidPpConstants.DjReportLength - 4),
            _ => (0, 0)
        };

        if (expectedLength == 0 || data.Length < 4)
            return null;

        var parameters = new byte[paramLength];
        var available = Math.Min(paramLength, data.Length - 4);
        data.Slice(4, available).CopyTo(parameters);

        return new HidPpFrame(reportId, data[1], data[2], data[3], parameters);
    }

    /// <summary>
    /// True if the frame is an error reply (feature_index 0x8F for HID++ 2.0
    /// reply, or sub-id 0x8F for HID++ 1.0 error reply on short reports).
    /// </summary>
    public bool IsErrorReply => FeatureIndex == 0x8F;

    /// <summary>
    /// Reads the first <paramref name="count"/> parameter bytes into the provided span.
    /// </summary>
    public ReadOnlySpan<byte> ParameterSlice(int offset, int count) =>
        Parameters.Span.Slice(offset, count);

    /// <summary>
    /// Reads two parameter bytes at <paramref name="offset"/> as a big-endian ushort
    /// (HID++ uses big-endian for multi-byte values such as CIDs and feature IDs).
    /// </summary>
    public ushort ReadUInt16BigEndian(int offset) =>
        BinaryPrimitives.ReadUInt16BigEndian(Parameters.Span.Slice(offset, 2));

    public override string ToString() =>
        $"[0x{ReportId:X2}] dev=0x{DeviceIndex:X2} feat=0x{FeatureIndex:X2} fn={Function} swid={SwId} params={Convert.ToHexString(Parameters.Span)}";
}
