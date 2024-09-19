// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Taiko.Rpc;

[RpcModule(ModuleType.Engine)]
public interface ITaikoAuthRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Retrieves the transaction pool content with the given upper limits.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<PreBuiltTxList[]?> taikoAuth_txPoolContent(Address beneficiary, UInt256 baseFee, ulong blockMaxGasLimit, ulong maxBytesPerTxList, Address[]? localAccounts, int maxTransactionsLists);
}
