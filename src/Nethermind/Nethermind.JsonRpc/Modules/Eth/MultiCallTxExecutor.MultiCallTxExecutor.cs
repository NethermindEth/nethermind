// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Facade;
using Nethermind.Facade.Simulate;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.JsonRpc.Data;
using static Microsoft.FSharp.Core.ByRefKinds;

namespace Nethermind.JsonRpc.Modules.Eth;

public class SimulateTxExecutor : ExecutorBase<IReadOnlyList<SimulateBlockResult>, SimulatePayload<TransactionForRpc>, SimulatePayload<TransactionWithSourceDetails>>
{
    private long gasCapBudget;

    public SimulateTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig) :
        base(blockchainBridge, blockFinder, rpcConfig)
    {
        gasCapBudget = rpcConfig.GasCap ?? long.MaxValue;
    }

    protected override SimulatePayload<TransactionWithSourceDetails> Prepare(SimulatePayload<TransactionForRpc> call)
    {
        SimulatePayload<TransactionWithSourceDetails>? result = new()
        {
            TraceTransfers = call.TraceTransfers,
            Validation = call.Validation,
            BlockStateCalls = call.BlockStateCalls?.Select(blockStateCall =>
            {
                if (blockStateCall.BlockOverrides?.GasLimit != null)
                {
                    blockStateCall.BlockOverrides.GasLimit = (ulong)Math.Min((long)blockStateCall.BlockOverrides.GasLimit!.Value, gasCapBudget);
                }

                return new BlockStateCall<TransactionWithSourceDetails>
                {
                    BlockOverrides = blockStateCall.BlockOverrides,
                    StateOverrides = blockStateCall.StateOverrides,
                    Calls = blockStateCall.Calls?.Select(callTransactionModel =>
                    {
                        if (callTransactionModel.Type == TxType.Legacy)
                        {
                            callTransactionModel.Type = TxType.EIP1559;
                        }

                        bool hadGasLimitInRequest = callTransactionModel.Gas.HasValue;
                        bool hadNonceInRequest = callTransactionModel.Nonce.HasValue;
                        callTransactionModel.EnsureDefaults(gasCapBudget);
                        gasCapBudget -= callTransactionModel.Gas!.Value;

                        var tx = callTransactionModel.ToTransaction(_blockchainBridge.GetChainId());

                        TransactionWithSourceDetails? result = new()
                        {
                            HadGasLimitInRequest = hadGasLimitInRequest,
                            HadNonceInRequest = hadNonceInRequest,
                            Transaction = tx
                        };

                        return result;
                    }).ToArray()
                };
            }).ToArray()
        };

        return result;
    }
    public override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(
        SimulatePayload<TransactionForRpc> call,
        BlockParameter? blockParameter)
    {
        SearchResult<Block> searchResult = _blockFinder.SearchForBlock(blockParameter);

        if (searchResult.IsError || searchResult.Object == null)
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(searchResult);
        }

        BlockHeader header = searchResult.Object.Header;

        if (blockParameter.Type == BlockParameterType.Latest)
        {
            header = _blockFinder.FindLatestBlock().Header;
        }

        if (!_blockchainBridge.HasStateForBlock(header!))
        {
            return ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail($"No state available for block {header.Hash}", ErrorCodes.ResourceUnavailable);
        }

        using CancellationTokenSource cancellationTokenSource = new(_rpcConfig.Timeout);
        SimulatePayload<TransactionWithSourceDetails>? toProcess = Prepare(call);
        return Execute(header.Clone(), toProcess, cancellationTokenSource.Token);
    }

    protected override ResultWrapper<IReadOnlyList<SimulateBlockResult>> Execute(BlockHeader header, SimulatePayload<TransactionWithSourceDetails> tx, CancellationToken token)
    {
        SimulateOutput results = _blockchainBridge.Simulate(header, tx, token);

        return results.Error is null
            ? ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Success(results.Items)
            : ResultWrapper<IReadOnlyList<SimulateBlockResult>>.Fail(results.Error, results.Items);
    }
}


