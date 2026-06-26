// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Peers
{
    public interface ISyncPeerPool : IAsyncDisposable
    {
        Task<SyncPeerAllocation> Allocate(
            IPeerAllocationStrategy peerAllocationStrategy,
            AllocationContexts allocationContexts,
            int timeoutMilliseconds = 0,
            CancellationToken cancellationToken = default);

        void Free(SyncPeerAllocation syncPeerAllocation);

        void ReportNoSyncProgress(PeerInfo peerInfo, AllocationContexts allocationContexts);

        void ReportBreachOfProtocol(PeerInfo peerInfo, DisconnectReason disconnectReason, string details);

        void ReportWeakPeer(PeerInfo peerInfo, AllocationContexts allocationContexts);

        /// <summary>
        /// Estimate the request limit for a specific request type for the peer which get allocated next based
        /// on the allocation strategy and context. May not be accurate as different peer may get allocated.
        /// </summary>
        Task<int?> EstimateRequestLimit(
            RequestType requestType,
            IPeerAllocationStrategy peerAllocationStrategy,
            AllocationContexts contexts,
            CancellationToken token);

        /// <summary>
        /// Wakes up all the sleeping peers.
        /// </summary>
        void WakeUpAll();

        /// <summary>
        /// All peers maintained by the pool
        /// </summary>
        IEnumerable<PeerInfo> AllPeers { get; }

        /// <summary>
        /// All the useful peers available for allocation.
        /// </summary>
        IEnumerable<PeerInfo> InitializedPeers { get; }

        /// <summary>
        /// Number of all sync peers
        /// </summary>
        int PeerCount { get; }

        /// <summary>
        /// Number of peers that are not sleeping
        /// </summary>
        int InitializedPeersCount { get; }

        /// <summary>
        /// Max number of peers
        /// </summary>
        int PeerMaxCount { get; }

        /// <summary>
        /// Invoked when a new connection is established and ETH subprotocol handshake is finished - this node is ready to sync.
        /// </summary>
        /// <param name="syncPeer"></param>
        void AddPeer(ISyncPeer syncPeer);

        /// <summary>
        /// Invoked after a session / connection is closed.
        /// </summary>
        /// <param name="syncPeer"></param>
        void RemovePeer(ISyncPeer syncPeer);

        /// <summary>
        /// Setting given peer as priority.
        /// </summary>
        /// <param name="id"></param>
        void SetPeerPriority(PublicKey id);

        /// <summary>
        /// It is hard to track total difficulty so occasionally we send a total difficulty request to update node information.
        /// Specifically when nodes send HintBlock message they do not attach total difficulty information.
        /// </summary>
        /// <param name="syncPeer"></param>
        /// <param name="hash">Hash of a block that we know might be the head block of the peer</param>
        void RefreshTotalDifficulty(ISyncPeer syncPeer, Hash256 hash);

        /// <summary>
        /// Starts the pool loops.
        /// </summary>
        void Start();

        PeerInfo? GetPeer(Node node);

        event EventHandler<PeerBlockNotificationEventArgs> NotifyPeerBlock;
    }

    public static class SyncPeerPoolExtensions
    {
        public static Task<T?> AllocateAndRun<T>(
            this ISyncPeerPool syncPeerPool,
            Func<ISyncPeer, Task<T>> func,
            IPeerAllocationStrategy peerAllocationStrategy,
            AllocationContexts allocationContexts,
            CancellationToken cancellationToken) => syncPeerPool.AllocateAndRun<T>(
                (peerInfo) => func(peerInfo.SyncPeer),
                peerAllocationStrategy,
                allocationContexts,
                cancellationToken);

        public static async Task<T?> AllocateAndRun<T>(
            this ISyncPeerPool syncPeerPool,
            Func<PeerInfo, Task<T>> func,
            IPeerAllocationStrategy peerAllocationStrategy,
            AllocationContexts allocationContexts,
            CancellationToken cancellationToken)
        {
            SyncPeerAllocation? allocation = await syncPeerPool.Allocate(
                peerAllocationStrategy,
                allocationContexts,
                timeoutMilliseconds: int.MaxValue,
                cancellationToken: cancellationToken);
            try
            {
                if (allocation?.Current is null) return default;
                return await func(allocation.Current);
            }
            finally
            {
                syncPeerPool.Free(allocation);
            }
        }


        public static async Task<BlockHeader?> FetchHeaderFromPeer(this ISyncPeerPool syncPeerPool, Hash256 hash, CancellationToken cancellationToken = default)
        {
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(Timeouts.DefaultFetchHeaderTimeout);

                List<Task<BlockHeader?>> requests = new(syncPeerPool.InitializedPeersCount);
                foreach (PeerInfo peer in syncPeerPool.InitializedPeers)
                {
                    requests.Add(FetchHeader(peer, hash, cts.Token));
                }

                while (requests.Count > 0)
                {
                    Task<BlockHeader?> completedRequest = await Task.WhenAny(requests);
                    requests.Remove(completedRequest);

                    BlockHeader? header = await completedRequest;
                    if (header is not null)
                    {
                        await cts.CancelAsync();
                        return header;
                    }
                }

                return await FetchAllocatedHeader(syncPeerPool, hash, cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Timeout or no peer.
                return null;
            }

            static async Task<BlockHeader?> FetchAllocatedHeader(ISyncPeerPool syncPeerPool, Hash256 headerHash, CancellationToken token)
            {
                using IOwnedReadOnlyList<BlockHeader>? headers = await syncPeerPool.AllocateAndRun(
                    peer => peer.GetBlockHeaders(headerHash, 1, 0, token),
                    BySpeedStrategy.FastestHeader,
                    AllocationContexts.Headers,
                    token);

                ReadOnlySpan<BlockHeader> headersSpan = headers is null ? [] : headers.AsSpan();
                return headersSpan.Length == 1 ? headersSpan[0] : null;
            }

            static async Task<BlockHeader?> FetchHeader(PeerInfo peer, Hash256 headerHash, CancellationToken token)
            {
                try
                {
                    return await peer.SyncPeer.GetHeadBlockHeader(headerHash, token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
                {
                    return null;
                }
            }
        }
    }
}
