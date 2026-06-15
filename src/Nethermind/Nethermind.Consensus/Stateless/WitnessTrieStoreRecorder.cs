// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Trie;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Per-capture collector for raw trie node RLP touched while a capture is armed. Populated by
/// <see cref="WitnessCapturingTrieStore"/> on every resolved node read; drained by
/// <see cref="WitnessGeneratingWorldState.GetWitness"/> when assembling the witness state nodes.
/// </summary>
public sealed class WitnessTrieStoreRecorder
{
    private readonly ConcurrentDictionary<Hash256AsKey, byte[]> _rlpCollector = new();

    /// <summary>Records an already-materialised node RLP (e.g. from a <c>TryLoadRlp</c> that paid the read).</summary>
    public void Record(Hash256 hash, byte[] rlp) => _rlpCollector.TryAdd(hash, rlp);

    /// <summary>
    /// Records a resolved node, materialising its RLP only on first capture. The static factory avoids
    /// a per-call closure allocation, and <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/> only
    /// invokes it when the key is absent — so repeat reads of the same node (hot in SLOAD loops) skip
    /// the allocate-then-discard of <c>node.FullRlp.ToArray()</c>.
    /// </summary>
    public void Record(Hash256 hash, TrieNode node)
        => _rlpCollector.GetOrAdd(hash, static (_, n) => n.FullRlp.ToArray()!, node);

    public IEnumerable<byte[]> TouchedNodesRlp => _rlpCollector.Select(static kvp => kvp.Value);

    /// <summary>Clears the captured-node set so the recorder can be reused across pooled env rents.</summary>
    public void Reset() => _rlpCollector.Clear();
}
