// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Trie;

namespace Nethermind.State.Flat.BlockRangeTrieForest;

/// <summary>
/// Stores trie node RLPs for persisted snapshots whose block-range span exceeds
/// <c>CompactSize</c>. Keyed by (blockRange, halfPath, keccak) so that
/// range-bounded scans are efficient and deletion can be amortized over time.
/// </summary>
public interface IBlockRangeTrieForest
{
    byte[]? TryGetState(long blockRange, in TreePath path, in ValueHash256 hash);
    byte[]? TryGetStorage(long blockRange, in ValueHash256 address, in TreePath path, in ValueHash256 hash);

    IWriter CreateWriter();

    /// <summary>
    /// Returns a cursor for deleting entries whose block-range is strictly below
    /// <paramref name="belowBlockRange"/>, resuming from <paramref name="resumeKey"/>
    /// (empty span to start from the beginning).
    /// </summary>
    IDeletionCursor CreateDeletionCursor(long belowBlockRange, ReadOnlySpan<byte> resumeKey);

    public interface IWriter : IDisposable
    {
        void PutState(long blockRange, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp);
        void PutStorage(long blockRange, in ValueHash256 address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp);
        void Flush();
    }

    public interface IDeletionCursor : IDisposable
    {
        /// <summary>Delete up to <paramref name="count"/> entries. Returns number actually deleted.</summary>
        int DeleteBatch(int count);

        /// <summary>True once all eligible entries have been deleted.</summary>
        bool IsExhausted { get; }

        /// <summary>The raw key of the last successfully deleted entry, for persistence as a cursor.</summary>
        byte[] CurrentKey { get; }
    }
}
