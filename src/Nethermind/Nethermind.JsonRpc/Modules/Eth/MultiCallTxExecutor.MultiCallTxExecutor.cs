// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Facade;
using Nethermind.Facade.Proxy.Models.MultiCall;

namespace Nethermind.JsonRpc.Modules.Eth;

public class MultiCallTxExecutor : ExecutorBase<MultiCallBlockResult[], MultiCallPayload, MultiCallPayload>
{
    public MultiCallTxExecutor(IBlockchainBridge blockchainBridge, IBlockFinder blockFinder, IJsonRpcConfig rpcConfig) :
        base(blockchainBridge, blockFinder, rpcConfig)
    { }

    protected override MultiCallPayload Prepare(MultiCallPayload call)
    {
        return call;
    }

    protected override ResultWrapper<MultiCallBlockResult[]> Execute(BlockHeader header, MultiCallPayload tx, CancellationToken token)
    {
        BlockchainBridge.MultiCallOutput results = _blockchainBridge.MultiCall(header.Clone(), tx, token);

        if (results.Error == null)
        {
            return ResultWrapper<MultiCallBlockResult[]>.Success(results.Items.ToArray());
        }

        return ResultWrapper<MultiCallBlockResult[]>.Fail(results.Error, results.Items.ToArray());

    }
}


