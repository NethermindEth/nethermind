// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Facade.Eth;

/// <summary>Joins the EIP-8288 <c>stark_proof</c> the block store keeps apart into the RPC block model.</summary>
/// <remarks>A hashes-only block reads only the proof's length, which counts towards its <c>size</c>. A block whose proof is not
/// available is returned without it and with its stored <c>size</c>: the RPC model has no way to mark it unavailable.</remarks>
public sealed class StarkProofBlockForRpcFactory(IBlockForRpcFactory blockForRpcFactory, IStarkProofStore proofStore) : IBlockForRpcFactory
{
    public BlockForRpc Create(Block block, bool includeFullTransactionData, ISpecProvider specProvider, bool skipTxs = false) =>
        blockForRpcFactory.Create(includeFullTransactionData ? proofStore.WithProof(block) : proofStore.WithProofSize(block),
            includeFullTransactionData, specProvider, skipTxs);

    public BlockHeaderForRpc CreateHeader(BlockHeader header, ISpecProvider? specProvider = null) =>
        blockForRpcFactory.CreateHeader(header, specProvider);
}
