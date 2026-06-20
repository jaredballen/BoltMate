namespace BoltMate.Core.HidPp;

/// <summary>
/// HID++ 2.0 error codes (incomplete list — extended per Solaar's
/// <c>hidpp20_constants.ERROR</c> enum). The receiver returns these in the
/// payload of a short error reply (feature_index == 0x8F).
/// </summary>
public enum HidPpErrorCode : byte
{
    None = 0x00,
    Unknown = 0x01,
    InvalidArgument = 0x02,
    OutOfRange = 0x03,
    HardwareError = 0x04,
    LogitechInternal = 0x05,
    InvalidFeatureIndex = 0x06,
    InvalidFunctionId = 0x07,
    Busy = 0x08,
    Unsupported = 0x09,
}

/// <summary>
/// Thrown when a HID++ 2.0 request returns an error reply
/// (feature_index 0x8F) or no reply at all within the timeout window.
/// </summary>
public sealed class HidPpException : Exception
{
    public byte DeviceIndex { get; }
    public byte FeatureIndex { get; }
    public int Function { get; }
    public HidPpErrorCode ErrorCode { get; }

    public HidPpException(byte deviceIndex, byte featureIndex, int function, HidPpErrorCode errorCode)
        : base($"HID++ device 0x{deviceIndex:X2} feature 0x{featureIndex:X2} fn={function} returned {errorCode} (0x{(byte)errorCode:X2})")
    {
        DeviceIndex = deviceIndex;
        FeatureIndex = featureIndex;
        Function = function;
        ErrorCode = errorCode;
    }

    public HidPpException(byte deviceIndex, byte featureIndex, int function, string message)
        : base(message)
    {
        DeviceIndex = deviceIndex;
        FeatureIndex = featureIndex;
        Function = function;
        ErrorCode = HidPpErrorCode.Unknown;
    }
}
