// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade;
using Nethermind.JsonRpc;
using Nethermind.Stateless.Execution;

namespace Nethermind.TrustedInput;

/// <inheritdoc/>
public sealed class TrustedInputRpcModule(
    TrustedInputStore store,
    IBlockFinder blockFinder,
    IBlockchainBridge blockchainBridge,
    ISpecProvider specProvider) : ITrustedInputRpcModule
{
    private readonly ConcurrentDictionary<Hash256, Lazy<ResultWrapper<byte[]>>> _pending = new();

    /// <inheritdoc/>
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

        Lazy<ResultWrapper<byte[]>> pending = _pending.GetOrAdd(block.Hash,
            _ => new Lazy<ResultWrapper<byte[]>>(() => Generate(parent, block)));
        try
        {
            return pending.Value;
        }
        finally
        {
            _pending.TryRemove(new KeyValuePair<Hash256, Lazy<ResultWrapper<byte[]>>>(block.Hash, pending));
        }
    }

    /// <remarks>The ECRECOVER inputs the hints need are collected as the witness generation executes the block.</remarks>
    private ResultWrapper<byte[]> Generate(BlockHeader parent, Block block)
    {
        if (!store.TryGet(block.Hash!, out byte[] input))
        {
            StatelessExecutor.EcRecoverInputCollector collector = new();
            using Witness witness = blockchainBridge.GenerateExecutionWitness(parent, block, collector);
            input = Stateless.Execution.IO.TrustedInput.Write(specProvider.ChainId, block, witness, collector.Inputs);
            store.Add(block.Number, block.Hash!, input);
        }
        return ResultWrapper<byte[]>.Success(input);
    }
}
