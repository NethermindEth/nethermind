// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Eth;

public class MultiCallTxExecutor : ExecutorBase<IReadOnlyList<MultiCallBlockResult>, MultiCallPayload<TransactionForRpc>, MultiCallPayload<Transaction>>
{
    public MultiCallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig) :
        base(blockchainBridge, blockFinder, rpcConfig)
    { }

    protected override MultiCallPayload<Transaction> Prepare(MultiCallPayload<TransactionForRpc> call)
    {
        var result = new MultiCallPayload<Transaction>
        {
            TraceTransfers = call.TraceTransfers,
            Validation = call.Validation,
            BlockStateCalls = call.BlockStateCalls?.Select(blockStateCall => new BlockStateCall<Transaction>
            {
                BlockOverrides = blockStateCall.BlockOverrides,
                StateOverrides = blockStateCall.StateOverrides,
                Calls = blockStateCall.Calls?.Select(callTransactionModel =>
                {
                    callTransactionModel.EnsureDefaults(_rpcConfig.GasCap);
                    callTransactionModel.Type ??= TxType.EIP1559;
                    return callTransactionModel.ToTransaction(_blockchainBridge.GetChainId());
                }).ToArray()
            }).ToArray()
        };

        return result;
    }

    protected override ResultWrapper<IReadOnlyList<MultiCallBlockResult>> Execute(BlockHeader header, MultiCallPayload<Transaction> tx, CancellationToken token)
    {
        MultiCallOutput results = _blockchainBridge.MultiCall(header.Clone(), tx, token);

        if (results.Error == null)
        {
            return ResultWrapper<IReadOnlyList<MultiCallBlockResult>>.Success(results.Items);
        }

        return ResultWrapper<IReadOnlyList<MultiCallBlockResult>>.Fail(results.Error, results.Items);

    }
}


