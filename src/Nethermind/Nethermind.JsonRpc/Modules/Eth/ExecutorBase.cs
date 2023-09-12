// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade;

namespace Nethermind.JsonRpc.Modules.Eth;

public abstract class ExecutorBase<TResult, TRequest, TProcessing>
{
    protected readonly IBlockchainBridge _blockchainBridge;
    protected readonly IBlockFinder _blockFinder;
    protected readonly IJsonRpcConfig _rpcConfig;

    protected ExecutorBase(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig)
    {
        _blockchainBridge = blockchainBridge;
        _blockFinder = blockFinder;
        _rpcConfig = rpcConfig;
    }

    public virtual ResultWrapper<TResult> Execute(
        TRequest call,
        BlockParameter? blockParameter)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError)
        {
            return ResultWrapper<TResult>.Fail(searchResult);
        }

        BlockHeader header = searchResult.Object;
        if (!_blockchainBridge.HasStateForBlock(header!))
        {
            return ResultWrapper<TResult>.Fail($"No state available for block {header.Hash}",
                ErrorCodes.ResourceUnavailable);
        }

        using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
        TProcessing? toProcess = Prepare(call);
        return Execute(header.Clone(), toProcess, cancellationTokenSource.Token);
    }

    protected abstract TProcessing Prepare(TRequest call);

    protected abstract ResultWrapper<TResult> Execute(BlockHeader header, TProcessing tx, CancellationToken token);

    protected ResultWrapper<TResult> GetInputError(CallOutput result) =>
        ResultWrapper<TResult>.Fail(result.Error, ErrorCodes.InvalidInput);
}
