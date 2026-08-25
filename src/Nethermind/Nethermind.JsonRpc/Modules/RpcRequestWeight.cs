// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.JsonRpc.Modules;

/// <summary>
/// Estimates how many "unit" requests a JSON-RPC request is worth for admission purposes.
/// </summary>
/// <remarks>
/// Only EVM-execution requests weigh more than one unit, scaled by the raw size of their <c>params</c>: state
/// overrides (injected code plus storage slots) dominate the payload of heavy simulations and are the best
/// pre-execution proxy for how much work one will do, and heavy multicall simulations otherwise get admitted as if
/// they were an <c>eth_getBalance</c>. The raw byte count is known before anything is deserialized, so a request
/// can be weighed — and shed — without paying for parameter binding; a request parsed into a
/// <see cref="System.Text.Json.JsonDocument"/> rather than sliced from the request body carries no byte count and
/// weighs one unit. The weight is clamped to <see cref="MaxWeight"/> so a single pathological request cannot
/// starve the queue for everybody else.
/// </remarks>
internal static class RpcRequestWeight
{
    public const int MinWeight = 1;
    public const int MaxWeight = 8;
    // Hex-encoded JSON is roughly twice the size of the override bytes it carries.
    public const int BytesPerWeightUnit = 128 * 1024;

    public static int Estimate(ResolvedMethodInfo method, int paramsUtf8Length)
    {
        if (method.CostClass != RpcMethodCostClass.EvmExecution)
        {
            return MinWeight;
        }

        int weight = MinWeight + paramsUtf8Length / BytesPerWeightUnit;
        return weight > MaxWeight ? MaxWeight : weight;
    }
}
