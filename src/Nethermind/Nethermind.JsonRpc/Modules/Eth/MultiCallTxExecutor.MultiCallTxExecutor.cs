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
using Nethermind.Facade.Multicall;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.JsonRpc.Data;
using static Microsoft.FSharp.Core.ByRefKinds;

namespace Nethermind.JsonRpc.Modules.Eth;

public class MultiCallTxExecutor : ExecutorBase<IReadOnlyList<MultiCallBlockResult>, MultiCallPayload<TransactionForRpc>, MultiCallPayload<TransactionWithSourceDetails>>
{
    private long gasCapBudget;

    public MultiCallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig) :
        base(blockchainBridge, blockFinder, rpcConfig)
    {
        gasCapBudget = rpcConfig.GasCap ?? long.MaxValue;
    }

    protected override MultiCallPayload<TransactionWithSourceDetails> Prepare(MultiCallPayload<TransactionForRpc> call)
    {
        MultiCallPayload<TransactionWithSourceDetails>? result = new()
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

    protected override ResultWrapper<IReadOnlyList<MultiCallBlockResult>> Execute(BlockHeader header, MultiCallPayload<TransactionWithSourceDetails> tx, CancellationToken token)
    {
        MultiCallOutput results = _blockchainBridge.MultiCall(header, tx, token);

        return results.Error is null
            ? ResultWrapper<IReadOnlyList<MultiCallBlockResult>>.Success(results.Items)
            : ResultWrapper<IReadOnlyList<MultiCallBlockResult>>.Fail(results.Error, results.Items);
    }
}


