// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Facade.Eth;

/// <summary>Joins the EIP-8288 <c>dependency_proof</c> the block store keeps apart into a full-transaction RPC block model.</summary>
/// <remarks>The proof is not part of the block's encoding, so it does not count towards <c>size</c>. A block whose proof is not
/// available is returned without it: the RPC model has no way to mark it unavailable.</remarks>
public sealed class DependencyProofBlockForRpcFactory(IBlockForRpcFactory blockForRpcFactory, IDependencyProofStore proofStore) : IBlockForRpcFactory
{
    public BlockForRpc Create(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false) =>
        blockForRpcFactory.Create(includeFullTransactionData ? proofStore.WithProof(block) : block,
            includeFullTransactionData, specProvider, skipTxs);

    public BlockHeaderForRpc CreateHeader(BlockHeader header, ISpecProvider? specProvider = null) =>
        blockForRpcFactory.CreateHeader(header, specProvider);
}
