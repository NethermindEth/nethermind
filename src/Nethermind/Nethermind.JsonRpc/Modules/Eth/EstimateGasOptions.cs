// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Modules.Eth;

/// <summary>Non-standard options of the <c>eth_estimateGas</c> JSON-RPC method, passed as its fifth parameter.</summary>
public class EstimateGasOptions
{
    /// <summary>Whether the estimate keeps searching when a nested call runs out of gas at the lowest gas limit that succeeds.</summary>
    /// <remarks>
    /// The result is then the lowest gas limit at which no nested call runs out of gas, if the highest gas limit of the
    /// search has none. The second search is a bisection between the first result and the highest gas limit, so it runs
    /// the transaction more times. Under EIP-8037 the result can be above the transaction gas limit cap, where a state-gas
    /// reservoir pays the state creation of a nested call with a fixed gas budget.
    /// </remarks>
    public bool AvoidNestedOutOfGas { get; set; }
}
