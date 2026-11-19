// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Facade.Eth;

/// <summary>Joins the EIP-8288 <c>stark_proof</c> the block store keeps apart into the RPC block model.</summary>
/// <remarks>Joined for hashes-only blocks too, as the proof counts towards the block's <c>size</c>.</remarks>
public sealed class StarkProofBlockForRpcFactory(IBlockForRpcFactory blockForRpcFactory, IStarkProofStore proofStore) : IBlockForRpcFactory
{
    public BlockForRpc Create(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false) =>
        blockForRpcFactory.Create(proofStore.WithProof(block), includeFullTransactionData, specProvider, skipTxs);

    public BlockHeaderForRpc CreateHeader(BlockHeader header, ISpecProvider? specProvider = null) =>
        blockForRpcFactory.CreateHeader(header, specProvider);
}
