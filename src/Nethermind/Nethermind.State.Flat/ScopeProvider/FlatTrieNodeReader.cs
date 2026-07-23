// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Trie;
using Nethermind.Trie.Sparse;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Committed-only parent-node source for one sparse trie: the state trie when
/// <paramref name="address"/> is null, otherwise the storage trie of that account.
/// </summary>
/// <remarks>
/// Resolution preserves request/result association. Memory tiers (transient resource, global
/// trie-node cache, in-memory snapshots) are keyed or checked by the expected hash and return
/// the node's retained RLP without rehashing it — they rely on the sealed
/// <see cref="TrieNode"/> Keccak/FullRlp pair being consistent, the same invariant every
/// Patricia read and commit walk over these tiers already depends on. Persisted tiers are
/// path-keyed with no such pairing, so the loaded bytes are keccak-validated before being
/// exposed, throwing <see cref="NodeHashMismatchException"/> when base persistence disagrees.
/// The path-keyed current-scope dirty map is never consulted, so a dirty node cannot shadow the
/// committed node at its path; the tiers that are consulted only ever satisfy a request with
/// the exact bytes the hash names, which makes a same-scope entry (e.g. from a diagnostic
/// Patricia run) harmless. Requests are resolved scalarly; batching against persistence is a
/// separate, benchmark-gated step. No <see cref="ReadOnlySnapshotBundle"/> lease is taken, so
/// the reader must be driven synchronously within the owning scope's lifetime; driving it from
/// a thread that can race scope disposal would need the trie warmer's lease treatment.
/// </remarks>
internal sealed class FlatTrieNodeReader(SnapshotBundle bundle, Hash256? address) : ISparseTrieNodeSource
{
    public void Resolve(ReadOnlySpan<SparseNodeRequest> requests, Span<CappedArray<byte>> results)
    {
        for (int i = 0; i < requests.Length; i++)
        {
            results[i] = address is null
                ? bundle.GetCommittedStateNodeRlp(in requests[i].Path, in requests[i].Hash)
                : bundle.GetCommittedStorageNodeRlp(address, in requests[i].Path, in requests[i].Hash);
            Debug.Assert(
                results[i].IsNull || ValueKeccak.Compute(results[i].AsSpan()) == requests[i].Hash,
                "Committed tier served bytes that do not hash to the requested node hash");
        }
    }
}
