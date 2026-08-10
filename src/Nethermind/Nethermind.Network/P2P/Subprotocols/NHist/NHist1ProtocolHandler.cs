// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.RequestSizer;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.NHist.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.State;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P.Subprotocols.NHist;

public class NHist1ProtocolHandler : ZeroProtocolHandlerBase, IStaticProtocolInfo, INHistSyncPeer
{
    private IHistoryServer HistoryServer { get; }
    private bool CanServe { get; }

    public override string Name => "nhist1";
    protected override TimeSpan InitTimeout => Timeouts.Eth;

    public static byte Version => NHistVersions.NHist1;
    public static string Code => Protocol.NHist;
    public override byte ProtocolVersion => Version;
    public override string ProtocolCode => Code;
    public override int MessageIdSpaceSize => 7;

    private const string DisconnectMessage = "Serving windowed flat history is not implemented in this node.";
    private const string TooManyInFlightMessage = "Too many concurrent nhist requests in flight for this peer.";
    private const int MaxInFlightRequestsPerPeer = IHistoryServer.MaxInFlightRequestsPerPeer;
    private static readonly TimeSpan ServedBytesWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ServeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RowsResponseTimeout = TimeSpan.FromSeconds(30);
    private static readonly long TicksPerWindow = (long)(Stopwatch.Frequency * ServedBytesWindow.TotalSeconds);

    private readonly long _servedBytesPerWindowCap;

    private readonly MessageDictionary<GetHistoryRangeAtHeightMessage, HistoryRangeAtHeightMessage> _getHistoryRangeRequests;
    private readonly MessageDictionary<GetChangesetsMessage, ChangesetsMessage> _getChangesetsRequests;
    private readonly MessageDictionary<GetHistoryRowsMessage, HistoryRowsMessage> _getHistoryRowsRequests;
    private readonly LatencyBasedRequestSizer _rowsRequestSizer = new(
        minRequestLimit: 512_000,
        maxRequestLimit: 3_000_000,
        lowerWatermark: TimeSpan.FromMilliseconds(2000),
        upperWatermark: TimeSpan.FromMilliseconds(3500));

    private int _inFlightRequests;
    private long _windowState;

    private sealed record PeerStatus(HistoryServingScope[] Scopes, bool SupportsFullClone, byte RowFormatVersion);

    private PeerStatus? _peerStatus;

    public HistoryServingScope[] PeerServedScopes => Volatile.Read(ref _peerStatus)?.Scopes ?? [];

    public bool PeerSupportsFullClone => Volatile.Read(ref _peerStatus)?.SupportsFullClone ?? false;

    public byte PeerRowFormatVersion => Volatile.Read(ref _peerStatus)?.RowFormatVersion ?? 0;

    public NHist1ProtocolHandler(
        ISession session,
        INodeStatsManager nodeStats,
        IMessageSerializationService serializer,
        IBackgroundTaskScheduler backgroundTaskScheduler,
        ILogManager logManager,
        IHistoryServer historyServer,
        ISyncConfig syncConfig)
        : base(session, nodeStats, serializer, backgroundTaskScheduler, logManager)
    {
        _getHistoryRangeRequests = new(this);
        _getChangesetsRequests = new(this);
        _getHistoryRowsRequests = new(this);
        HistoryServer = historyServer;
        CanServe = historyServer.CanServe;
        _servedBytesPerWindowCap = Math.Max(1024 * 1024, syncConfig.HistoryServingMaxBytesPerSecond);
    }

    public override void Init()
    {
        Send(new NHistStatusMessage
        {
            Scopes = [.. HistoryServer.ServedScopes],
            SupportsFullClone = HistoryServer.CanServeFullClone,
            RowFormatVersion = HistoryServer.RowFormatVersion
        });
        if (Logger.IsInfo) Logger.Info($"nhist1 initialized on {Session}: sent status (SupportsFullClone={HistoryServer.CanServeFullClone}, row format {HistoryServer.RowFormatVersion}, scopes {HistoryServer.ServedScopes.Count}).");
        NotifyProtocolInitialized(new ProtocolInitializedEventArgs(this));
    }

