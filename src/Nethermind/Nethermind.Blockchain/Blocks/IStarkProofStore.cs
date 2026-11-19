// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Blocks;

/// <summary>Stores EIP-8288 block-body <c>stark_proof</c>s by block number and hash, apart from the block bodies.</summary>
/// <remarks>
/// A proof is kept for as long as its block body: full validation needs it and it cannot be re-derived.
/// A missing proof for a block whose header commits to one means the data is not available, not that the block is invalid.
/// </remarks>
public interface IStarkProofStore
{
    void Insert(ulong blockNumber, Hash256 blockHash, byte[] proof);

    /// <summary>Defers the write off the block-suggestion path; falls back to <see cref="Insert"/> when deferral is disabled.</summary>
    void InsertDeferred(ulong blockNumber, Hash256 blockHash, byte[] proof) => Insert(blockNumber, blockHash, proof);

    byte[]? Get(ulong blockNumber, Hash256 blockHash);

    /// <summary>The length of the stored proof's RLP encoding, read without copying the proof.</summary>
    /// <returns><c>null</c> when no proof is stored.</returns>
    int? GetEncodedLength(ulong blockNumber, Hash256 blockHash) => Get(blockNumber, blockHash) is { } proof ? Rlp.LengthOf(proof) : null;
    void Delete(ulong blockNumber, Hash256 blockHash);

    /// <summary>Drops every proof in <c>[fromInclusive, toExclusive)</c> in one operation.</summary>
    void DeleteRange(ulong fromInclusive, ulong toExclusive);
}

public static class StarkProofStoreExtensions
{
    /// <summary>Returns <paramref name="block"/> carrying the stored proof its header commits to.</summary>
    /// <returns>A copy carrying the stored proof, leaving the block store's cached instance without it; otherwise
    /// <paramref name="block"/> itself, which lacks a committed proof only when it is not available.</returns>
    public static Block WithProof(this IStarkProofStore proofStore, Block block)
    {
        if (!CommitsToMissingProof(block) || proofStore.Get(block.Number, block.Hash!) is not { } proof)
        {
            return block;
        }

        Block withProof = block.WithReplacedBody(new BlockBody(block.Transactions, block.Uncles, block.Withdrawals, proof));
        // A stored block's size is that of its stored encoding, and a hashes-only block cannot have it recomputed.
        if (block.EncodedSize is { } storedSize) withProof.EncodedSize = SizeWithProof(storedSize, Rlp.LengthOf(proof));
        return withProof;
    }

    /// <summary><see cref="WithProof"/>, or <c>null</c> when the proof the header commits to is not available.</summary>
    /// <remarks>A body without the proof its header commits to is not this block's body, so it must not be served as one.</remarks>
    public static Block? WithAvailableProof(this IStarkProofStore proofStore, Block block)
    {
        block = proofStore.WithProof(block);
        return CommitsToMissingProof(block) ? null : block;
    }

    /// <summary>Returns <paramref name="block"/> with the <see cref="Block.EncodedSize"/> it has with its proof, leaving the proof unread.</summary>
    /// <remarks>For a hashes-only block, whose transactions are not there to recompute the size from. A block without a stored
    /// size, as a cached or pending one is, has its proof joined instead, so the size computed from its body includes it.</remarks>
    public static Block WithProofSize(this IStarkProofStore proofStore, Block block)
    {
        if (!CommitsToMissingProof(block)) return block;
        if (block.EncodedSize is not { } storedSize) return proofStore.WithProof(block);
        if (proofStore.GetEncodedLength(block.Number, block.Hash!) is not { } proofLength) return block;

        Block withSize = block.WithReplacedBody(block.Body);
        withSize.EncodedSize = SizeWithProof(storedSize, proofLength);
        return withSize;
    }

    private static bool CommitsToMissingProof(Block block) =>
        block.StarkProof is null && block.Header.RecursiveStark is not null && block.Hash is not null;

    private static int SizeWithProof(int storedSize, int proofLength)
    {
        int contentLength = storedSize - 1;
        while (Rlp.LengthOfSequence(contentLength) > storedSize) contentLength--;
        return Rlp.LengthOfSequence(contentLength + proofLength);
    }
}
