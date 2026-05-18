// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Synchronization.FastSync;

/// <summary>
/// Locally reconstructs the upper, missing slice of the state and storage tries left behind by
/// snap sync — avoiding the network round-trips of post-snap healing when possible.
/// </summary>
/// <remarks>
/// Implementations expect snap sync to have already committed all leaf-bearing subtrees to the
/// database; they probe path-keyed trie storage downward from the (missing) root and assemble
/// the missing branches/extensions from whatever subtrees are still present. A non-flat (e.g.,
/// hash-keyed) storage backend cannot support this and is expected to return a no-op implementation.
/// </remarks>
public interface ITrieReassembler
{
    /// <summary>
    /// Reassemble the state trie, first rebuilding the storage tries for each entry in
    /// <paramref name="updatedStorageAccounts"/> and updating the corresponding state-leaf
    /// <c>StorageRoot</c> values.
    /// </summary>
    /// <param name="updatedStorageAccounts">Hashed account addresses whose storage trie was touched during
    /// snap sync (snap's <c>UpdatedStorages</c> tracker). Storage tries for these accounts are reassembled
    /// first, and the resulting roots are used to rewrite the corresponding state-leaf entries.</param>
    /// <returns>The reassembled state root, or <see langword="null"/> if reassembly is not supported
    /// or the database has no leaves to start from.</returns>
    Hash256? TryReassemble(IReadOnlyCollection<Hash256> updatedStorageAccounts);
}

/// <summary>
/// No-op reassembler used when the storage backend does not support path-keyed iteration
/// (e.g., the legacy hash-keyed Patricia store). Always returns <see langword="null"/> so callers
/// fall back to the existing healing path.
/// </summary>
public sealed class NoopTrieReassembler : ITrieReassembler
{
    public static readonly NoopTrieReassembler Instance = new();
    private NoopTrieReassembler() { }

    public Hash256? TryReassemble(IReadOnlyCollection<Hash256> updatedStorageAccounts) => null;
}
