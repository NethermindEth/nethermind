// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin
{
    public interface IEngineDebugBridge
    {
        ExecutionPayloadForDebugRpc GenerateNewPayload(BlockParameter blockParameter);
        ExecutionPayloadForDebugRpc GenerateNewPayloadWithTransactions(TransactionForRpc[] transactions);
        Hash256 CalculateBlockHash(ExecutionPayload executionPayload);
    }
}
