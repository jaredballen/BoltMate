
using System.Reactive.Linq;
using DynamicData;
using LogiPlusSwitcher;

var logiBoldService = new LogiBoltService();
var added = logiBoldService.DeviceAdded
    .Subscribe(device =>
    {
        Console.WriteLine($"ADDED: {device.Manufacturer} {device.Product} {device.Name} - {device.GetHashCode()}");
        foreach (var type in device.GetSupportedLogiDeviceTypes())
        {
            Console.WriteLine($"    {type}");
        }
    });
    
var removed = logiBoldService.DeviceRemoved
    .Subscribe(device =>
    {
        Console.WriteLine($"REMOVED: {device.Manufacturer} {device.Product} {device.Name} - {device.GetHashCode()}");
        foreach (var type in device.GetSupportedLogiDeviceTypes())
        {
            Console.WriteLine($"    {type}");
        }
    });

logiBoldService.Connect();

Console.ReadLine();
