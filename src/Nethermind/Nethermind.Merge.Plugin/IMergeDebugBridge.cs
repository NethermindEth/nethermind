// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Merge.Plugin
{
    public interface IMergeDebugBridge
    {
        ExecutionPayloadForRpc GenerateNewPayload(BlockParameter blockParameter);
        ExecutionPayloadForRpc GenerateNewPayloadWithTransactions(TransactionForRpc[] transactions);
    }
}
