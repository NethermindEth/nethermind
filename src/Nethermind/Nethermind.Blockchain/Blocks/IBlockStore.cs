// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

/// <summary>
/// Raw block store. Does not know or care about blockchain or blocktree, only encoding/decoding to kv store.
/// Generally you probably need IBlockTree instead of this.
/// </summary>
public interface IBlockStore
{
    void Insert(Block block, WriteFlags writeFlags = WriteFlags.None);

    /// <summary>Stores the block made of <paramref name="header"/> and an encoded body.</summary>
    /// <param name="body">The encoded body with its sequence prefix, as a block bodies message carries it.</param>
    void Insert(BlockHeader header, ReadOnlySpan<byte> body, WriteFlags writeFlags = WriteFlags.None);

    /// <summary>
    /// Inserts a freshly suggested block, deferring the durable write off the engine API path when
    /// the implementation supports it. Visibility is synchronous: the block is readable through all
    /// store methods immediately, regardless of whether the database write has completed.
    /// </summary>
    void InsertDeferred(Block block) => Insert(block);
    void Delete(ulong blockNumber, Hash256 blockHash);

    /// <summary>Drops every block in <c>[fromInclusive, toExclusive)</c> in one operation, whatever their hashes.</summary>
    void DeleteRange(ulong fromInclusive, ulong toExclusive);

    /// <summary>Drops many ranges in one call, so a store keeping a hash-keyed cache can invalidate it once
    /// instead of once per range. The default loops <see cref="DeleteRange"/>, whose key-reachability caveat applies.</summary>
    void DeleteRanges(IReadOnlyList<(ulong FromInclusive, ulong ToExclusive)> ranges)
    {
        foreach ((ulong fromInclusive, ulong toExclusive) in ranges) DeleteRange(fromInclusive, toExclusive);
    }

    Block? Get(ulong blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = true);
    byte[]? GetRlp(ulong blockNumber, Hash256 blockHash);

    /// <summary>The stored block encoding, in memory that the caller disposes.</summary>
    /// <remarks>Unlike <see cref="GetRlp"/>, the database value need not be copied to a managed array.</remarks>
    MemoryManager<byte>? GetOwnedRlp(ulong blockNumber, Hash256 blockHash) => ArrayMemoryManager.From(GetRlp(blockNumber, blockHash));

    /// <summary>Starts reading stored blocks into memory before the reads that need them.</summary>
    /// <remarks>Returns before the reads complete. A store that cannot read ahead does nothing.</remarks>
    void Prefetch(ReadOnlySpan<(ulong Number, ValueHash256 Hash)> blocks) { }
    ReceiptRecoveryBlock? GetReceiptRecoveryBlock(ulong blockNumber, Hash256 blockHash);
    void Cache(Block block);

    /// <summary>Whether the block is stored, or queued for a write that will store it.</summary>
    /// <remarks>Answers about durability, not readability: a block an implementation can merely serve right now -
    /// one held only in an evictable cache - must report <c>false</c>. Callers decide from this whether a body
    /// still has to be downloaded, and the fast-blocks bodies descent never revisits a block it was told exists,
    /// so a block reported present and later evicted is lost for good.</remarks>
    bool HasBlock(ulong blockNumber, Hash256 blockHash);
}
