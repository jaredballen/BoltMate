using BoltMate.Hid.Abstractions;

namespace BoltMate.Tests.Support;

/// <summary>
/// In-memory <see cref="IReceiverTransport"/> for tests. Tests control which
/// receivers <see cref="Enumerate"/> returns and observe what <see cref="Open"/>
/// produces. Each call to Open returns a fresh <see cref="FakeReceiverConnection"/>
/// stored on the corresponding <see cref="FakeBoltSession"/>.
/// </summary>
public sealed class FakeReceiverTransport : IReceiverTransport
{
    public List<FakeBoltSession> Sessions { get; } = new();

    public IReadOnlyList<BoltReceiverInfo> Enumerate() =>
        Sessions.Select(s => s.Info).ToList();

    public IReceiverConnection Open(BoltReceiverInfo info)
    {
        var session = Sessions.First(s => s.Info.Path == info.Path);
        var connection = new FakeReceiverConnection();
        session.Connections.Add(connection);
        return connection;
    }

    public FakeBoltSession AddReceiver(string path = "/test/bolt-0", string serial = "SER-0")
    {
        var info = new BoltReceiverInfo(path, serial, "Logitech Bolt Receiver", "Logitech", 0x0001);
        var session = new FakeBoltSession(info);
        Sessions.Add(session);
        return session;
    }

    public void RemoveReceiver(string path)
    {
        Sessions.RemoveAll(s => s.Info.Path == path);
    }
}

public sealed class FakeBoltSession
{
    public BoltReceiverInfo Info { get; }
    public List<FakeReceiverConnection> Connections { get; } = new();

    public FakeBoltSession(BoltReceiverInfo info)
    {
        Info = info;
    }

    /// <summary>Latest connection opened against this receiver (test convenience).</summary>
    public FakeReceiverConnection LastConnection => Connections[^1];
}
