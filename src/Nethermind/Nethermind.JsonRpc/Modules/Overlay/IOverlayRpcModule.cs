// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Facade.Filters;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.Overlay;

[RpcModule(ModuleType.Overlay)]
public interface IOverlayRpcModule : IRpcModule
{
    [JsonRpcMethod(IsImplemented = true,
        ResultCanBeNull = true,
        IsSharable = false,
        Description = "Re-runs the transaction that deployed the contract at `address` with `code` as its creation code and returns the runtime code that the constructor gives. Pass the result as a `code` state override to `overlay_getLogs` or `eth_call`. Needs the state of the parent of the creation block and of the blocks after it. If a contract deployed it, the code runs as a call from that contract: `ORIGIN` is that contract, and the transactions before it in the block also see the code at `address` and, for a constructor that gets ETH, the value added to the balance of that contract. An address whose code was removed and deployed again can give an earlier deployment.",
        ExampleResponse = "{\"code\":\"0x6080604052600436106100295760003560e01c8063\"}")]
    ResultWrapper<OverlayCreationCode?> overlay_callConstructor(
        [JsonRpcParameter(ExampleValue = "\"0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2\"")] Address address,
        [JsonRpcParameter(Description = "The creation code (init code with constructor arguments) to deploy in place of the original one.")] byte[] code);

    [JsonRpcMethod(IsImplemented = true,
        IsSharable = false,
        Description = "Re-executes the blocks in the filter range with the state overrides applied to the state before each block and returns the logs that match the filter, as `eth_getLogs` does. A block hash can select a block that is not canonical only when the range is that one block. Needs the state of each block in the range.")]
    ResultWrapper<IEnumerable<FilterLog>> overlay_getLogs(
        Filter filter,
        [JsonRpcParameter(Description = "The state overrides, as for `eth_call`, applied before each block in the range.")] Dictionary<Address, AccountOverride>? stateOverride = null);
}
