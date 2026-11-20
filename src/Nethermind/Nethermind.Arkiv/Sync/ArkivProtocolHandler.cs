// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;

namespace Nethermind.Arkiv.Sync;

/// <summary>What a syncing node asks of a peer over <c>arkiv/1</c>.</summary>
public interface IArkivSyncPeer
{
    Task<StoreManifestMessage> GetStoreManifest(ulong minNumber, CancellationToken token);
    Task<byte[]> GetStoreChunk(Hash256 hash, int file, long offset, int length, CancellationToken token);
}

/// <summary>
/// The <c>arkiv/1</c> satellite protocol (see <see cref="ArkivSyncProtocol"/>): serves this node's store copy to peers
/// that state-sync, and asks peers for theirs. Registered on the peer's sync peer, as <c>snap</c> is.
/// </summary>
public sealed class ArkivProtocolHandler(
    ISession session,
    INodeStatsManager nodeStats,
    IMessageSerializationService serializer,
    IBackgroundTaskScheduler backgroundTaskScheduler,
    ILogManager logManager,
    ArkivChain chain)
    : ZeroProtocolHandlerBase(session, nodeStats, serializer, backgroundTaskScheduler, logManager), IArkivSyncPeer, IStaticProtocolInfo
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<long, TaskCompletionSource<ArkivSyncMessage>> _pending = new();

    public static byte Version => ArkivSyncProtocol.Version;
    public static string Code => ArkivSyncProtocol.Code;
    public override string Name => "arkiv1";
    public override byte ProtocolVersion => Version;
    public override string ProtocolCode => Code;
    public override int MessageIdSpaceSize => 4;
    protected override TimeSpan InitTimeout => Timeouts.Eth;

    public override void Init() => NotifyProtocolInitialized(new ProtocolInitializedEventArgs(this));

    protected override bool HandleMessageCore(ZeroPacket message)
    {
        switch (message.PacketType)
        {
            case ArkivSyncProtocol.GetStoreManifest:
                HandleInBackground<GetStoreManifestMessage, StoreManifestMessage>(message, ServeManifest);
                return true;
            case ArkivSyncProtocol.GetStoreChunk:
                HandleInBackground<GetStoreChunkMessage, StoreChunkMessage>(message, ServeChunk);
                return true;
            case ArkivSyncProtocol.StoreManifest:
                Complete(Deserialize<StoreManifestMessage>(message));
                return true;
            case ArkivSyncProtocol.StoreChunk:
                Complete(Deserialize<StoreChunkMessage>(message));
                return true;
            default:
                return false;
        }
    }

    private ValueTask<StoreManifestMessage> ServeManifest(GetStoreManifestMessage request, CancellationToken token)
    {
        using GetStoreManifestMessage _ = request;
        ArkivStoreCopy? copy = chain.ServedCopy(request.MinNumber);
        StoreManifestMessage response = copy is null
            ? new StoreManifestMessage()
            : new StoreManifestMessage { Number = copy.Number, Hash = copy.Hash, Files = copy.Files };
        response.RequestId = request.RequestId;
        return ValueTask.FromResult(response);
    }

    private ValueTask<StoreChunkMessage> ServeChunk(GetStoreChunkMessage request, CancellationToken token)
    {
        using GetStoreChunkMessage _ = request;
        int length = Math.Clamp(request.Length, 0, ArkivSyncProtocol.MaxChunk);
        StoreChunkMessage response = new() { Data = chain.ReadServedChunk(request.Hash, request.File, request.Offset, length) ?? [], RequestId = request.RequestId };
        return ValueTask.FromResult(response);
    }

    private void Complete(ArkivSyncMessage response)
    {
        if (_pending.TryRemove(response.RequestId, out TaskCompletionSource<ArkivSyncMessage>? waiting)) waiting.TrySetResult(response);
        else if (Logger.IsDebug) Logger.Debug($"arkiv/1: unrequested response {response.RequestId} from {Session:hide}");
    }

    // Generic in the request's type: Nethermind finds a message's serializer by its static type.
    private async Task<TResponse> Request<TRequest, TResponse>(TRequest request, CancellationToken token)
        where TRequest : ArkivSyncMessage
        where TResponse : ArkivSyncMessage
    {
        TaskCompletionSource<ArkivSyncMessage> waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.RequestId] = waiting;
        try
        {
            Send(request);
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(RequestTimeout);
            ArkivSyncMessage response = await waiting.Task.WaitAsync(timeout.Token);
            return response as TResponse ?? throw new InvalidOperationException($"arkiv/1: a peer answered {request.GetType().Name} with {response.GetType().Name}");
        }
        finally
        {
            _pending.TryRemove(request.RequestId, out _);
        }
    }

    public Task<StoreManifestMessage> GetStoreManifest(ulong minNumber, CancellationToken token) =>
        Request<GetStoreManifestMessage, StoreManifestMessage>(new GetStoreManifestMessage { MinNumber = minNumber }, token);

    public async Task<byte[]> GetStoreChunk(Hash256 hash, int file, long offset, int length, CancellationToken token) =>
        (await Request<GetStoreChunkMessage, StoreChunkMessage>(new GetStoreChunkMessage { Hash = hash, File = file, Offset = offset, Length = length }, token)).Data;

    public override void DisconnectProtocol(DisconnectReason disconnectReason, string details) => Dispose();

    public override void Dispose()
    {
        foreach (KeyValuePair<long, TaskCompletionSource<ArkivSyncMessage>> waiting in _pending)
        {
            waiting.Value.TrySetCanceled();
        }
        _pending.Clear();
        ClearProtocolEvents();
    }
}

/// <summary>Advertises <c>arkiv/1</c> to every peer.</summary>
public sealed class ArkivCapabilityResolver : IP2PCapabilityResolver
{
    public event Action? Changed { add { } remove { } }

    public void Resolve(ISet<Capability> capabilities) => capabilities.Add(new Capability(ArkivSyncProtocol.Code, ArkivSyncProtocol.Version));
}
