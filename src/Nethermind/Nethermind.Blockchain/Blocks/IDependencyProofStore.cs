// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Blocks;

/// <summary>Stores EIP-8141 <c>dependency_proof</c>s by block number and hash, apart from the block bodies.</summary>
/// <remarks>
/// A proof is kept for as long as its block body: full validation needs it and it cannot be re-derived.
/// A missing proof for a block whose header commits to one means the data is not available, not that the block is invalid.
/// </remarks>
public interface IDependencyProofStore
{
    void Insert(ulong blockNumber, Hash256 blockHash, byte[] proof);

    /// <summary>Defers the write off the block-suggestion path; falls back to <see cref="Insert"/> when deferral is disabled.</summary>
    void InsertDeferred(ulong blockNumber, Hash256 blockHash, byte[] proof) => Insert(blockNumber, blockHash, proof);

    byte[]? Get(ulong blockNumber, Hash256 blockHash);

    void Delete(ulong blockNumber, Hash256 blockHash);

    /// <summary>Drops every proof in <c>[fromInclusive, toExclusive)</c> in one operation.</summary>
    void DeleteRange(ulong fromInclusive, ulong toExclusive);
}

public static class DependencyProofStoreExtensions
{
    /// <summary>Returns <paramref name="block"/> carrying the stored proof its header commits to.</summary>
    /// <returns>A copy carrying the stored proof, leaving the block store's cached instance without it; otherwise
    /// <paramref name="block"/> itself, which lacks a committed proof only when it is not available.</returns>
    public static Block WithProof(this IDependencyProofStore proofStore, Block block)
    {
        if (block.DependencyProof is not null || block.Header.DependencyProofHash is null || block.Hash is null
            || proofStore.Get(block.Number, block.Hash) is not { } proof)
        {
            return block;
        }

        Block withProof = block.WithReplacedBody(block.Body);
        withProof.DependencyProof = proof;
        // The proof is not part of the block's encoding, so the stored size stands.
        withProof.EncodedSize = block.EncodedSize;
        return withProof;
    }
}
