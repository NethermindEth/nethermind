// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Test.ParallelSync;

namespace Nethermind.Synchronization.Test.Mocks;

/// <summary>
/// A minimal <see cref="ISyncPeerPool"/> for dispatcher tests: allocations are limited to
/// <paramref name="peerCount"/> concurrent peers, and <see cref="Allocate"/> waits until one is freed.
/// </summary>
public class TestSyncPeerPool(int peerCount = 1) : ISyncPeerPool
{
    private readonly SemaphoreSlim _peerSemaphore = new(peerCount, peerCount);
    private readonly Lock _lock = new();
    private int _freedCount;

    /// <summary>Total number of <see cref="Free"/> calls, for asserting that allocations are not leaked.</summary>
    public int FreedCount => Volatile.Read(ref _freedCount);

    public async Task<SyncPeerAllocation> Allocate(
        IPeerAllocationStrategy peerAllocationStrategy,
        AllocationContexts contexts,
        int timeoutMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        await _peerSemaphore.WaitAsync(cancellationToken);
        ISyncPeer syncPeer = new MockSyncPeer("Nethermind", UInt256.One);
        SyncPeerAllocation allocation = new(new PeerInfo(syncPeer), contexts, _lock);
        return allocation;
    }

    private class MockSyncPeer(string clientId, UInt256 totalDifficulty) : BaseSyncPeerMock
    {
        public override string ClientId => clientId;
        public override UInt256? TotalDifficulty => totalDifficulty;
    }

    public void Free(SyncPeerAllocation syncPeerAllocation)
    {
        Interlocked.Increment(ref _freedCount);
        _peerSemaphore.Release();
    }

    public void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts contexts)
    {
    }

    public void ReportBreachOfProtocol(PeerInfo peerInfo, DisconnectReason disconnectReason, string details)
    {
    }

    public void ReportWeakPeer(PeerInfo peerInfo, AllocationContexts contexts)
    {
    }

    public Task<int?> EstimateRequestLimit(RequestType bodies, IPeerAllocationStrategy peerAllocationStrategy, AllocationContexts blocks,
        CancellationToken token) =>
        Task.FromResult<int?>(null);

    public void WakeUpAll() =>
        throw new NotImplementedException();

    public IEnumerable<PeerInfo> AllPeers { get; } = Array.Empty<PeerInfo>();
    public IEnumerable<PeerInfo> InitializedPeers { get; } = Array.Empty<PeerInfo>();
    public int PeerCount { get; } = 0;
    public int InitializedPeersCount { get; } = 0;
    public int PeerMaxCount { get; } = 0;

    public void AddPeer(ISyncPeer syncPeer)
    {
    }

    public void RemovePeer(ISyncPeer syncPeer)
    {
    }

    public void SetPeerPriority(PublicKey id)
    {
    }

    public void RefreshTotalDifficulty(ISyncPeer syncPeer, Hash256 hash)
    {
    }

    public void Start()
    {
    }

    public Task StopAsync() =>
        Task.CompletedTask;

    public PeerInfo? GetPeer(Node node) =>
        null;

    public event EventHandler<PeerBlockNotificationEventArgs> NotifyPeerBlock = static delegate { };

    public ValueTask DisposeAsync()
    {
        _peerSemaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}
