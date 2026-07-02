// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Evm.State;

/// <summary>
/// Optional capability of a world-state scope to defer state-trie updates and root computation across a window of
/// known-canonical blocks. An interior block calls <see cref="BeginDeferredRootBlock"/> before processing and
/// <see cref="CommitDeferred"/> after: its account changes accumulate (last write per account wins) instead of
/// being applied to the state trie, and its snapshot is committed under the block's known header root. The first
/// block committed WITHOUT deferral applies the accumulated changes together with its own — collapsing repeated
/// writes to hot accounts into a single trie update — and recomputes the root, which the caller verifies against
/// the header. Storage tries are unaffected: they commit per block, so per-account storage roots stay exact.
/// </summary>
public interface IDeferredRootScope
{
    /// <summary>Marks the next commit as deferred. Returns false when the scope cannot defer (read-only,
    /// trie-less, or trie-verification mode), in which case the caller must process the block normally.</summary>
    bool BeginDeferredRootBlock();

    /// <summary>Commits the block's snapshot under <paramref name="knownStateRoot"/> (the canonical header root)
    /// without touching the state trie. Ends the per-block deferral started by <see cref="BeginDeferredRootBlock"/>.</summary>
    void CommitDeferred(ulong blockNumber, Hash256 knownStateRoot);
}

/// <summary>
/// World-state-level access to <see cref="IDeferredRootScope"/> for the currently open scope.
/// </summary>
public interface IDeferredRootWorldState
{
    /// <summary>Whether the currently open scope supports deferred roots.</summary>
    bool SupportsDeferredRoots { get; }

    /// <summary>See <see cref="IDeferredRootScope.BeginDeferredRootBlock"/>.</summary>
    bool BeginDeferredRootBlock();

    /// <summary>See <see cref="IDeferredRootScope.CommitDeferred"/>.</summary>
    void CommitTreeDeferred(ulong blockNumber, Hash256 knownStateRoot);
}
