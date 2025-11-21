// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Tasks;
using Nethermind.Core.Utils;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.State.Healing;
using Nethermind.State.Snap;
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Trie;

namespace Nethermind.Synchronization.Trie;

public class CodeRecovery(ISyncPeerPool peerPool, ILogManager logManager) : ICodeRecovery
{
    // Pick by reduced latency instead of throughput
    private static readonly IPeerAllocationStrategy SnapPeerStrategy =
        new SatelliteProtocolPeerAllocationStrategy<ISnapSyncPeer>(
            new BySpeedStrategy(TransferSpeedType.Latency, false),
            Protocol.Snap);

    private const int ConcurrentAttempt = 3;
    private readonly ILogger _logger = logManager.GetClassLogger<SnapRangeRecovery>();

    public async Task<byte[]?> Recover(byte[] key, CancellationToken cancellationToken = default)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource();

        try
        {
            using ArrayPoolList<Task<byte[]>>? concurrentAttempts = Enumerable.Range(0, ConcurrentAttempt)
                .Select(_ =>
                {
                    return peerPool.AllocateAndRun(async (peer) =>
                    {
                        if (peer is null) return null;
                        try
                        {
                            byte[]? result = await RecoverFromPeer(peer.SyncPeer, key, cts.Token);
                            if (result is not null) return result;

                            if (_logger.IsDebug) _logger.Debug($"Mark peer {peer} weak");
                            peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                        catch (Exception ex)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Error recovering node from {peer} {ex}");
                            peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                        }
                        return null;
                    }, SnapPeerStrategy, AllocationContexts.Snap, cts.Token);
                })
                .ToPooledList(ConcurrentAttempt);

            return await Wait.AnyWhere(result => result is not null, concurrentAttempts);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<byte[]?> RecoverFromPeer(ISyncPeer peer, byte[] key, CancellationToken token)
    {
        if (!peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer? snapProtocol)) return null;

        using ArrayPoolList<ValueHash256> hashes = new(1);
        hashes.Add(new ValueHash256(key));

        using IOwnedReadOnlyList<byte[]> ownedReadOnlyList = await snapProtocol.GetByteCodes(hashes, token);

        if (_logger.IsTrace) _logger.Trace($"Fetched code {key} from {peer}: {string.Join(", ", ownedReadOnlyList.Select(b => b.ToHexString()))}");

        return ownedReadOnlyList.Count switch
        {
            1 => ownedReadOnlyList[0],
            _ => null
        };
    }
}
