// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Stats;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;

namespace Nethermind.Synchronization.Trie;

public class CodeRecovery(ISyncPeerPool peerPool, ILogManager logManager) : ICodeRecovery
{
    // Pick by reduced latency instead of throughput
    private static readonly IPeerAllocationStrategy SnapPeerStrategy =
        new SatelliteProtocolPeerAllocationStrategy<ISnapSyncPeer>(
            new BySpeedStrategy(TransferSpeedType.Latency, false),
            Protocol.Snap);

    // The caller blocks a processing thread on this, and peer allocation waits indefinitely on its own,
    // so the wait must be bounded here. Matches PathNodeRecovery.
    private static readonly TimeSpan RecoveryTimeout = TimeSpan.FromSeconds(3);

    private const int ConcurrentAttempt = 3;
    private readonly ILogger _logger = logManager.GetClassLogger<CodeRecovery>();

    public async Task<byte[]?> Recover(ValueHash256 codeHash, CancellationToken cancellationToken = default)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource(RecoveryTimeout);

        if (_logger.IsDebug) _logger.Debug($"Recovering code {codeHash}");

        try
        {
            using ArrayPoolList<Task<byte[]?>> concurrentAttempts = new(ConcurrentAttempt);
            for (int i = 0; i < ConcurrentAttempt; i++)
            {
                concurrentAttempts.Add(peerPool.AllocateAndRun(async (PeerInfo peer) =>
                {
                    try
                    {
                        byte[]? result = await RecoverFromPeer(peer.SyncPeer, codeHash, cts.Token);
                        if (result is not null) return result;

                        if (_logger.IsDebug) _logger.Debug($"Mark peer {peer} weak");
                        peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Error recovering code from {peer} {ex}");
                        peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                    }
                    return null;
                }, SnapPeerStrategy, AllocationContexts.Snap, cts.Token));
            }

            byte[]? recovered = await Wait.AnyWhere(static result => result is not null, concurrentAttempts);
            if (recovered is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Failed to recover code {codeHash}");
            }

            return recovered;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Recovery is best effort and the caller blocks on it while reading code, so degrade to a
            // miss and let the caller report the missing code rather than this failure.
            if (_logger.IsWarn) _logger.Warn($"Error recovering code {codeHash} {ex}");
            return null;
        }
    }

    private async Task<byte[]?> RecoverFromPeer(ISyncPeer peer, ValueHash256 codeHash, CancellationToken token)
    {
        if (!peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer? snapProtocol)) return null;

        using ArrayPoolList<ValueHash256> hashes = new(1) { codeHash };

        using IByteArrayList? result = await snapProtocol.GetByteCodes(hashes, token);
        if (result is not { Count: 1 } || ValueKeccak.Compute(result[0]) != codeHash) return null;

        if (_logger.IsTrace) _logger.Trace($"Fetched code {codeHash} from {peer}");

        return result[0].ToArray();
    }
}
