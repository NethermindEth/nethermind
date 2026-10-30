// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.JsonRpc;

namespace Nethermind.TrustedInput;

public sealed class TrustedInputRpcModule(
    TrustedInputStore store,
    IBlockFinder blockFinder,
    IBlockchainBridge blockchainBridge,
    ISpecProvider specProvider) : ITrustedInputRpcModule
{
    public ResultWrapper<byte[]> trustedinput_getInput(BlockParameter blockParameter)
    {
        Block? block = blockFinder.FindBlock(blockParameter);
        if (block?.Hash is null)
            return ResultWrapper<byte[]>.Fail($"Unable to find block {blockParameter}", ErrorCodes.ResourceNotFound);

        if (store.TryGet(block.Hash, out byte[] input))
            return ResultWrapper<byte[]>.Success(input);

        if (block.Number == 0)
            return ResultWrapper<byte[]>.Fail("Cannot generate an input for the genesis block", ErrorCodes.InvalidInput);

        BlockHeader? parent = blockFinder.FindHeader(block.ParentHash!);
        if (parent is null)
            return ResultWrapper<byte[]>.Fail($"Unable to find the parent of block {blockParameter}", ErrorCodes.ResourceNotFound);

        using Witness witness = blockchainBridge.GenerateExecutionWitness(parent, block);
        input = Stateless.Execution.IO.TrustedInput.Write(specProvider.ChainId, block, witness);
        store.Add(block.Number, block.Hash, input);
        return ResultWrapper<byte[]>.Success(input);
    }
}
