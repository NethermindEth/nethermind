// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin;

[RpcModule(ModuleType.Debug)]
public interface IDebugRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Return a valid ExecutionPayload from a Block")]
    ResultWrapper<ExecutionPayloadForDebugRpc> debug_generateNewPayload(BlockParameter blockParameter);

    [JsonRpcMethod(Description = "Return a valid ExecutionPayload from a range of block")]
    ResultWrapper<ExecutionPayloadForDebugRpc> debug_generateNewPayload(BlockParameter blockParameterStart, BlockParameter blockParameterEnd);

    [JsonRpcMethod(Description = "Return a valid ExecutionPayload from a Block with the given transactions")]
    ResultWrapper<ExecutionPayloadForDebugRpc> debug_generateNewPayloadWithTransactions(TransactionForRpc[] transactions);

    [JsonRpcMethod(Description = "Calculate the block hash for the given ExecutionPayload")]
    ResultWrapper<Hash256> debug_calculateBlockHash(ExecutionPayload executionPayload);
}
