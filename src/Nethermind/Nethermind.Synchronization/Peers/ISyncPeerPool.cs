// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Peers
{
    public interface ISyncPeerPool : IDisposable
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

        /// <summary>
        /// Stops the pool loops
        /// </summary>
        /// <returns></returns>
        Task StopAsync();

        PeerInfo? GetPeer(Node node);

        event EventHandler<PeerBlockNotificationEventArgs> NotifyPeerBlock;

        event EventHandler<PeerHeadRefreshedEventArgs> PeerRefreshed;
    }

    public static class SyncPeerPoolExtensions
    {
        public static async Task<T> AllocateAndRun<T>(
            this ISyncPeerPool syncPeerPool,
            Func<ISyncPeer, Task<T>> func,
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
                if (allocation is null) return default;
                return await func(allocation.Current?.SyncPeer);
            }
            finally
            {
                syncPeerPool.Free(allocation);
            }
        }


        public static async Task<BlockHeader?> FetchHeaderFromPeer(this ISyncPeerPool syncPeerPool, Hash256 hash, CancellationToken cancellationToken = default)
        {
            BlockHeader[]? headers = null;
            try
            {
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(Timeouts.DefaultFetchHeaderTimeout);

                headers = await syncPeerPool.AllocateAndRun(
                    peer => peer.GetBlockHeaders(hash, 1, 0, cancellationToken),
                    BySpeedStrategy.FastestHeader,
                    AllocationContexts.Headers,
                    cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout or no peer.
                return null;
            }

            return headers?.Length == 1 ? headers[0] : null;
        }
    }
}
