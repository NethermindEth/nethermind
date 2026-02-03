// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Facade;

namespace Nethermind.JsonRpc.Modules.Eth;

public abstract class ExecutorBase<TResult, TRequest, TProcessing>(
    IBlockchainBridge blockchainBridge,
    IBlockFinder blockFinder,
    IJsonRpcConfig rpcConfig)
{
    protected readonly IBlockchainBridge _blockchainBridge = blockchainBridge;
    protected readonly IBlockFinder _blockFinder = blockFinder;
    protected readonly IJsonRpcConfig _rpcConfig = rpcConfig;

    public virtual ResultWrapper<TResult> Execute(
        TRequest call,
        BlockParameter? blockParameter,
        Dictionary<Address, AccountOverride>? stateOverride = null,
        SearchResult<BlockHeader>? searchResult = null)
    {
        searchResult ??= _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.Value.IsError) return ResultWrapper<TResult>.Fail(searchResult.Value);

        BlockHeader header = searchResult.Value.Object!;
        if (!_blockchainBridge.HasStateForBlock(header))
        {
            return ResultWrapper<TResult>.Fail($"No state available for block {header.ToString(BlockHeader.Format.FullHashAndNumber)}", ErrorCodes.ResourceUnavailable);
        }

        using CancellationTokenSource timeout = _rpcConfig.BuildTimeoutCancellationToken();
        Result<TProcessing> prepareResult = Prepare(call);
        return !prepareResult ? ResultWrapper<TResult>.Fail(prepareResult.Error!, ErrorCodes.InvalidInput) : Execute(header.Clone(), prepareResult.Data!, stateOverride, timeout.Token);
    }

    protected abstract Result<TProcessing> Prepare(TRequest call);

    protected abstract ResultWrapper<TResult> Execute(BlockHeader header, TProcessing tx, Dictionary<Address, AccountOverride>? stateOverride, CancellationToken token);

    protected ResultWrapper<TResult>? TryGetInputError(CallOutput result)
    {
        return result.InputError ? ResultWrapper<TResult>.Fail(result.Error!, ErrorCodes.InvalidInput) : null;
    }
}
