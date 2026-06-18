using System;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using DynamicData;
using HidSharp;

namespace LogiPlusSwitcher;

public interface ILogiBoltService
{
    void Connect();
    IReadOnlySet<ILogiDevice> Devices { get; }
    IObservable<ILogiDevice> DeviceAdded { get; }
    IObservable<ILogiDevice> DeviceRemoved { get; }
}

public class LogiBoltService : ILogiBoltService
{
    // Logitech VID and Bolt Receiver PID
    private const int LogitechVid = 0x046D;
    private const int BoltReceiverPid = 0xC548;

    private readonly HashSet<ILogiDevice> _logiDevices = new();
    public IReadOnlySet<ILogiDevice> Devices => _logiDevices;

    private readonly Subject<ILogiDevice> _deviceAdded = new();
    private readonly Subject<ILogiDevice> _deviceRemoved = new();
    public IObservable<ILogiDevice> DeviceAdded => _deviceAdded;
    public IObservable<ILogiDevice> DeviceRemoved => _deviceRemoved;

    private IDisposable? _changedSubscription;

    public void Connect()
    {
        // Wrap the Changed event as an observable and throttle it
        var changedObservable = Observable
            .FromEventPattern<EventHandler, EventArgs>(
                h => DeviceList.Local.Changed += h,
                h => DeviceList.Local.Changed -= h);

        _changedSubscription = changedObservable
            .Throttle(TimeSpan.FromMilliseconds(100))
            .Subscribe(_ => UpdateDevices());

        UpdateDevices();
    }

    private void UpdateDevices()
    {
        try
        {
            var logiDevicesToAdd = DeviceList.Local.GetHidDevices()
                .Where(hid => hid.VendorID == LogitechVid && hid.ProductID == BoltReceiverPid)
                .Select(hid => new LogiDevice(hid))
                .Where(logiDevice => _logiDevices.Contains(logiDevice) is false);

            foreach (var logiDevice in logiDevicesToAdd)
            {
                _logiDevices.Add(logiDevice);
                _deviceAdded.OnNext(logiDevice);
            }

            ///// var logiDevicesToRemove = _logiDevices.Where(kvp => DeviceList.Local.GetAllDevices(hid => hid.key != kvp.Key))
            /////     .ToList();
            ///// 
            ///// foreach (var kvp in logiDevicesToRemove)
            ///// {
            /////     _logiDevices.Remove(kvp.Key);
            /////     _deviceRemoved.OnNext(kvp.Value);
            /////     kvp.Value.Dispose();
            ///// }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
}