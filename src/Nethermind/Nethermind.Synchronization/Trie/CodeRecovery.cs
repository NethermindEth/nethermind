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

/// <summary>
/// Fetches missing bytecode over the snap <c>GetByteCodes</c> protocol.
/// </summary>
/// <remarks>
/// Races a few peers against each other and takes the first response that hashes to the requested code hash,
/// so a peer that serves nothing or serves the wrong bytes only loses its own attempt.
/// </remarks>
public class CodeRecovery(ISyncPeerPool peerPool, ILogManager logManager, INodeStatsManager? nodeStatsManager) : ICodeRecovery
{
    /// <summary>Creates bytecode recovery without transfer speed reporting.</summary>
    public CodeRecovery(ISyncPeerPool peerPool, ILogManager logManager) : this(peerPool, logManager, null)
    {
    }

    private sealed record RecoveryResult(PeerInfo Peer, byte[] Code, long ElapsedMilliseconds);

    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

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

    /// <inheritdoc/>
    public async Task<byte[]?> Recover(ValueHash256 codeHash, CancellationToken cancellationToken = default)
    {
        using AutoCancelTokenSource cts = cancellationToken.CreateChildTokenSource(RecoveryTimeout);
        CancellationToken token = cts.Token;

        if (_logger.IsDebug) _logger.Debug($"Recovering code {codeHash}");

        using ArrayPoolList<Task<RecoveryResult?>> concurrentAttempts = new(ConcurrentAttempt);
        for (int i = 0; i < ConcurrentAttempt; i++)
        {
            concurrentAttempts.Add(Attempt());
        }

        RecoveryResult? recovered = await Wait.AnyWhere(static result => result is not null, concurrentAttempts);
        if (recovered is null && token.IsCancellationRequested) return null;
        if (recovered is not null && recovered.Code.Length > 0)
            nodeStatsManager?.ReportTransferSpeedEvent(recovered.Peer.SyncPeer.Node, TransferSpeedType.SnapRanges,
                recovered.Code.Length / Math.Max(1, recovered.ElapsedMilliseconds));
        if (recovered is null)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to recover code {codeHash}");
        }

        return recovered?.Code;

        // Completes with null rather than faulting. Wait.AnyWhere rethrows the first faulted task and
        // drops the rest, and Allocate/Free throw outside the reach of the inner handler.
        async Task<RecoveryResult?> Attempt()
        {
            try
            {
                return await peerPool.AllocateAndRun(async (PeerInfo peer) =>
                {
                    try
                    {
                        long started = TimeProvider.GetTimestamp();
                        byte[]? result = await RecoverFromPeer(peer.SyncPeer, codeHash, token);
                        if (token.IsCancellationRequested) return null;
                        if (result is not null)
                            return new RecoveryResult(peer, result, (long)TimeProvider.GetElapsedTime(started).TotalMilliseconds);

                        if (_logger.IsDebug) _logger.Debug($"Mark peer {peer:hide} weak");
                        peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Error recovering code from {peer:hide} {ex:hide}");
                        peerPool.ReportWeakPeer(peer, AllocationContexts.Snap);
                    }
                    return null;
                }, SnapPeerStrategy, AllocationContexts.Snap, token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                // Best effort, and the caller blocks on this while reading code.
                if (_logger.IsWarn) _logger.Warn($"Error recovering code {codeHash} {ex}");
                return null;
            }
        }
    }

    private async Task<byte[]?> RecoverFromPeer(ISyncPeer peer, ValueHash256 codeHash, CancellationToken token)
    {
        if (!peer.TryGetSatelliteProtocol(Protocol.Snap, out ISnapSyncPeer? snapProtocol)) return null;

        using ArrayPoolList<ValueHash256> hashes = new(1) { codeHash };

        using IByteArrayList? result = await snapProtocol.GetByteCodes(hashes, token);
        if (result is not { Count: 1 } || ValueKeccak.Compute(result[0]) != codeHash) return null;

        if (_logger.IsTrace) _logger.Trace($"Fetched code {codeHash} from {peer:hide}");

        return result[0].ToArray();
    }
}
