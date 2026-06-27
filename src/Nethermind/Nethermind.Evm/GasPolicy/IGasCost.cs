// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Compile-time descriptor of a fixed opcode gas charge: its cost and the dimension it belongs to.
/// </summary>
/// <remarks>
/// Implemented by zero-size <c>struct</c> tags and consumed via <c>IGasPolicy.Consume&lt;TCost&gt;</c>.
/// Because the EVM is monomorphized over <c>VirtualMachine&lt;TGasPolicy&gt;</c> and the tag is a type
/// parameter, <see cref="GasCost"/>/<see cref="Dimension"/> are compile-time constants in the
/// specialized method — the charge folds to the same code as a literal <c>cost</c> argument, while
/// letting the policy categorize the charge (e.g. a multidimensional policy attributes it to a
/// <see cref="MultiGasDimension"/>) without the caller passing a precomputed number.
/// </remarks>
public interface IGasCost
{
    /// <summary>The gas cost of the charge.</summary>
    static abstract ulong GasCost { get; }

    /// <summary>The dimension the charge is attributed to; computation by default.</summary>
    static virtual MultiGasDimension Dimension => MultiGasDimension.Computation;
}

/// <summary>Computation charge of <see cref="GasCostOf.Base"/>.</summary>
public readonly struct BaseGasCost : IGasCost
{
    public static ulong GasCost => GasCostOf.Base;
}

/// <summary>Computation charge of <see cref="GasCostOf.VeryLow"/>.</summary>
public readonly struct VeryLowGasCost : IGasCost
{
    public static ulong GasCost => GasCostOf.VeryLow;
}

/// <summary>Computation charge of <see cref="GasCostOf.Low"/>.</summary>
public readonly struct LowGasCost : IGasCost
{
    public static ulong GasCost => GasCostOf.Low;
}

/// <summary>Computation charge of <see cref="GasCostOf.Mid"/>.</summary>
public readonly struct MidGasCost : IGasCost
{
    public static ulong GasCost => GasCostOf.Mid;
}

/// <summary>Computation charge of <see cref="GasCostOf.High"/>.</summary>
public readonly struct HighGasCost : IGasCost
{
    public static ulong GasCost => GasCostOf.High;
}
