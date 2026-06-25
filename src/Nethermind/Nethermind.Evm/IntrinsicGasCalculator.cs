// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

/// <summary>
/// Non-generic intrinsic gas result for backward compatibility.
/// </summary>
public readonly record struct EthereumIntrinsicGas(ulong Standard, ulong FloorGas)
{
    public ulong MinimalGas { get; } = Math.Max(Standard, FloorGas);
    public static explicit operator ulong(EthereumIntrinsicGas gas) => gas.MinimalGas;
    public static implicit operator EthereumIntrinsicGas(IntrinsicGas<EthereumGasPolicy> gas) =>
        new(gas.Standard.Value + gas.Standard.StateReservoir, gas.FloorGas.Value);
}

public static class IntrinsicGasCalculator
{
    /// <summary>
    /// Calculates intrinsic gas with TGasPolicy type, allowing MultiGas breakdown for Arbitrum.
    /// </summary>
    private static IntrinsicGas<TGasPolicy> Calculate<TGasPolicy>(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit = 0)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        TGasPolicy.CalculateIntrinsicGas(transaction, releaseSpec, blockGasLimit);

    /// <summary>
    /// Non-generic backward-compatible Calculate method.
    /// </summary>
    public static EthereumIntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec, ulong blockGasLimit = 0) =>
        Calculate<EthereumGasPolicy>(transaction, releaseSpec, blockGasLimit);

    public static ulong AccessListCost(Transaction transaction, IReleaseSpec releaseSpec) =>
        IGasPolicy<EthereumGasPolicy>.AccessListCost(transaction, releaseSpec,
            IGasPolicy<EthereumGasPolicy>.CalculateFloorTokensInAccessList(transaction, releaseSpec));
}
