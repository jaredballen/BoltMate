using System;
using LogiPlusSwitcher.Licensing;

namespace LogiPlusSwitcher.Licensing.Tests;

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
}
