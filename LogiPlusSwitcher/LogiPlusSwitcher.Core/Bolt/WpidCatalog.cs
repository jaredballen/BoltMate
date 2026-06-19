namespace LogiPlusSwitcher.Core.Bolt;

/// <summary>
/// Wireless Product Id → human model name lookup. Lets us show a meaningful
/// device label even before the device wakes up enough to answer feature
/// 0x0005 DEVICE_NAME, which is most of why the table exists.
/// </summary>
/// <remarks>
/// The table is conservative — covers Logitech's Bolt lineup as of 2026 plus
/// the most common Unifying overlap. Add entries here as new devices surface;
/// Solaar's <c>descriptors.py</c> is the canonical source.
/// </remarks>
public static class WpidCatalog
{
    private static readonly Dictionary<ushort, string> Catalog = new()
    {
        // Pointing devices (Bolt)
        { 0xB02A, "MX Anywhere 3" },
        { 0xB378, "MX Anywhere 3S" },
        { 0xB02B, "MX Vertical" },
        { 0xB033, "MX Master 3" },
        { 0xB034, "MX Master 3S" },
        { 0xB037, "MX Master 3 for Mac" },
        { 0xB37D, "Lift" },
        { 0xB37E, "Lift for Business" },
        { 0xB025, "M650" },
        { 0xB026, "M650 L" },
        { 0xB02C, "Signature M650" },

        // Keyboards (Bolt)
        { 0xB35B, "MX Keys Mini" },
        { 0xB35E, "MX Keys S" },
        { 0xB35F, "MX Keys" },
        { 0xB360, "MX Keys Mini for Business" },
        { 0xB361, "MX Keys for Business" },
        { 0xB362, "MX Keys S for Business" },
        { 0xB364, "Signature K855" },
        { 0xB365, "MX Mechanical" },
        { 0xB366, "MX Mechanical Mini" },

        // Headsets / Speakers
        { 0xB3A0, "Zone Vibe 100" },
        { 0xB3A1, "Zone Vibe 125" },
        { 0xB3A4, "Zone 950" },

        // Webcams / capture (Bolt over USB combo not all)
        { 0xB37F, "MX Brio 705 for Business" },
    };

    /// <summary>Returns a model name for the WPID, or null if unknown.</summary>
    public static string? Lookup(ushort wpid) =>
        Catalog.TryGetValue(wpid, out var name) ? name : null;

    /// <summary>Returns the model name, or a generic "Logi 0xXXXX" if unknown.</summary>
    public static string LookupOrFallback(ushort wpid) =>
        Lookup(wpid) ?? $"Logi 0x{wpid:X4}";
}
