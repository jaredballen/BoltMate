using LogiPlusSwitcher.Core.Bolt;
using Xunit;

namespace LogiPlusSwitcher.Tests;

public class PairingBackupTests
{
    [Fact]
    public async Task Round_trips_through_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logiplus-backup-{Guid.NewGuid():N}.json");

        var snap = new PairingBackup
        {
            CapturedAt = new DateTimeOffset(2026, 6, 19, 7, 30, 0, TimeSpan.FromHours(-4)),
            Receivers =
            {
                new ReceiverBackup
                {
                    Serial = "CEB26A",
                    ProductString = "USB Receiver",
                    FirmwareVersion = "02.00.B1601",
                    Slots =
                    {
                        new SlotBackup
                        {
                            DeviceIndex = 3,
                            Wpid = 0xB034,
                            Name = "MX Master 3S",
                            Serial = "2235LZ53T6Q8",
                            BluetoothAddress = "F2:FD:55:EB:01:03",
                            CurrentHost = 0,
                        },
                    },
                },
            },
        };

        try
        {
            await PairingBackup.SaveAsync(snap, path);
            Assert.True(File.Exists(path));

            var loaded = await PairingBackup.LoadAsync(path);

            Assert.Equal(snap.CapturedAt, loaded.CapturedAt);
            Assert.Single(loaded.Receivers);
            var r = loaded.Receivers[0];
            Assert.Equal("CEB26A", r.Serial);
            Assert.Equal("02.00.B1601", r.FirmwareVersion);
            Assert.Single(r.Slots);
            Assert.Equal((byte)3, r.Slots[0].DeviceIndex);
            Assert.Equal((ushort)0xB034, r.Slots[0].Wpid);
            Assert.Equal("MX Master 3S", r.Slots[0].Name);
            Assert.Equal("F2:FD:55:EB:01:03", r.Slots[0].BluetoothAddress);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
