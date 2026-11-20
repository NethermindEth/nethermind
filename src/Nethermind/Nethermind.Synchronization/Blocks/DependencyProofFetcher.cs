// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Blocks;

/// <summary>Fetches the EIP-8288 dependency proofs of downloaded blocks over EIP-8498 <c>eth/74</c>.</summary>
/// <remarks>
/// Neither headers nor bodies carry the proof, so a block synced from peers has it only if it is fetched separately.
/// A block without dependencies commits to the empty proof, which needs no request. Finality is trusted: a block at or below
/// the finalized block is not asked for, and is validated without its proof.
/// EIP-8498: an empty entry for a block with dependencies means the proof is unavailable, so another peer is asked without
/// penalising the first; a non-empty entry that does not hash to <c>dependency_proof_hash</c> is a breach of protocol.
/// A proof no peer serves leaves the block without one, which validation then skips.
/// </remarks>
public sealed class DependencyProofFetcher(ISyncPeerPool syncPeerPool, IBlockTree blockTree, ILogManager logManager)
{
    /// <summary>Peers asked for a proof, the first being the one that served the bodies.</summary>
    internal const int MaxSources = 3;

    private readonly ILogger _logger = logManager.GetClassLogger<DependencyProofFetcher>();

    /// <summary>Returns the proof of each of <paramref name="headers"/>, aligned with them.</summary>
    /// <returns><c>null</c> for a block before EIP-8288, a finalized block, or one whose proof no peer served.</returns>
    public async Task<byte[]?[]> FetchAsync(PeerInfo source, IReadOnlyList<BlockHeader> headers, CancellationToken cancellationToken)
    {
        byte[]?[] proofs = new byte[]?[headers.Count];
        ulong? finalized = blockTree.FindFinalizedHeader()?.Number;
        List<int> pending = [];
        for (int i = 0; i < headers.Count; i++)
        {
            if (headers[i].RecursiveStark is not { } commitment) continue;
            if (commitment.DependencyProofHash == Keccak.OfAnEmptyString) proofs[i] = [];
            else if (finalized is null || headers[i].Number > finalized) pending.Add(i);
        }

        if (pending.Count == 0) return proofs;

        ulong highest = headers[pending[^1]].Number;
        int asked = 0;
        foreach (PeerInfo peer in Sources(source, highest))
        {
            if (pending.Count == 0 || asked++ == MaxSources || cancellationToken.IsCancellationRequested) break;
            await Request(peer, headers, pending, proofs, cancellationToken);
        }

        if (pending.Count > 0 && _logger.IsDebug)
            _logger.Debug($"No peer served the dependency proofs of {pending.Count} blocks up to {highest}; they are validated without them.");
        return proofs;
    }

    private IEnumerable<PeerInfo> Sources(PeerInfo source, ulong highest)
    {
        if (source.SyncPeer.SupportsDependencyProofs()) yield return source;
        foreach (PeerInfo peer in syncPeerPool.InitializedPeers)
        {
            if (peer != source && peer.SyncPeer.SupportsDependencyProofs() && peer.HeadNumber >= highest) yield return peer;
        }
    }

    private async Task Request(PeerInfo peer, IReadOnlyList<BlockHeader> headers, List<int> pending, byte[]?[] proofs, CancellationToken cancellationToken)
    {
        using ArrayPoolList<Hash256> hashes = new(pending.Count);
        foreach (int i in pending) hashes.Add(headers[i].Hash!);

        IOwnedReadOnlyList<byte[]> response;
        try
        {
            response = await peer.SyncPeer.GetDependencyProofs(hashes, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Dependency proof request to {peer} failed: {e.Message}");
            return;
        }

        using (response)
        {
            if (response.Count > pending.Count)
            {
                syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, "more dependency proofs than requested");
                return;
            }

            List<int> unavailable = [];
            for (int j = 0; j < pending.Count; j++)
            {
                int i = pending[j];
                byte[]? proof = j < response.Count ? response[j] : null;
                if (proof is null or { Length: 0 })
                {
                    unavailable.Add(i);
                    continue;
                }

                if (Keccak.Compute(proof) != headers[i].RecursiveStark!.DependencyProofHash)
                {
                    syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, "dependency proof does not match its header");
                    for (int k = j; k < pending.Count; k++) unavailable.Add(pending[k]);
                    break;
                }

                proofs[i] = proof;
            }

            pending.Clear();
            pending.AddRange(unavailable);
        }
    }
}
