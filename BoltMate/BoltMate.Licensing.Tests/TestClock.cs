using System;
using BoltMate.Licensing;

namespace BoltMate.Licensing.Tests;

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
}