    public override void Dispose() => ClearProtocolEvents();

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) => Dispose();

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        int size = message.Content.ReadableBytes;

        switch (message.PacketType)
        {
            case NHist1MessageCode.Status:
                NHistStatusMessage statusMessage = Deserialize<NHistStatusMessage>(message.Content);
                ReportIn(statusMessage, size);
                Volatile.Write(ref _peerStatus, new PeerStatus(statusMessage.Scopes, statusMessage.SupportsFullClone, statusMessage.RowFormatVersion));
                if (Logger.IsInfo) Logger.Info($"nhist1 status from {Session}: SupportsFullClone={statusMessage.SupportsFullClone}, row format {statusMessage.RowFormatVersion}, scopes {statusMessage.Scopes.Length}.");
                return true;
            case NHist1MessageCode.GetHistoryRangeAtHeight:
                if (ShouldServeNHist())
                    ScheduleOrReleaseQuota<GetHistoryRangeAtHeightMessage, HistoryRangeAtHeightMessage>(message, Handle);
                return true;
            case NHist1MessageCode.HistoryRangeAtHeight:
                HistoryRangeAtHeightMessage rangeMessage = Deserialize<HistoryRangeAtHeightMessage>(message.Content);
                ReportIn(rangeMessage, size);
                Handle(rangeMessage, size);
                return true;
            case NHist1MessageCode.GetChangesets:
                if (ShouldServeNHist())
                    ScheduleOrReleaseQuota<GetChangesetsMessage, ChangesetsMessage>(message, Handle);
                return true;
            case NHist1MessageCode.Changesets:
                ChangesetsMessage changesetsMessage = Deserialize<ChangesetsMessage>(message.Content);
                ReportIn(changesetsMessage, size);
                Handle(changesetsMessage, size);
                return true;
            case NHist1MessageCode.GetHistoryRows:
                if (ShouldServeNHist())
                    ScheduleOrReleaseQuota<GetHistoryRowsMessage, HistoryRowsMessage>(message, Handle);
                return true;
            case NHist1MessageCode.HistoryRows:
                HistoryRowsMessage rowsMessage = Deserialize<HistoryRowsMessage>(message.Content);
                ReportIn(rowsMessage, size);
                Handle(rowsMessage, size);
                return true;
            default:
                return false;
        }
    }

    private bool ShouldServeNHist()
    {
        if (!CanServe)
        {
            Session.InitiateDisconnect(DisconnectReason.NHistServerNotImplemented, DisconnectMessage);
            if (Logger.IsDebug) Logger.Debug($"Peer disconnected because of requesting nhist data. Peer: {Session.Node.ClientId}");
            return false;
        }

        if (Interlocked.Increment(ref _inFlightRequests) > MaxInFlightRequestsPerPeer)
        {
            Interlocked.Decrement(ref _inFlightRequests);
            Session.InitiateDisconnect(DisconnectReason.MessageLimitsBreached, TooManyInFlightMessage);
            if (Logger.IsDebug) Logger.Debug($"Peer disconnected for exceeding the nhist in-flight request quota. Peer: {Session.Node.ClientId}");
            return false;
        }

        return true;
    }

    private void ScheduleOrReleaseQuota<TReq, TRes>(ZeroPacket message, Func<TReq, CancellationToken, ValueTask<TRes>> handle)
        where TReq : P2PMessage
        where TRes : P2PMessage
    {
        TReq request;
        try
        {
            request = Deserialize<TReq>(message.Content);
        }
        catch
        {
            Interlocked.Decrement(ref _inFlightRequests);
            throw;
        }

        ReportIn(request, message.Content.ReadableBytes);

        if (!BackgroundTaskScheduler.TryScheduleSyncServe(request, handle, ServeTimeout))
        {
            Interlocked.Decrement(ref _inFlightRequests);
        }
    }

    private async ValueTask ThrottleForServedBytesAsync(long responseBytes, CancellationToken cancellationToken)
    {
        long nowWindow = Stopwatch.GetTimestamp() / TicksPerWindow;
        long servedInWindow;

        while (true)
        {
            long snapshot = Volatile.Read(ref _windowState);
            long snapshotWindow = snapshot >> 32;
            long snapshotServed = snapshot & 0xFFFFFFFFL;

            long baseServed = snapshotWindow == nowWindow ? snapshotServed : 0;
            long newServed = Math.Min(baseServed + responseBytes, uint.MaxValue);
            long newState = (nowWindow << 32) | newServed;

            if (Interlocked.CompareExchange(ref _windowState, newState, snapshot) == snapshot)
            {
                servedInWindow = newServed;
                break;
            }
        }

        if (servedInWindow > _servedBytesPerWindowCap)
        {
            long ticksIntoWindow = Stopwatch.GetTimestamp() - nowWindow * TicksPerWindow;
            TimeSpan remaining = ServedBytesWindow - TimeSpan.FromSeconds((double)ticksIntoWindow / Stopwatch.Frequency);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, CancellationToken.None);
            }
        }
    }

    private void Handle(HistoryRangeAtHeightMessage msg, long size) => _getHistoryRangeRequests.Handle(msg.RequestId, msg, size);

    private void Handle(ChangesetsMessage msg, long size) => _getChangesetsRequests.Handle(msg.RequestId, msg, size);

    private void Handle(HistoryRowsMessage msg, long size) => _getHistoryRowsRequests.Handle(msg.RequestId, msg, size);

    private async ValueTask<HistoryRangeAtHeightMessage> Handle(GetHistoryRangeAtHeightMessage getMessage, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<HistoryRangeEntry>? entries = null;
        try
        {
            using GetHistoryRangeAtHeightMessage message = getMessage;
            long byteLimit = NHistMessageLimits.ClampResponseBytes(message.ResponseBytes);
            byte[]? cursor = message.Cursor.Length == 0 ? null : message.Cursor;
            byte[]? nextCursor;
            (entries, nextCursor) = HistoryServer.GetHistoryRangeAtHeight(
                message.StartKey, message.EndKey, message.Height, cursor, byteLimit, NHistMessageLimits.MaxResponseEntries, cancellationToken);

            long responseBytes = 0;
            for (int i = 0; i < entries.Count; i++) responseBytes += entries[i].Value.Length;
            await ThrottleForServedBytesAsync(responseBytes, cancellationToken);

            HistoryRangeAtHeightMessage response = new()
            {
                RequestId = message.RequestId,
                Entries = entries,
                NextCursor = nextCursor
            };
            entries = null;
            return response;
        }
        catch
        {
            entries?.Dispose();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightRequests);
        }
    }

    private async ValueTask<ChangesetsMessage> Handle(GetChangesetsMessage getMessage, CancellationToken cancellationToken)
    {
        ArrayPoolList<ChangesetChunkEntry>? chunks = null;
        try
        {
            using GetChangesetsMessage message = getMessage;
            long byteLimit = NHistMessageLimits.ClampResponseBytes(message.ResponseBytes);
            chunks = new ArrayPoolList<ChangesetChunkEntry>(16);
            long responseBytes = 0;

            await foreach (ChangesetChunkEntry chunk in HistoryServer.GetChangesets(message.FromBlock, message.ToBlock, byteLimit, NHistMessageLimits.MaxResponseChunks, cancellationToken))
            {
                chunks.Add(chunk);
                responseBytes += chunk.Payload.Length;
            }

            await ThrottleForServedBytesAsync(responseBytes, cancellationToken);

            ChangesetsMessage response = new() { RequestId = message.RequestId, Chunks = chunks };
            chunks = null;
            return response;
        }
        catch
        {
            chunks?.Dispose();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightRequests);
        }
    }

    private async ValueTask<HistoryRowsMessage> Handle(GetHistoryRowsMessage getMessage, CancellationToken cancellationToken)
    {
        IOwnedReadOnlyList<HistoryRowEntry>? entries = null;
        try
        {
            using GetHistoryRowsMessage message = getMessage;
            long byteLimit = NHistMessageLimits.ClampResponseBytes(message.ResponseBytes);
            byte[]? cursor = message.Cursor.Length == 0 ? null : message.Cursor;
            byte[]? nextCursor;
            bool refused;
            (entries, nextCursor, refused) = HistoryServer.GetHistoryRows(
                message.Column, message.StartKey, message.EndKey, cursor, byteLimit, NHistMessageLimits.MaxResponseRowEntries, cancellationToken);

            long responseBytes = 0;
            for (int i = 0; i < entries.Count; i++) responseBytes += entries[i].Key.Length + entries[i].Value.Length;
            await ThrottleForServedBytesAsync(responseBytes, cancellationToken);

            HistoryRowsMessage response = new()
            {
                RequestId = message.RequestId,
                Entries = entries,
                NextCursor = nextCursor,
                Refused = refused
            };
            entries = null;
            return response;
        }
        catch
        {
            entries?.Dispose();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlightRequests);
        }
    }

    public async Task<HistoryRowsMessage> GetHistoryRows(
        HistoryRowColumn column, byte[] startKey, byte[] endKey, byte[]? cursor, CancellationToken token) =>
        await _rowsRequestSizer.MeasureLatency(bytesLimit =>
            SendRequest(new GetHistoryRowsMessage
            {
                Column = column,
                StartKey = startKey,
                EndKey = endKey,
                Cursor = cursor ?? [],
                ResponseBytes = bytesLimit
            }, _getHistoryRowsRequests, token, RowsResponseTimeout));

    public async Task<HistoryRangeAtHeightMessage> GetHistoryRangeAtHeight(
        ValueHash256 startKey, ValueHash256 endKey, ulong height, byte[]? cursor, CancellationToken token) =>
        await _nodeStats.RunLatencyRequestSizer(RequestType.SnapRanges, bytesLimit =>
            SendRequest(new GetHistoryRangeAtHeightMessage
            {
                StartKey = startKey,
                EndKey = endKey,
                Height = height,
                Cursor = cursor ?? [],
                ResponseBytes = bytesLimit
            }, _getHistoryRangeRequests, token));

    public async Task<ChangesetsMessage> GetChangesets(ulong fromBlock, ulong toBlock, CancellationToken token) =>
        await _nodeStats.RunLatencyRequestSizer(RequestType.SnapRanges, bytesLimit =>
            SendRequest(new GetChangesetsMessage
            {
                FromBlock = fromBlock,
                ToBlock = toBlock,
                ResponseBytes = bytesLimit
            }, _getChangesetsRequests, token));

    private async Task<TOut> SendRequest<TIn, TOut>(TIn msg, MessageDictionary<TIn, TOut> messageDictionary, CancellationToken token, TimeSpan? timeout = null)
        where TIn : NHistMessageBase
        where TOut : NHistMessageBase
    {
        Request<TIn, TOut> request = new(msg);
        messageDictionary.Send(request);

        return await HandleResponse(request, TransferSpeedType.SnapRanges, static req => req.ToString(), token, timeout);
    }

    // INHistSyncPeer: the plain-DTO facing surface a sync-layer consumer reaches through
    // ISyncPeer.TryGetSatelliteProtocol<INHistSyncPeer> — copies entries out of the pooled wire message and
    // disposes it immediately, so nothing outside this handler ever has to know about IOwnedReadOnlyList lifetime.
    async Task<NHistChangesetsPage> INHistSyncPeer.GetChangesets(ulong fromBlockInclusive, ulong toBlockInclusive, CancellationToken token)
    {
        using ChangesetsMessage response = await GetChangesets(fromBlockInclusive, toBlockInclusive, token);
        ChangesetChunkEntry[] copy = new ChangesetChunkEntry[response.Chunks.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            ChangesetChunkEntry chunk = response.Chunks[i];
            copy[i] = new ChangesetChunkEntry(chunk.Block, chunk.ChunkIndex, chunk.IsLastChunkForBlock, chunk.Payload.ToArray());
        }

        return new NHistChangesetsPage(copy);
    }

    async Task<NHistRowsPage> INHistSyncPeer.GetHistoryRows(HistoryRowColumn column, byte[] startKey, byte[] endKey, byte[]? cursor, CancellationToken token)
    {
        using HistoryRowsMessage response = await GetHistoryRows(column, startKey, endKey, cursor, token);
        HistoryRowEntry[] copy = new HistoryRowEntry[response.Entries.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            HistoryRowEntry entry = response.Entries[i];
            copy[i] = new HistoryRowEntry((byte[])entry.Key.Clone(), entry.Value.ToArray());
        }

        return new NHistRowsPage(copy, response.NextCursor, response.Refused);
    }
}
