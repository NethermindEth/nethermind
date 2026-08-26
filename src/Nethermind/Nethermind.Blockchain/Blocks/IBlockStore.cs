// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
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
    ReceiptRecoveryBlock? GetReceiptRecoveryBlock(ulong blockNumber, Hash256 blockHash);
    void Cache(Block block);
    bool HasBlock(ulong blockNumber, Hash256 blockHash);
}
