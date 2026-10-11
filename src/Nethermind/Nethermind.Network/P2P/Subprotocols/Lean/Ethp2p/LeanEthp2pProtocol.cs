// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Net.Security;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>Constants of the optional EIP-8437 ethp2p binding: <c>lean/1</c> messages over authenticated QUIC streams.</summary>
public static class LeanEthp2pProtocol
{
    /// <summary>The ALPN identifier, replacing the libp2p ALPN; there is no further protocol or multiplexer negotiation.</summary>
    public const string Alpn = "ethp2p_lean_1";
    public static readonly SslApplicationProtocol ApplicationProtocol = new(Alpn);

    /// <summary>The TLS server name a dialer sends; peers authenticate by certificate, never by name.</summary>
    /// <remarks>Without one .NET sends the peer's IP literal, which TLS forbids as a server name and msquic fails.</remarks>
    public const string ServerName = "ethp2p";

    /// <summary>The EIP-778 key advertising <c>[version, udp_port]</c>.</summary>
    public const string EnrKey = "leanq";
    public const ulong EnrVersion = 1;

    /// <summary>Broadcast framework stream selectors, single bytes as in the framework's reference implementation.</summary>
    public const byte BcastStream = 0x01;
    public const byte SessStream = 0x02;
    public const byte ChunkStream = 0x03;

    /// <summary>Retrieval stream selectors of this profile.</summary>
    public const byte ControlStream = 0x10;
    public const byte ResponseStream = 0x11;
    public const int ResponsePrefixBytes = 1 + sizeof(ulong);
    public const int FrameHeaderBytes = 1 + sizeof(uint);

    /// <summary>Application error for <c>STOP_SENDING</c> and <c>RESET_STREAM</c> of a request's response stream.</summary>
    public const long RequestCancelledError = 0x01;

    /// <summary>The framework's completion signal: <c>RESET_STREAM</c> of the reconstructing node's outbound SESS stream.</summary>
    public const long ReconstructedError = 0x01;

    /// <summary>Refusal of a framework stream: not subscribed, not yet ready, duplicate or over budget. Not a fault.</summary>
    public const long RefusedError = 0x00;

    /// <summary>Connection close without fault: shutdown, incompatible Status or a duplicate connection.</summary>
    /// <remarks>EIP-8437 defines no connection error codes; these are local choices.</remarks>
    public const long NoError = 0x00;
    public const long ProtocolViolationError = 0x02;

    /// <summary>Inbound SESS and CHUNK streams allowed at once: one SESS stream per live session and some CHUNK streams.</summary>
    /// <remarks>QUIC has one unidirectional stream limit, so SESS streams, which live as long as their session, must not use up
    /// the allowance that CHUNK and retrieval response streams need.</remarks>
    public const int MaxBroadcastStreams = LeanBroadcastEngine.MaxSessions + 16;

    /// <summary>Inbound unidirectional streams per connection: both control streams, one per live response, and broadcast.</summary>
    public const int MaxInboundStreams = 2 + LeanProtocol.MaxRequestsPerPeer + MaxBroadcastStreams;

    /// <summary>Per-stream receive window, at least one maximal frame; QUIC requires a power of two.</summary>
    public const int StreamReceiveWindow = 256 * 1024;

    /// <summary>Connection receive window, at least every inbound stream's full window, so neither control stream is ever
    /// starved by the others; QUIC requires a power of two.</summary>
    public const int ConnectionReceiveWindow = 32 * 1024 * 1024;

    /// <summary>Encodes <c>U8(message_id) || U32(payload_length) || payload</c>.</summary>
    public static byte[] Frame(int messageId, ReadOnlySpan<byte> payload, bool responsePrefix = false, ulong requestId = 0)
    {
        int offset = responsePrefix ? ResponsePrefixBytes : 0;
        byte[] frame = new byte[offset + FrameHeaderBytes + payload.Length];
        if (responsePrefix) WriteResponsePrefix(frame, requestId);
        frame[offset] = (byte)messageId;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(offset + 1), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(offset + FrameHeaderBytes));
        return frame;
    }

    /// <summary>Writes <c>0x01 || U64(request_id)</c>.</summary>
    public static void WriteResponsePrefix(Span<byte> destination, ulong requestId)
    {
        destination[0] = ResponseStream;
        BinaryPrimitives.WriteUInt64BigEndian(destination[1..], requestId);
    }

    /// <summary>The payload ceiling of a message: the tighter Objects and Transactions limits, else <c>MAX_MESSAGE_BYTES</c>.</summary>
    public static int MaxPayload(int messageId) => messageId switch
    {
        LeanMessageCode.Objects => LeanProtocol.MaxMetadataResponseBytes,
        LeanMessageCode.Transactions => LeanProtocol.MaxTxResponseBytes,
        _ => LeanProtocol.MaxMessageBytes
    };

    /// <summary>Encodes a message's RLP payload with its <c>lean/1</c> serializer.</summary>
    public static byte[] Encode(LeanMessage message) => message switch
    {
        LeanStatusMessage m => new LeanStatusMessageSerializer().Encode(m),
        AnnounceObjectsMessage m => new AnnounceObjectsMessageSerializer().Encode(m),
        GetObjectsMessage m => new GetObjectsMessageSerializer().Encode(m),
        ObjectsMessage m => new ObjectsMessageSerializer().Encode(m),
        GetChunksMessage m => new GetChunksMessageSerializer().Encode(m),
        ChunkMessage m => new ChunkMessageSerializer().Encode(m),
        CompleteMessage m => new CompleteMessageSerializer().Encode(m),
        CancelMessage m => new CancelMessageSerializer().Encode(m),
        GetTransactionsMessage m => new GetTransactionsMessageSerializer().Encode(m),
        TransactionsMessage m => new TransactionsMessageSerializer().Encode(m),
        _ => throw new ArgumentException($"{message.GetType().Name} is not a lean/1 message", nameof(message))
    };

    /// <summary>The request ID a response message answers.</summary>
    internal static ulong RequestIdOf(LeanMessage message) => message switch
    {
        ObjectsMessage m => m.RequestId,
        ChunkMessage m => m.RequestId,
        CompleteMessage m => m.RequestId,
        TransactionsMessage m => m.RequestId,
        _ => throw new ArgumentException($"{message.GetType().Name} is not a response", nameof(message))
    };
}
