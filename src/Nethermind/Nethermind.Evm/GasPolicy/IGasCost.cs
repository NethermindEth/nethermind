// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

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

/// <summary>
/// Compile-time descriptor of a spec-dependent fixed opcode gas charge — the cost is read from the
/// active <see cref="IReleaseSpec"/> rather than a literal, but it is still resolved inside the policy.
/// </summary>
/// <remarks>Consumed via <c>IGasPolicy.Consume&lt;TCost&gt;(ref gas, spec)</c>; the spec the opcode
/// already has in hand is forwarded so the cost is computed from the price book without the caller
/// passing a precomputed number.</remarks>
public interface ISpecGasCost
{
    /// <summary>The gas cost of the charge for the given <paramref name="spec"/>.</summary>
    static abstract ulong GasCost(IReleaseSpec spec);

    /// <summary>The dimension the charge is attributed to; computation by default.</summary>
    static virtual MultiGasDimension Dimension => MultiGasDimension.Computation;
}

/// <summary>Computation charge of <see cref="GasCostOf.Base"/>.</summary>
public readonly struct BaseGasCost : IGasCost { public static ulong GasCost => GasCostOf.Base; }

/// <summary>Computation charge of <see cref="GasCostOf.VeryLow"/>.</summary>
public readonly struct VeryLowGasCost : IGasCost { public static ulong GasCost => GasCostOf.VeryLow; }

/// <summary>Computation charge of <see cref="GasCostOf.Low"/>.</summary>
public readonly struct LowGasCost : IGasCost { public static ulong GasCost => GasCostOf.Low; }

/// <summary>Computation charge of <see cref="GasCostOf.Mid"/>.</summary>
public readonly struct MidGasCost : IGasCost { public static ulong GasCost => GasCostOf.Mid; }

/// <summary>Computation charge of <see cref="GasCostOf.High"/>.</summary>
public readonly struct HighGasCost : IGasCost { public static ulong GasCost => GasCostOf.High; }

/// <summary>Computation charge of <see cref="GasCostOf.Jump"/> (JUMP).</summary>
public readonly struct JumpGasCost : IGasCost { public static ulong GasCost => GasCostOf.Jump; }

/// <summary>Computation charge of <see cref="GasCostOf.JumpI"/> (JUMPI).</summary>
public readonly struct JumpIGasCost : IGasCost { public static ulong GasCost => GasCostOf.JumpI; }

/// <summary>Computation charge of <see cref="GasCostOf.JumpDest"/> (JUMPDEST).</summary>
public readonly struct JumpDestGasCost : IGasCost { public static ulong GasCost => GasCostOf.JumpDest; }

/// <summary>Computation charge of <see cref="GasCostOf.TLoad"/> (TLOAD).</summary>
public readonly struct TLoadGasCost : IGasCost { public static ulong GasCost => GasCostOf.TLoad; }

/// <summary>Computation charge of <see cref="GasCostOf.TStore"/> (TSTORE).</summary>
public readonly struct TStoreGasCost : IGasCost { public static ulong GasCost => GasCostOf.TStore; }

/// <summary>Computation charge of <see cref="GasCostOf.SelfBalance"/> (SELFBALANCE).</summary>
public readonly struct SelfBalanceGasCost : IGasCost { public static ulong GasCost => GasCostOf.SelfBalance; }

/// <summary>Computation charge of <see cref="GasCostOf.BlockHash"/> (BLOCKHASH).</summary>
public readonly struct BlockHashGasCost : IGasCost { public static ulong GasCost => GasCostOf.BlockHash; }

/// <summary>Computation charge of <see cref="GasCostOf.BlobHash"/> (BLOBHASH).</summary>
public readonly struct BlobHashGasCost : IGasCost { public static ulong GasCost => GasCostOf.BlobHash; }

/// <summary>Computation charge of <see cref="GasCostOf.Exp"/> (EXP base, before the per-byte cost).</summary>
public readonly struct ExpGasCost : IGasCost { public static ulong GasCost => GasCostOf.Exp; }

/// <summary>Spec-dependent computation charge of <c>spec.GasCosts.SLoadCost</c> (SLOAD base).</summary>
public readonly struct SLoadGasCost : ISpecGasCost { public static ulong GasCost(IReleaseSpec spec) => spec.GasCosts.SLoadCost; }

/// <summary>Spec-dependent computation charge of <c>spec.GasCosts.BalanceCost</c> (BALANCE base).</summary>
public readonly struct BalanceGasCost : ISpecGasCost { public static ulong GasCost(IReleaseSpec spec) => spec.GasCosts.BalanceCost; }

/// <summary>Spec-dependent computation charge of <c>spec.GasCosts.ExtCodeHashCost</c> (EXTCODEHASH base).</summary>
public readonly struct ExtCodeHashGasCost : ISpecGasCost { public static ulong GasCost(IReleaseSpec spec) => spec.GasCosts.ExtCodeHashCost; }

/// <summary>Spec-dependent computation charge of <c>spec.GasCosts.ExtCodeCost</c> (EXTCODESIZE base).</summary>
public readonly struct ExtCodeSizeGasCost : ISpecGasCost { public static ulong GasCost(IReleaseSpec spec) => spec.GasCosts.ExtCodeCost; }
