// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

/// <summary>
/// Non-generic intrinsic gas result for backward compatibility.
/// </summary>
public readonly record struct EthereumIntrinsicGas(long Standard, long FloorGas)
{
    public long MinimalGas { get; } = Math.Max(Standard, FloorGas);
    public static explicit operator long(EthereumIntrinsicGas gas) => gas.MinimalGas;
    public static implicit operator EthereumIntrinsicGas(IntrinsicGas<EthereumGasPolicy> gas) =>
        new(gas.Standard.Value, gas.FloorGas.Value);
}

public static class IntrinsicGasCalculator
{
    /// <summary>
    /// Calculates intrinsic gas with TGasPolicy type, allowing MultiGas breakdown for Arbitrum.
    /// </summary>
    private static IntrinsicGas<TGasPolicy> Calculate<TGasPolicy>(Transaction transaction, IReleaseSpec releaseSpec)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        TGasPolicy.CalculateIntrinsicGas(transaction, releaseSpec);

    /// <summary>
    /// Non-generic backward-compatible Calculate method.
    /// </summary>
    public static EthereumIntrinsicGas Calculate(Transaction transaction, IReleaseSpec releaseSpec) =>
        Calculate<EthereumGasPolicy>(transaction, releaseSpec);

    public static long AccessListCost(Transaction transaction, IReleaseSpec releaseSpec) =>
        IGasPolicy<EthereumGasPolicy>.AccessListCost(transaction, releaseSpec);
}
