using System.Text.Json.Serialization;
using BoltMate.Core.Topology;

namespace BoltMate.Core.Topology.Messages;

/// <summary>
/// Discriminated envelope used on the encrypted mDNS+TCP backchannel.
/// Exactly one of the payload fields is populated; the rest stay
/// <c>null</c> and serialize-out.
/// </summary>
/// <remarks>
/// Trust boundary: every TCP frame is wrapped by
/// <see cref="IPeerCryptoProvider"/> before transmit, so successful
/// receipt + decode is itself proof the sender holds the account's
/// SyncKey. Receivers MUST NOT route on any field outside this
/// envelope until decrypt succeeds.
/// </remarks>
public sealed class TcpFrame
{
    public TcpFrameKind Kind { get; set; }

    public ReceiverAnnouncement? Announcement { get; set; }

    public LogBundleRequest? LogBundleRequest { get; set; }

    public LogBundleResponse? LogBundleResponse { get; set; }
}

public enum TcpFrameKind
{
    Unknown = 0,
    Announce = 1,
    LogBundleRequest = 2,
    LogBundleResponse = 3,
}

/// <summary>
/// Asks the recipient peer to produce its local log bundle and reply
/// with a <see cref="LogBundleResponse"/>.
/// </summary>
/// <remarks>
/// No bearer-token / sub-claim field on the request — auth is fully
/// proven by the outer AES-GCM envelope under the per-account
/// SyncKey. A LogBundleRequest that decrypts is, by construction,
/// from the same account.
/// </remarks>
public sealed class LogBundleRequest
{
    /// <summary>Correlation token echoed back on the response so the
    /// requester can match multiple in-flight requests to multiple peers.</summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>Sender machine id — informational, used in logs.</summary>
    public string FromMachineId { get; set; } = string.Empty;
}

public sealed class LogBundleResponse
{
    public string RequestId { get; set; } = string.Empty;

    public string FromMachineId { get; set; } = string.Empty;

    public string FromHostname { get; set; } = string.Empty;

    /// <summary>Base64-encoded zip bytes produced by the responder's
    /// LogBundler. Null when the responder couldn't build a bundle.</summary>
    public string? ZipBase64 { get; set; }

    /// <summary>Optional error message when ZipBase64 is null.</summary>
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(TcpFrame))]
[JsonSerializable(typeof(ReceiverAnnouncement))]
[JsonSerializable(typeof(LogBundleRequest))]
[JsonSerializable(typeof(LogBundleResponse))]
public partial class TcpFrameContext : JsonSerializerContext { }
