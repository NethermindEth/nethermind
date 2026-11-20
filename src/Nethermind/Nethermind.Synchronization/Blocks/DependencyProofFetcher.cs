// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
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
/// A block without dependencies commits to the empty proof, which needs no request, and a block that
/// <see cref="DependencyProofFinality"/> trusts is not asked for.
/// EIP-8498: an empty entry for a block with dependencies means the proof is unavailable, so another peer is asked without
/// penalising the first; a non-empty entry that does not hash to <c>dependency_proof_hash</c> is a breach of protocol.
/// A response cut short by the soft limit is not unavailability: the rest is asked of the same peer.
/// </remarks>
public sealed class DependencyProofFetcher(ISyncPeerPool syncPeerPool, IBlockTree blockTree, ILogManager logManager)
{
    /// <summary>Hashes per <c>GetDependencyProofs</c>, an implementation-defined limit that serving peers enforce too.</summary>
    public const int MaxProofsPerRequest = 64;

    /// <summary>Peers asked for a proof, the first being the one that served the bodies.</summary>
    internal const int MaxSources = 3;

    private readonly ILogger _logger = logManager.GetClassLogger<DependencyProofFetcher>();

    /// <summary>Returns the proof of each of <paramref name="headers"/>, aligned with them.</summary>
    /// <returns>Per header its proof, <c>null</c> where none is needed or none was served; and whether a needed one was not
    /// served, in which case the block must not be validated yet.</returns>
    public async Task<(byte[]?[] Proofs, bool[] Unavailable)> FetchAsync(PeerInfo source, IReadOnlyList<BlockHeader> headers, CancellationToken cancellationToken)
    {
        byte[]?[] proofs = new byte[]?[headers.Count];
        bool[] unavailable = new bool[headers.Count];
        List<int> pending = [];
        for (int i = 0; i < headers.Count; i++)
        {
            if (headers[i].RecursiveStark is not { } commitment) continue;
            if (commitment.DependencyProofHash == Keccak.OfAnEmptyString) proofs[i] = [];
            else if (!DependencyProofFinality.IsTrusted(blockTree, headers[i])) pending.Add(i);
        }

        if (pending.Count > 0)
        {
            ulong highest = headers[pending[^1]].Number;
            int asked = 0;
            foreach (PeerInfo peer in Sources(source, highest))
            {
                if (pending.Count == 0 || asked++ == MaxSources || cancellationToken.IsCancellationRequested) break;
                pending = await RequestAll(peer, headers, pending, proofs, cancellationToken);
            }

            foreach (int i in pending) unavailable[i] = true;
            if (pending.Count > 0 && _logger.IsDebug)
                _logger.Debug($"No peer served the dependency proofs of {pending.Count} blocks up to {highest}; they are requested again.");
        }

        return (proofs, unavailable);
    }

    private IEnumerable<PeerInfo> Sources(PeerInfo source, ulong highest)
    {
        if (source.SyncPeer.SupportsDependencyProofs()) yield return source;
        foreach (PeerInfo peer in syncPeerPool.InitializedPeers)
        {
            if (peer != source && peer.SyncPeer.SupportsDependencyProofs() && peer.HeadNumber >= highest) yield return peer;
        }
    }

    /// <summary>Asks <paramref name="peer"/> for every pending proof, in chunks, while it makes progress.</summary>
    /// <returns>The blocks still without a proof.</returns>
    private async Task<List<int>> RequestAll(PeerInfo peer, IReadOnlyList<BlockHeader> headers, List<int> pending, byte[]?[] proofs,
        CancellationToken cancellationToken)
    {
        List<int> remaining = [];
        int next = 0;
        while (next < pending.Count && !cancellationToken.IsCancellationRequested)
        {
            int count = Math.Min(MaxProofsPerRequest, pending.Count - next);
            int answered = await Request(peer, headers, pending.GetRange(next, count), proofs, remaining, cancellationToken);
            if (answered <= 0)
            {
                // No progress: a failed, empty or rejected response leaves the rest to other peers.
                for (int k = next; k < pending.Count; k++)
                {
                    int i = pending[k];
                    if (proofs[i] is null && !remaining.Contains(i)) remaining.Add(i);
                }
                break;
            }

            next += answered;
        }

        return remaining;
    }

    /// <returns>How many of <paramref name="chunk"/> the response answered, or -1 for a response that was rejected.</returns>
    private async Task<int> Request(PeerInfo peer, IReadOnlyList<BlockHeader> headers, List<int> chunk, byte[]?[] proofs, List<int> unavailable,
        CancellationToken cancellationToken)
    {
        using ArrayPoolList<Hash256> hashes = new(chunk.Count);
        foreach (int i in chunk) hashes.Add(headers[i].Hash!);

        IOwnedReadOnlyList<byte[]> response;
        try
        {
            response = await peer.SyncPeer.GetDependencyProofs(hashes, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Dependency proof request to {peer} failed: {e.Message}");
            return -1;
        }

        using (response)
        {
            if (response.Count > chunk.Count)
            {
                syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, "more dependency proofs than requested");
                return -1;
            }

            for (int j = 0; j < response.Count; j++)
            {
                int i = chunk[j];
                byte[] proof = response[j];
                if (proof.Length == 0)
                {
                    unavailable.Add(i);
                    continue;
                }

                if (Keccak.Compute(proof) != headers[i].RecursiveStark!.DependencyProofHash)
                {
                    syncPeerPool.ReportBreachOfProtocol(peer, DisconnectReason.ForwardSyncFailed, "dependency proof does not match its header");
                    return -1;
                }

                proofs[i] = proof;
            }

            return response.Count;
        }
    }
}
