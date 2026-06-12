// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Stateless;

/// <summary>
/// Per-capture collector for raw trie node RLP touched while a capture is armed. Populated by
/// <see cref="WitnessCapturingTrieStore"/> on every resolved node read; drained by
/// <see cref="WitnessGeneratingWorldState.GetWitness"/> when assembling the witness state nodes.
/// </summary>
public sealed class WitnessTrieStoreRecorder
{
    private readonly ConcurrentDictionary<Hash256AsKey, byte[]> _rlpCollector = new();

    public void Record(Hash256 hash, byte[] rlp) => _rlpCollector.TryAdd(hash, rlp);

    public IEnumerable<byte[]> TouchedNodesRlp => _rlpCollector.Select(static kvp => kvp.Value);
}
