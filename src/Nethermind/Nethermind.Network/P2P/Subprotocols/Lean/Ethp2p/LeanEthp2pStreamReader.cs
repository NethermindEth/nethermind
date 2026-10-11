// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>Invalid stream data under the ethp2p binding: the binding is terminated.</summary>
internal sealed class LeanEthp2pViolationException(string message) : Exception(message);

/// <summary>The stream is not a retrieval stream; the shared dispatcher routes it.</summary>
internal sealed class LeanEthp2pOtherStreamException(byte selector) : Exception($"stream selector {selector} is not a retrieval stream")
{
    public byte Selector { get; } = selector;
}

/// <summary>Receives what an incoming stream carries.</summary>
internal interface ILeanEthp2pStreamHandler
{
    /// <summary>Resolves a response stream's request ID; see <see cref="LeanObjectTransport.OpenResponseStream"/>.</summary>
    LeanResponseTarget OpenResponse(ulong requestId);

    /// <summary>A control-stream message, Status first.</summary>
    void OnControl(LeanMessage message);

    /// <summary>A Chunk on a response stream; its bytes alias the frame and are only valid during the call.</summary>
    void OnChunk(in LeanChunkView chunk);
}

internal enum LeanEthp2pStreamEnd
{
    /// <summary>The stream named a retired request and was discarded unread.</summary>
    Discarded,

    /// <summary>FIN before a complete terminal response: incomplete delivery, never Served.</summary>
    Incomplete,

    /// <summary>FIN directly after the terminal response, which may now retire the request.</summary>
    Complete
}

/// <summary>Incremental parser of one incoming retrieval stream under the EIP-8437 ethp2p profile.</summary>
/// <remarks>
/// The stream type, request ID, message ID and payload length are checked before any payload byte is read or buffered.
/// A terminal response is held until FIN, so extra bytes after it are rejected before the request retires.
/// Not thread-safe: one reader per stream.
/// </remarks>
internal sealed class LeanEthp2pStreamReader(ILeanEthp2pStreamHandler handler)
{
    private enum Stage { Type, RequestId, Header, Payload, Discard }

    private static readonly LeanStatusMessageSerializer StatusSerializer = new();
    private static readonly AnnounceObjectsMessageSerializer AnnounceSerializer = new();
    private static readonly GetObjectsMessageSerializer GetObjectsSerializer = new();
    private static readonly ObjectsMessageSerializer ObjectsSerializer = new();
    private static readonly GetChunksMessageSerializer GetChunksSerializer = new();
    private static readonly CompleteMessageSerializer CompleteSerializer = new();
    private static readonly CancelMessageSerializer CancelSerializer = new();
    private static readonly GetTransactionsMessageSerializer GetTransactionsSerializer = new();
    private static readonly TransactionsMessageSerializer TransactionsSerializer = new();

    private readonly byte[] _small = new byte[LeanEthp2pProtocol.ResponsePrefixBytes];
    private Stage _stage = Stage.Type;
    private int _want = 1;
    private int _have;
    private byte[]? _payload;
    private int _messageId;
    private bool _statusSeen;
    private LeanResponseTarget _target;

    /// <summary>The stream type once its first byte is read.</summary>
    public byte? Type { get; private set; }

    /// <summary>The request ID named by a response stream's prefix.</summary>
    public ulong RequestId { get; private set; }

    /// <summary>Whether the response stream's prefix named a live request.</summary>
    public bool Identified { get; private set; }

    /// <summary>The terminal response, held until FIN.</summary>
    public LeanMessage? Terminal { get; private set; }

    /// <summary>Whether a prefix, frame header or payload has started but not finished.</summary>
    public bool InPartialUnit => _have > 0 || _stage == Stage.Payload;

    public bool IsDiscarding => _stage == Stage.Discard;

    /// <summary>Payload bytes taken so far, for checks that header rejection reads none.</summary>
    public long PayloadBytesRead { get; private set; }

