// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

/// <summary>Writes the EIP-8288 <c>dependency_proof</c> a block arrives with to <see cref="IDependencyProofStore"/>, keeping it out of the block cache.</summary>
/// <remarks>
/// Blocks read back carry no proof, so cached blocks stay small. The proof is written before the body and deleted after it,
/// so an interrupted write or delete leaves an unread proof without a body rather than a body without its proof.
/// </remarks>
public sealed class DependencyProofSplittingBlockStore(IBlockStore blockStore, IDependencyProofStore proofStore) : IBlockStore, IClearableCache
{
    public void Insert(Block block, WriteFlags writeFlags = WriteFlags.None)
    {
        if (block.StarkProof is { } proof) proofStore.Insert(block.Number, HashOf(block), proof);
        blockStore.Insert(WithoutProof(block), writeFlags);
    }

    public void InsertDeferred(Block block)
    {
        if (block.StarkProof is { } proof) proofStore.InsertDeferred(block.Number, HashOf(block), proof);
        blockStore.InsertDeferred(WithoutProof(block));
    }

    public void Delete(ulong blockNumber, Hash256 blockHash)
    {
        blockStore.Delete(blockNumber, blockHash);
        proofStore.Delete(blockNumber, blockHash);
    }

    public void DeleteRange(ulong fromInclusive, ulong toExclusive)
    {
        blockStore.DeleteRange(fromInclusive, toExclusive);
        proofStore.DeleteRange(fromInclusive, toExclusive);
    }

    public void DeleteRanges(IReadOnlyList<(ulong FromInclusive, ulong ToExclusive)> ranges)
    {
        blockStore.DeleteRanges(ranges);
        foreach ((ulong fromInclusive, ulong toExclusive) in ranges) proofStore.DeleteRange(fromInclusive, toExclusive);
    }

    public Block? Get(ulong blockNumber, Hash256 blockHash, RlpBehaviors rlpBehaviors = RlpBehaviors.None, bool shouldCache = true) =>
        blockStore.Get(blockNumber, blockHash, rlpBehaviors, shouldCache);

    public byte[]? GetRlp(ulong blockNumber, Hash256 blockHash) => blockStore.GetRlp(blockNumber, blockHash);

    public ReceiptRecoveryBlock? GetReceiptRecoveryBlock(ulong blockNumber, Hash256 blockHash) =>
        blockStore.GetReceiptRecoveryBlock(blockNumber, blockHash);

    public void Cache(Block block) => blockStore.Cache(WithoutProof(block));

    public bool HasBlock(ulong blockNumber, Hash256 blockHash) => blockStore.HasBlock(blockNumber, blockHash);

    void IClearableCache.ClearCache() => (blockStore as IClearableCache)?.ClearCache();

    private static Hash256 HashOf(Block block) =>
        block.Hash ?? throw new InvalidOperationException("An attempt to store a block with a null hash.");

    /// <summary>A copy of <paramref name="block"/> without the proof; the caller's block may still be validated.</summary>
    private static Block WithoutProof(Block block) => block.StarkProof is null
        ? block
        : new Block(block.Header, new BlockBody(block.Transactions, block.Uncles, block.Withdrawals))
        {
            EncodedTransactions = block.EncodedTransactions
        };
}
