// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Logging;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>The EIP-8437 <c>lean/1</c> connection: Status negotiation and strict decoding for the shared object transport.</summary>
public sealed class Lean1ProtocolHandler(ISession session, INodeStatsManager nodeStats, IMessageSerializationService serializer,
    IBackgroundTaskScheduler backgroundTaskScheduler, ILogManager logManager, LeanObjectTransport transport)
    : ZeroProtocolHandlerBase(session, nodeStats, serializer, backgroundTaskScheduler, logManager), IStaticProtocolInfo, ILeanLink
{
    private static readonly LeanStatusMessageSerializer StatusSerializer = new();
    private static readonly AnnounceObjectsMessageSerializer AnnounceSerializer = new();
    private static readonly GetObjectsMessageSerializer GetObjectsSerializer = new();
    private static readonly ObjectsMessageSerializer ObjectsSerializer = new();
    private static readonly GetChunksMessageSerializer GetChunksSerializer = new();
    private static readonly CompleteMessageSerializer CompleteSerializer = new();
    private static readonly CancelMessageSerializer CancelSerializer = new();
    private static readonly GetTransactionsMessageSerializer GetTransactionsSerializer = new();
    private static readonly TransactionsMessageSerializer TransactionsSerializer = new();

    private readonly LeanObjectTransport _transport = transport;
    private LeanPeer? _peer;
    private bool _statusReceived;
    private int _disposed;

    public static string Code => LeanProtocol.Code;
    public static byte Version => LeanProtocol.Version;
    public override string Name => "lean1";
    public override string ProtocolCode => Code;
    public override byte ProtocolVersion => Version;
    public override int MessageIdSpaceSize => LeanProtocol.MessageCount;
    protected override TimeSpan InitTimeout => TimeSpan.FromSeconds(10);

    string ILeanLink.Description => Session.ToString() ?? "lean peer";

    public override void Init()
    {
        if (!Session.HasAgreedCapability(new Capability(Code, Version)))
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "lean/1 capability was not negotiated");
            Dispose();
            return;
        }
        // Chunks rely on the bulk path for backpressure and yielding between messages.
        if (Session is not ILeanBulkSession bulk || !bulk.EnableLeanBulk())
        {
            Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, "lean/1 chunk transport unavailable");
            Dispose();
            return;
        }
        if (_transport.CreateStatus() is not { } status)
        {
            Session.InitiateDisconnect(DisconnectReason.ClientQuitting, "lean/1 genesis is unavailable");
            Dispose();
            return;
        }
        Send(status);
        _ = CheckProtocolInitTimeout();
    }

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        if (Volatile.Read(ref _disposed) != 0 || Session.IsClosing) return true;
        if (message.PacketType >= LeanProtocol.MessageCount) return false;
        try
        {
            ReadOnlySpan<byte> data = LeanWire.Span(message.Content);
            if (message.PacketType == LeanMessageCode.Status)
            {
                HandleStatus(StatusSerializer.Decode(data));
                return true;
            }
            // Nothing but Status may arrive before the peer's Status has been accepted.
            if (_peer is not { } peer)
            {
                Violation(_statusReceived ? "message after an incompatible Status" : "message before Status");
                return true;
            }
            switch (message.PacketType)
            {
                case LeanMessageCode.AnnounceObjects: _transport.OnAnnounce(peer, AnnounceSerializer.Decode(data)); break;
                case LeanMessageCode.GetObjects: _transport.OnGetObjects(peer, GetObjectsSerializer.Decode(data)); break;
                case LeanMessageCode.Objects:
                    if (data.Length > LeanProtocol.MaxMetadataResponseBytes) throw new RlpException("Objects exceeds MAX_METADATA_RESPONSE_BYTES");
                    _transport.OnObjects(peer, ObjectsSerializer.Decode(data));
                    break;
                case LeanMessageCode.GetChunks: _transport.OnGetChunks(peer, GetChunksSerializer.Decode(data)); break;
                case LeanMessageCode.Chunk: _transport.OnChunk(peer, LeanChunkView.Parse(data)); break;
                case LeanMessageCode.Complete: _transport.OnComplete(peer, CompleteSerializer.Decode(data)); break;
                case LeanMessageCode.Cancel: _transport.OnCancel(peer, CancelSerializer.Decode(data)); break;
                case LeanMessageCode.GetTransactions: _transport.OnGetTransactions(peer, GetTransactionsSerializer.Decode(data)); break;
                case LeanMessageCode.Transactions:
                    if (data.Length > LeanProtocol.MaxTxResponseBytes) throw new RlpException("Transactions exceeds MAX_TX_RESPONSE_BYTES");
                    _transport.OnTransactions(peer, TransactionsSerializer.Decode(data));
                    break;
            }
        }
        catch (RlpException exception)
        {
            Violation($"malformed message {message.PacketType}: {exception.Message}");
        }
        return true;
    }

    private void HandleStatus(LeanStatusMessage status)
    {
        if (_statusReceived)
        {
            Violation("second Status");
            return;
        }
        _statusReceived = true;
        ReceivedProtocolInitMsg(status);
        LeanPeer? peer = _transport.Accept(this, status, Session.Node?.Id);
        if (peer is null)
        {
            // A well-formed incompatible Status disables only this capability; other protocols keep the connection.
            Dispose();
            return;
        }
        _peer = peer;
        if (Volatile.Read(ref _disposed) != 0)
        {
            _transport.Remove(peer);
            return;
        }
        NotifyProtocolInitialized(new ProtocolInitializedEventArgs(this));
    }

    private void Violation(string reason)
    {
        if (Logger.IsDebug) Logger.Debug($"{Session} lean/1 violation: {reason}");
        LeanMetrics.Record(0, LeanEvent.Violation);
        Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, $"lean/1: {reason}");
    }

    void ILeanLink.Send(LeanMessage message)
    {
        if (Volatile.Read(ref _disposed) != 0 || Session.IsClosing) return;
        // Serializers are resolved by the static message type, so each message is sent as its concrete type.
        switch (message)
        {
            case AnnounceObjectsMessage announce: Send(announce); break;
            case GetObjectsMessage getObjects: Send(getObjects); break;
            case ObjectsMessage objects: Send(objects); break;
            case GetChunksMessage getChunks: Send(getChunks); break;
            case CompleteMessage complete: Send(complete); break;
            case CancelMessage cancel: Send(cancel); break;
            case GetTransactionsMessage getTransactions: Send(getTransactions); break;
            case TransactionsMessage transactions: Send(transactions); break;
            default: throw new ArgumentException($"{message.GetType().Name} is not sent as a control message", nameof(message));
        }
    }

    async ValueTask<bool> ILeanLink.SendChunkAsync(ChunkMessage message, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || Session.IsClosing || Session is not ILeanBulkSession bulk) return false;
        return await bulk.DeliverLeanChunkAsync(message, cancellationToken).ConfigureAwait(false) != 0;
    }

    void ILeanLink.Penalize(string reason) => Session.InitiateDisconnect(DisconnectReason.BreachOfProtocol, reason);

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) => Dispose();

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_peer is { } peer) _transport.Remove(peer);
        ClearProtocolEvents();
    }
}
