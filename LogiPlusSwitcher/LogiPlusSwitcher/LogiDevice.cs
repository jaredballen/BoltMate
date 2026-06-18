using HidSharp;
using HidSharp.Reports.Input;

namespace LogiPlusSwitcher;

public enum LogiDeviceType : uint
{
    Unknown = 0xFFFFFFFF,
    ManagementInterface = 0xFFFFFFFE,
    BoltReceiver = 0xFF000001,
    Keyboard = 0x00010006,
    Mouse = 0x00010002,
}

public interface ILogiDevice : IDisposable
{
    //LogiDeviceType Type { get; }
    string Manufacturer { get; }
    string Product { get; }
    string Name { get; }
    List<LogiDeviceType> GetSupportedLogiDeviceTypes();
    
    // TODO: Remove this property in the future.
    HidDevice Device { get; }

    void Listen();
}

public class LogiDevice(HidDevice device) : ILogiDevice
{
    private HidDeviceInputReceiver? _inputReceiver;
    public string Manufacturer => device.GetManufacturer();
    
    public string Product => device.GetProductName();
    
    public string Name => device.GetFriendlyName();
    
    public HidDevice Device => device;
    
    public void Listen()
    {
        Console.WriteLine($"Listening to {device.GetHashCode()}");
        
        Task.Run(async () =>
        {
            try
            {
                var stream = device.Open();

                var reportDescriptor = device.GetReportDescriptor();
                _inputReceiver = reportDescriptor.CreateHidDeviceInputReceiver();
                var inputReportBuffer = new byte[device.GetMaxInputReportLength()];

                _inputReceiver.Received += (_, _) =>
                {
                    while (_inputReceiver.TryRead(inputReportBuffer, 0, out var report))
                    {
                        var channel = -1;
                        var data = inputReportBuffer.AsSpan(1, 3);
////                        if (data.SequenceEqual<byte>([0x09, 0x00, 0x59]))
////                            channel = 0;
////                        else if (data.SequenceEqual<byte>([0x09, 0x00, 0x5A]))
////                            channel = 1;
////                        else if (data.SequenceEqual<byte>([0x09, 0x00, 0x5B]))
////                            channel = 2;
////                        else
////                            channel = -1;
////
////                        if (channel < 0) continue;

                        Console.WriteLine($"{BitConverter.ToString(inputReportBuffer)}");
                    }
                };

                _inputReceiver.Start(stream);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading data: {ex}");
            }
        });
    }

    public List<LogiDeviceType> GetSupportedLogiDeviceTypes()
    {
        try
        {
            var descriptor = device.GetReportDescriptor();
            if (descriptor?.DeviceItems == null)
                return [LogiDeviceType.Unknown];

            var types = descriptor.DeviceItems
                .SelectMany(item => item.Usages?.GetAllValues() ?? [])
                .Select(usage => usage switch
                {
                    (uint)LogiDeviceType.Keyboard => LogiDeviceType.Keyboard,
                    (uint)LogiDeviceType.Mouse => LogiDeviceType.Mouse,
                    (uint)LogiDeviceType.BoltReceiver => LogiDeviceType.BoltReceiver,
                    _ => LogiDeviceType.Unknown
                })
                .Where(t => t != LogiDeviceType.Unknown)
                .Distinct()
                .ToList();

            if (types.Count == 0)
                types.Add(LogiDeviceType.Unknown);
            
            return types;
        }
        catch (Exception e)
        {
            return [ LogiDeviceType.ManagementInterface ];
        }
    }

    public override int GetHashCode() => device.GetHashCode();
    
    public void Dispose()
    {
        Console.WriteLine($"STOP listening to {device.GetHashCode()}");
        _inputReceiver.Stream.Dispose();
    }
}