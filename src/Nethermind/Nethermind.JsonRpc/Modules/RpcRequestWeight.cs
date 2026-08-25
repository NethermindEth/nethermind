// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.JsonRpc.Modules;

/// <summary>
/// Estimates how many "unit" requests a JSON-RPC request is worth for admission purposes.
/// </summary>
/// <remarks>
/// Only EVM-execution requests carrying state overrides weigh more than one unit: the override payload (injected
/// code plus storage slots) is the best pre-execution proxy for how much work a simulation will do, and heavy
/// multicall simulations otherwise get admitted as if they were an <c>eth_getBalance</c>. The weight is clamped to
/// <see cref="MaxWeight"/> so a single pathological request cannot starve the queue for everybody else.
/// </remarks>
internal static class RpcRequestWeight
{
    public const int MinWeight = 1;
    public const int MaxWeight = 8;
    private const int BytesPerStorageSlot = 64;
    private const int BytesPerWeightUnit = 64 * 1024;

    public static int Estimate(ResolvedMethodInfo method, object?[]? parameters, int parameterCount)
    {
        if (method.CostClass != RpcMethodCostClass.EvmExecution || parameters is null)
        {
            return MinWeight;
        }

        long overrideBytes = 0;
        for (int i = 0; i < parameterCount; i++)
        {
            switch (parameters[i])
            {
                case Dictionary<Address, AccountOverride> stateOverride:
                    overrideBytes += MeasureOverrides(stateOverride);
                    break;
                case SimulatePayload<TransactionForRpc> { BlockStateCalls: { } blockStateCalls }:
                    foreach (BlockStateCall<TransactionForRpc> blockStateCall in blockStateCalls)
                    {
                        if (blockStateCall.StateOverrides is { } blockOverrides)
                        {
                            overrideBytes += MeasureOverrides(blockOverrides);
                        }
                    }
                    break;
            }
        }

        long weight = MinWeight + overrideBytes / BytesPerWeightUnit;
        return weight > MaxWeight ? MaxWeight : (int)weight;
    }

    private static long MeasureOverrides(Dictionary<Address, AccountOverride> overrides)
    {
        long bytes = 0;
        foreach (AccountOverride accountOverride in overrides.Values)
        {
            bytes += accountOverride.Code?.Length ?? 0;
            bytes += (long)BytesPerStorageSlot * ((accountOverride.State?.Count ?? 0) + (accountOverride.StateDiff?.Count ?? 0));
        }

        return bytes;
    }
}
