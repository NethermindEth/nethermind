// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Generated from the fork graph for the range Osaka..Amsterdam.
// Do not edit by hand: SpecFlagsTests recomputes this and fails with what is wrong.
// To change the range, move the Floor and Max constants in SpecFlagsTests and rerun it.

using System;
using Nethermind.Core.Specs;

namespace Nethermind.Evm;

/// <inheritdoc cref="SpecFlags"/>
internal static partial class SpecFlags
{
    /// <summary>Rules that hold one value across every fork in the range, as constants.</summary>
    /// <remarks>
    /// A constant here folds the branch that reads it, so the handler specialization behind the
    /// untaken side is never compiled. This is the whole point of the file.
    /// </remarks>
    public const bool ConstEip150 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip158 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip2200 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip2929 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip3860 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip6780 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstNetGasMetering = true;

    public static bool Eip150(IReleaseSpec spec) => ConstEip150;

    public static bool Eip158(IReleaseSpec spec) => ConstEip158;

    public static bool Eip2200(IReleaseSpec spec) => ConstEip2200;

    public static bool Eip2929(IReleaseSpec spec) => ConstEip2929;

    public static bool Eip3860(IReleaseSpec spec) => ConstEip3860;

    public static bool Eip6780(IReleaseSpec spec) => ConstEip6780;

    public static bool NetGasMetering(IReleaseSpec spec) => ConstNetGasMetering;

    // EIP-8038 activates at Amsterdam, which is inside the range, so it stays dynamic and the handlers
    // behind both settings are compiled.
    public static bool Eip8038(IReleaseSpec spec) => spec.IsEip8038Enabled;

    // EIP-2780, EIP-7708, EIP-8037 and EIP-8246 also activate at Amsterdam and at no other fork in the
    // range, so they follow the EIP-8038 flag the table has already chosen. Reading the spec here
    // would compile every pairing of the five where the range produces two.
    public static bool Eip2780<TEip8038>(IReleaseSpec spec) where TEip8038 : struct, IEip8038Flag => TEip8038.IsActive;

    public static bool Eip7708<TEip8038>(IReleaseSpec spec) where TEip8038 : struct, IEip8038Flag => TEip8038.IsActive;

    public static bool Eip8037<TEip8038>(IReleaseSpec spec) where TEip8038 : struct, IEip8038Flag => TEip8038.IsActive;

    public static bool Eip8246<TEip8038>(IReleaseSpec spec) where TEip8038 : struct, IEip8038Flag => TEip8038.IsActive;

    private static IReleaseSpec? _validated;

    /// <summary>Rejects a spec outside the fork range this build folded its rules for.</summary>
    /// <remarks>
    /// Without this a spec from outside the range runs against constants that do not describe it,
    /// producing wrong gas and a wrong state root that the proof would then attest to.
    /// </remarks>
    public static void Validate(IReleaseSpec spec)
    {
        // A spec is fixed for the block, so the checks run once per spec rather than once per
        // transaction. One slot suffices: the guest validates a single block and runs single-threaded.
        if (ReferenceEquals(_validated, spec)) return;

        Check(spec.Use63Over64Rule, ConstEip150, "EIP-150");
        Check(spec.ClearEmptyAccountWhenTouched, ConstEip158, "EIP-158");
        Check(spec.UseNetGasMeteringWithAStipendFix, ConstEip2200, "EIP-2200");
        Check(spec.UseHotAndColdStorage, ConstEip2929, "EIP-2929");
        Check(spec.IsEip3860Enabled, ConstEip3860, "EIP-3860");
        Check(spec.SelfdestructOnlyOnSameTransaction, ConstEip6780, "EIP-6780");
        Check(spec.UseNetGasMetering, ConstNetGasMetering, "NetGasMetering");
        Follows(spec.IsEip2780Enabled, spec.IsEip8038Enabled, "EIP-2780", "EIP-8038");
        Follows(spec.IsEip7708Enabled, spec.IsEip8038Enabled, "EIP-7708", "EIP-8038");
        Follows(spec.IsEip8037Enabled, spec.IsEip8038Enabled, "EIP-8037", "EIP-8038");
        Follows(spec.RemoveSelfdestructBurn, spec.IsEip8038Enabled, "EIP-8246", "EIP-8038");
        _validated = spec;

        static void Check(bool actual, bool compiled, string eip)
        {
            if (actual != compiled)
                throw new InvalidOperationException(
                    $"{eip} is {actual} for this block but the guest was built for the fork range Osaka..Amsterdam, where it is always {compiled}. Rebuild with a range that covers the block.");
        }

        static void Follows(bool actual, bool anchor, string eip, string anchorEip)
        {
            if (actual != anchor)
                throw new InvalidOperationException(
                    $"{eip} is {actual} for this block but {anchorEip} is {anchor}; the guest was built for the fork range Osaka..Amsterdam, where they move together. Rebuild with a range that covers the block.");
        }
    }
}
