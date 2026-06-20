namespace BoltMate.Hid.Abstractions;

/// <summary>
/// HID++ wire constants — report IDs, special device indexes, and the
/// software identifier this app uses to tag its own requests.
/// </summary>
public static class HidPpConstants
{
    /// <summary>Short HID++ report (7 bytes incl. report id).</summary>
    public const byte ReportIdShort = 0x10;

    /// <summary>Long HID++ report (20 bytes incl. report id).</summary>
    public const byte ReportIdLong = 0x11;

    /// <summary>HID++ 1.0 DJ-mode notification report (15 bytes incl. report id).</summary>
    public const byte ReportIdDj = 0x20;

    /// <summary>Total wire length of a short HID++ report.</summary>
    public const int ShortReportLength = 7;

    /// <summary>Total wire length of a long HID++ report.</summary>
    public const int LongReportLength = 20;

    /// <summary>Total wire length of a DJ-mode report.</summary>
    public const int DjReportLength = 15;

    /// <summary>Number of parameter bytes carried by a short report.</summary>
    public const int ShortParameterLength = 3;

    /// <summary>Number of parameter bytes carried by a long report.</summary>
    public const int LongParameterLength = 16;

    /// <summary>Device index that addresses the receiver itself.</summary>
    public const byte DeviceIndexReceiver = 0xFF;

    /// <summary>Lowest device index assigned to paired devices.</summary>
    public const byte DeviceIndexFirstSlot = 0x01;

    /// <summary>Highest device index assigned to paired devices on Bolt (6 slots).</summary>
    public const byte DeviceIndexLastSlot = 0x06;

    /// <summary>
    /// Software identifier this app stamps into outgoing requests so we can filter
    /// our own write-echo notifications out of the inbound stream. 1..15 are valid;
    /// avoid 1 (Logi Options+ commonly uses it) and 4 (Solaar default).
    /// </summary>
    public const byte OurSwId = 0x0E;
}
