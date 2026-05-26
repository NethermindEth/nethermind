// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Output of <see cref="MultiProofReader"/>: a set of decoded trie nodes forming Merkle proofs
/// for the requested keys. Account nodes come from the state trie; storage nodes come from
/// per-account storage tries.
/// </summary>
public sealed class DecodedMultiProof
{
    public List<ProofNode> AccountNodes { get; } = [];

    /// <summary>Storage proof nodes keyed by accountPathHash (keccak(address)).</summary>
    public Dictionary<Hash256, List<ProofNode>> StorageNodes { get; } = [];

    public bool IsEmpty => AccountNodes.Count == 0 && StorageNodes.Count == 0;
}
