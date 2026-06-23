using System;

namespace BoltMate.App.Services;

/// <summary>
/// Background metadata enricher. Watches every receiver attached via
/// <see cref="IReceiverManager"/> and runs feature discovery, device-name
/// reads, host-binding reads, and battery polls on link-up. Has no
/// public surface beyond lifecycle — exists for DI registration and
/// shutdown ordering.
/// </summary>
public interface IDeviceEnricher : IDisposable
{
}
