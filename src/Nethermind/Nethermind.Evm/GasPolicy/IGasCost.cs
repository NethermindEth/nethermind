// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Compile-time descriptor of a fixed opcode gas charge.
/// </summary>
/// <remarks>
/// Implemented by zero-size <c>struct</c> tags and consumed via <c>IGasPolicy.Consume&lt;TCost&gt;</c>.
/// Because the EVM is monomorphized over <c>VirtualMachine&lt;TGasPolicy&gt;</c> and the tag is a type
/// parameter, <see cref="GasCost"/> is a compile-time constant in the specialized method — the charge
/// folds to the same code as a literal <c>cost</c> argument, without the caller passing a number.
/// </remarks>
public interface IGasCost
{
    /// <summary>The gas cost of the charge.</summary>
    static abstract ulong GasCost { get; }
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
}

// Fixed opcode-cost tags; each name maps to the GasCostOf value it charges.
public readonly struct BaseGasCost : IGasCost { public static ulong GasCost => GasCostOf.Base; }
public readonly struct VeryLowGasCost : IGasCost { public static ulong GasCost => GasCostOf.VeryLow; }
public readonly struct LowGasCost : IGasCost { public static ulong GasCost => GasCostOf.Low; }
public readonly struct MidGasCost : IGasCost { public static ulong GasCost => GasCostOf.Mid; }
public readonly struct HighGasCost : IGasCost { public static ulong GasCost => GasCostOf.High; }
public readonly struct JumpGasCost : IGasCost { public static ulong GasCost => GasCostOf.Jump; }
public readonly struct JumpIGasCost : IGasCost { public static ulong GasCost => GasCostOf.JumpI; }
public readonly struct JumpDestGasCost : IGasCost { public static ulong GasCost => GasCostOf.JumpDest; }
public readonly struct TLoadGasCost : IGasCost { public static ulong GasCost => GasCostOf.TLoad; }
public readonly struct TStoreGasCost : IGasCost { public static ulong GasCost => GasCostOf.TStore; }
public readonly struct SelfBalanceGasCost : IGasCost { public static ulong GasCost => GasCostOf.SelfBalance; }
public readonly struct BlockHashGasCost : IGasCost { public static ulong GasCost => GasCostOf.BlockHash; }
public readonly struct BlobHashGasCost : IGasCost { public static ulong GasCost => GasCostOf.BlobHash; }
public readonly struct ExpGasCost : IGasCost { public static ulong GasCost => GasCostOf.Exp; }

// Spec-dependent opcode-cost tags; each reads its cost from the active spec's price book.
public readonly struct SLoadGasCost : ISpecGasCost { public static ulong GasCost(IReleaseSpec spec) => spec.GasCosts.SLoadCost; }
public readonly struct BalanceGasCost : ISpecGasCost { public static ulong GasCost(IReleaseSpec spec) => spec.GasCosts.BalanceCost; }
public readonly struct ExtCodeHashGasCost : ISpecGasCost { public static ulong GasCost(IReleaseSpec spec) => spec.GasCosts.ExtCodeHashCost; }
public readonly struct ExtCodeSizeGasCost : ISpecGasCost { public static ulong GasCost(IReleaseSpec spec) => spec.GasCosts.ExtCodeCost; }
