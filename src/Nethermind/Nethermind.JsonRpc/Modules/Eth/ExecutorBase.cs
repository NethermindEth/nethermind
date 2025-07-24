// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Evm;
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
        BlockParameter? blockParameter,
        Dictionary<Address, AccountOverride>? stateOverride = null,
        SearchResult<BlockHeader>? searchResult = null)
    {
        searchResult ??= _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.Value.IsError) return ResultWrapper<TResult>.Fail(searchResult.Value);

        BlockHeader header = searchResult.Value.Object;
        if (!_blockchainBridge.HasStateForBlock(header!))
            return ResultWrapper<TResult>.Fail($"No state available for block {header.Hash}",
                ErrorCodes.ResourceUnavailable);

        using CancellationTokenSource timeout = _rpcConfig.BuildTimeoutCancellationToken();
        TProcessing? toProcess = Prepare(call);
        return Execute(header.Clone(), toProcess, stateOverride, timeout.Token);
    }

    protected abstract TProcessing Prepare(TRequest call);

    protected abstract ResultWrapper<TResult> Execute(BlockHeader header, TProcessing tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token);

    protected ResultWrapper<TResult>? TryGetInputError(CallOutput result)
    {
        return result.InputError ? ResultWrapper<TResult>.Fail(result.Error!, ErrorCodes.InvalidInput) : null;
    }
}
