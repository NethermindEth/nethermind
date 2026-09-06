// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;

namespace Nethermind.Evm;

/// <summary>
/// The fork rules the opcode table consults when it selects a handler specialization.
/// </summary>
/// <remarks>
/// Each rule is read once per table build rather than per execution, so routing them through this
/// type costs nothing at run time. It exists so the zkEVM build can answer a rule with a constant:
/// an ahead-of-time compiler emits every reachable instantiation, and a rule that cannot vary over
/// the range of forks the guest is built for removes half of them. A rule that varies but always
/// moves together with another takes that rule's flag as a type parameter, so the zkEVM build can
/// derive it from the flag and skip the pairings the range never produces. See
/// <c>SpecFlags.zkevm.cs</c>, which is generated from the fork graph and verified by
/// <c>SpecFlagsTests</c>.
/// </remarks>
internal static partial class SpecFlags
{
    public static bool Eip150(IReleaseSpec spec) => spec.Use63Over64Rule;

    public static bool Eip158(IReleaseSpec spec) => spec.ClearEmptyAccountWhenTouched;

    public static bool Eip160(IReleaseSpec spec) => spec.UseExpDDosProtection;

    public static bool Eip2200(IReleaseSpec spec) => spec.UseNetGasMeteringWithAStipendFix;

    public static bool Eip2929(IReleaseSpec spec) => spec.UseHotAndColdStorage;

    public static bool Eip3860(IReleaseSpec spec) => spec.IsEip3860Enabled;

    public static bool Eip6780(IReleaseSpec spec) => spec.SelfdestructOnlyOnSameTransaction;

    public static bool Eip8038(IReleaseSpec spec) => spec.IsEip8038Enabled;

    public static bool NetGasMetering(IReleaseSpec spec) => spec.UseNetGasMetering;

    // The type parameter is the EIP-8038 flag the table has already chosen. A build whose fork range
    // switches these rules together with it derives them from the flag; this build reads the spec.
    public static bool Eip2780<TEip8038>(IReleaseSpec spec) where TEip8038 : struct, IEip8038Flag => spec.IsEip2780Enabled;

    public static bool Eip7708<TEip8038>(IReleaseSpec spec) where TEip8038 : struct, IEip8038Flag => spec.IsEip7708Enabled;

    public static bool Eip8037<TEip8038>(IReleaseSpec spec) where TEip8038 : struct, IEip8038Flag => spec.IsEip8037Enabled;

    public static bool Eip8246<TEip8038>(IReleaseSpec spec) where TEip8038 : struct, IEip8038Flag => spec.RemoveSelfdestructBurn;

    /// <summary>Rejects a spec the build cannot serve.</summary>
    /// <remarks>Every rule is read from <paramref name="spec"/> here, so every spec is servable.</remarks>
    public static void Validate(IReleaseSpec spec) { }
}
