// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
//
// Generated from the fork graph for the range Osaka..BPO2.
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
    public const bool ConstEip2780 = false;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip2929 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip3860 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip7708 = false;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip8037 = false;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip8038 = false;

    public static bool Eip150(IReleaseSpec spec) => ConstEip150;

    public static bool Eip158(IReleaseSpec spec) => ConstEip158;

    public static bool Eip2780(IReleaseSpec spec) => ConstEip2780;

    public static bool Eip2929(IReleaseSpec spec) => ConstEip2929;

    public static bool Eip3860(IReleaseSpec spec) => ConstEip3860;

    public static bool Eip7708(IReleaseSpec spec) => ConstEip7708;

    public static bool Eip8037(IReleaseSpec spec) => ConstEip8037;

    public static bool Eip8038(IReleaseSpec spec) => ConstEip8038;

    /// <summary>Rejects a spec outside the fork range this build folded its rules for.</summary>
    /// <remarks>
    /// Without this a spec from outside the range runs against constants that do not describe it,
    /// producing wrong gas and a wrong state root that the proof would then attest to. Amsterdam is
    /// the first fork past the range, so an Amsterdam block fails here until the range moves.
    /// </remarks>
    public static void Validate(IReleaseSpec spec)
    {
        Check(spec.Use63Over64Rule, ConstEip150, "EIP-150");
        Check(spec.ClearEmptyAccountWhenTouched, ConstEip158, "EIP-158");
        Check(spec.IsEip2780Enabled, ConstEip2780, "EIP-2780");
        Check(spec.UseHotAndColdStorage, ConstEip2929, "EIP-2929");
        Check(spec.IsEip3860Enabled, ConstEip3860, "EIP-3860");
        Check(spec.IsEip7708Enabled, ConstEip7708, "EIP-7708");
        Check(spec.IsEip8037Enabled, ConstEip8037, "EIP-8037");
        Check(spec.IsEip8038Enabled, ConstEip8038, "EIP-8038");

        static void Check(bool actual, bool compiled, string eip)
        {
            if (actual != compiled)
                throw new InvalidOperationException(
                    $"{eip} is {actual} for this block but the guest was built for the fork range Osaka..BPO2, where it is always {compiled}. Rebuild with a range that covers the block.");
        }
    }
}