    /// <summary>Consumes received bytes, dispatching complete messages.</summary>
    /// <exception cref="LeanEthp2pViolationException">The bytes violate the binding.</exception>
    public void Feed(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_stage == Stage.Discard) return;
            if (Terminal is not null) throw new LeanEthp2pViolationException("bytes after a terminal response");
            int count = Math.Min(_want - _have, data.Length);
            Span<byte> target = _stage == Stage.Payload ? _payload.AsSpan(_have, count) : _small.AsSpan(_have, count);
            data[..count].CopyTo(target);
            data = data[count..];
            _have += count;
            if (_stage == Stage.Payload) PayloadBytesRead += count;
            if (_have < _want) continue;
            _have = 0;
            Consume();
        }
    }

    /// <summary>Handles FIN.</summary>
    /// <exception cref="LeanEthp2pViolationException">The control stream was closed.</exception>
    public LeanEthp2pStreamEnd Finish()
    {
        if (_stage == Stage.Discard) return LeanEthp2pStreamEnd.Discarded;
        if (Type == LeanEthp2pProtocol.ControlStream) throw new LeanEthp2pViolationException("control stream closed");
        return Terminal is not null && _have == 0 ? LeanEthp2pStreamEnd.Complete : LeanEthp2pStreamEnd.Incomplete;
    }

    /// <summary>Releases a payload buffer of an unfinished frame.</summary>
    public void Abandon() => ReturnPayload();

    private void Consume()
    {
        switch (_stage)
        {
            case Stage.Type:
                Type = _small[0];
                if (Type == LeanEthp2pProtocol.ControlStream) Next(Stage.Header, LeanEthp2pProtocol.FrameHeaderBytes);
                else if (Type == LeanEthp2pProtocol.ResponseStream) Next(Stage.RequestId, sizeof(ulong));
                else throw new LeanEthp2pOtherStreamException(_small[0]);
                break;
            case Stage.RequestId:
                RequestId = BinaryPrimitives.ReadUInt64BigEndian(_small);
                _target = handler.OpenResponse(RequestId);
                switch (_target)
                {
                    case LeanResponseTarget.Invalid: throw new LeanEthp2pViolationException($"response stream for unissued request {RequestId}");
                    case LeanResponseTarget.Duplicate: throw new LeanEthp2pViolationException($"second response stream for request {RequestId}");
                    case LeanResponseTarget.Retired: Next(Stage.Discard, 0); break;
                    default:
                        Identified = true;
                        Next(Stage.Header, LeanEthp2pProtocol.FrameHeaderBytes);
                        break;
                }
                break;
            case Stage.Header:
                ConsumeHeader();
                break;
            case Stage.Payload:
                ConsumePayload();
                break;
        }
    }

    private void ConsumeHeader()
    {
        _messageId = _small[0];
        if (_messageId >= LeanProtocol.MessageCount) throw new LeanEthp2pViolationException($"unknown message ID {_messageId}");
        if (!Allowed(_messageId)) throw new LeanEthp2pViolationException($"message {_messageId} is not allowed here");
        uint length = BinaryPrimitives.ReadUInt32BigEndian(_small.AsSpan(1));
        if (length == 0 || length > LeanEthp2pProtocol.MaxPayload(_messageId))
            throw new LeanEthp2pViolationException($"message {_messageId} payload length {length}");
        _payload = ArrayPool<byte>.Shared.Rent((int)length);
        Next(Stage.Payload, (int)length);
    }

    private bool Allowed(int messageId) => Type == LeanEthp2pProtocol.ControlStream
        ? _statusSeen
            ? messageId is LeanMessageCode.AnnounceObjects or LeanMessageCode.GetObjects or LeanMessageCode.GetChunks
                or LeanMessageCode.Cancel or LeanMessageCode.GetTransactions
            : messageId == LeanMessageCode.Status
        : _target switch
        {
            LeanResponseTarget.GetObjects => messageId == LeanMessageCode.Objects,
            LeanResponseTarget.GetChunks => messageId is LeanMessageCode.Chunk or LeanMessageCode.Complete,
            _ => messageId == LeanMessageCode.Transactions
        };

    private void ConsumePayload()
    {
        ReadOnlySpan<byte> payload = _payload.AsSpan(0, _want);
        try
        {
            if (Type == LeanEthp2pProtocol.ControlStream) handler.OnControl(DecodeControl(payload));
            else if (_messageId == LeanMessageCode.Chunk)
            {
                LeanChunkView chunk = LeanChunkView.Parse(payload);
                CheckResponseId(chunk.RequestId);
                handler.OnChunk(chunk);
            }
            else
            {
                LeanMessage terminal = _messageId switch
                {
                    LeanMessageCode.Objects => ObjectsSerializer.Decode(payload),
                    LeanMessageCode.Complete => CompleteSerializer.Decode(payload),
                    _ => TransactionsSerializer.Decode(payload)
                };
                CheckResponseId(LeanEthp2pProtocol.RequestIdOf(terminal));
                Terminal = terminal;
            }
        }
        catch (RlpException exception)
        {
            throw new LeanEthp2pViolationException($"malformed message {_messageId}: {exception.Message}");
        }
        finally
        {
            ReturnPayload();
        }
        Next(Stage.Header, LeanEthp2pProtocol.FrameHeaderBytes);
    }

    private LeanMessage DecodeControl(ReadOnlySpan<byte> payload)
    {
        if (_messageId == LeanMessageCode.Status)
        {
            LeanStatusMessage status = StatusSerializer.Decode(payload);
            _statusSeen = true;
            return status;
        }
        return _messageId switch
        {
            LeanMessageCode.AnnounceObjects => AnnounceSerializer.Decode(payload),
            LeanMessageCode.GetObjects => GetObjectsSerializer.Decode(payload),
            LeanMessageCode.GetChunks => GetChunksSerializer.Decode(payload),
            LeanMessageCode.Cancel => CancelSerializer.Decode(payload),
            _ => GetTransactionsSerializer.Decode(payload)
        };
    }

    private void CheckResponseId(ulong requestId)
    {
        if (requestId != RequestId) throw new LeanEthp2pViolationException($"response for request {requestId} on the stream of request {RequestId}");
    }

    private void Next(Stage stage, int want)
    {
        _stage = stage;
        _want = want;
    }

    private void ReturnPayload()
    {
        if (_payload is null) return;
        ArrayPool<byte>.Shared.Return(_payload);
        _payload = null;
    }
}
