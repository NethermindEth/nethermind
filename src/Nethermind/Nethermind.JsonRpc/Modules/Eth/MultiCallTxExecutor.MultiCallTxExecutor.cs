// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Int256;

namespace Nethermind.JsonRpc.Modules.Eth;
public class MultiCallTxExecutor
{
    private readonly IDbProvider _dbProvider;
    private readonly IBlockchainBridge _blockchainBridge;
    private readonly IBlockFinder _blockFinder;
    private readonly IJsonRpcConfig _rpcConfig;
    private readonly ISpecProvider _specProvider;

    public MultiCallTxExecutor(IDbProvider DbProvider,
        IBlockchainBridge blockchainBridge,
        IBlockFinder blockFinder,
        ISpecProvider specProvider,
        IJsonRpcConfig rpcConfig)
    {
        _dbProvider = DbProvider;
        _blockchainBridge = blockchainBridge;
        _blockFinder = blockFinder;
        _specProvider = specProvider;
        _rpcConfig = rpcConfig;
    }

    private UInt256 MaxGas => GetMaxGas(_rpcConfig);

    public static UInt256 GetMaxGas(IJsonRpcConfig config)
    {
        return (UInt256)config.GasCap * (UInt256)config.GasCapMultiplier;
    }

    public ResultWrapper<MultiCallBlockResult[]> Execute(ulong version,
        MultiCallBlockStateCallsModel[] blockCallsToProcess,
        BlockParameter? blockParameter, bool traceTransfers)
    {
        SearchResult<BlockHeader> searchResult = _blockFinder.SearchForHeader(blockParameter);
        if (searchResult.IsError) return ResultWrapper<MultiCallBlockResult[]>.Fail(searchResult);

        BlockHeader header = searchResult.Object;
        if (!EthRpcModule.HasStateForBlock(_blockchainBridge, header))
        {
            return ResultWrapper<MultiCallBlockResult[]>.Fail($"No state available for block {header.Hash}",
                ErrorCodes.ResourceUnavailable);
        }

        using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);

        List<MultiCallBlockResult> results = _blockchainBridge.MultiCall(header.Clone(),
            blockCallsToProcess,
            cancellationTokenSource.Token);

        return ResultWrapper<MultiCallBlockResult[]>.Success(results.ToArray());
    }
}
