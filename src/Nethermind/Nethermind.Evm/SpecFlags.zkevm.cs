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
    public const bool ConstEip2929 = true;

    /// <inheritdoc cref="ConstEip150"/>
    public const bool ConstEip3860 = true;

    public static bool Eip150(IReleaseSpec spec) => ConstEip150;

    public static bool Eip158(IReleaseSpec spec) => ConstEip158;

    public static bool Eip2929(IReleaseSpec spec) => ConstEip2929;

    public static bool Eip3860(IReleaseSpec spec) => ConstEip3860;

    // EIP-2780, EIP-7708, EIP-8037 and EIP-8038 activate at Amsterdam, which is inside the range, so
    // they stay dynamic and the handlers behind both settings are compiled.
    public static bool Eip2780(IReleaseSpec spec) => spec.IsEip2780Enabled;

    public static bool Eip7708(IReleaseSpec spec) => spec.IsEip7708Enabled;

    public static bool Eip8037(IReleaseSpec spec) => spec.IsEip8037Enabled;

    public static bool Eip8038(IReleaseSpec spec) => spec.IsEip8038Enabled;

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
        Check(spec.UseHotAndColdStorage, ConstEip2929, "EIP-2929");
        Check(spec.IsEip3860Enabled, ConstEip3860, "EIP-3860");
        _validated = spec;

        static void Check(bool actual, bool compiled, string eip)
        {
            if (actual != compiled)
                throw new InvalidOperationException(
                    $"{eip} is {actual} for this block but the guest was built for the fork range Osaka..Amsterdam, where it is always {compiled}. Rebuild with a range that covers the block.");
        }
    }
}
