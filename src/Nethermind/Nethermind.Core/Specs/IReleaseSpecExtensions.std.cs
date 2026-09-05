// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Specs;

public static partial class IReleaseSpecExtensions
{
    private static readonly ConditionalWeakTable<IReleaseSpec, IReleaseSpec> _noEip158Specs = [];

    private static IReleaseSpec GetNoEip158Spec(IReleaseSpec spec) =>
        _noEip158Specs.GetValue(spec, static s => new NoEip158Spec(s));

    extension(IReleaseSpec spec)
    {
        public bool ClearEmptyAccountWhenTouched => spec.IsEip158Enabled;
        public bool UseHotAndColdStorage => spec.IsEip2929Enabled;
        public bool ChargeForTopLevelCreate => spec.IsEip2Enabled;
        public bool FailOnOutOfGasCodeDeposit => spec.IsEip2Enabled;
        public bool UseShanghaiDDosProtection => spec.IsEip150Enabled;
        public bool UseConstantinopleNetGasMetering => spec.IsEip1283Enabled;
        public bool UseIstanbulNetGasMetering => spec.IsEip2200Enabled;
        public bool UseNetGasMetering => spec.UseConstantinopleNetGasMetering || spec.UseIstanbulNetGasMetering;
        public bool UseNetGasMeteringWithAStipendFix => spec.UseIstanbulNetGasMetering;
        public bool Use63Over64Rule => spec.UseShanghaiDDosProtection;

        /// <summary>
        /// Determines whether the specified address is a precompiled contract for this release specification.
        /// </summary>
        /// <param name="address">The address to check for precompile status.</param>
        /// <returns><c>true</c> if the address is a precompiled contract; otherwise, <c>false</c>.</returns>
        /// <remarks>
        /// Called for every call target, which is almost never a precompile, so a non-precompile is rejected by its
        /// address shape alone and only a low address pays the set probe. Assumes every precompile lives at a low
        /// address, as <see cref="Address.PrecompileIndexOrNegative"/> requires.
        /// </remarks>
        public bool IsPrecompile(Address address)
        {
            // TESTING: call-frequency instrumentation, testing branch only - never merge to master.
            Precompiles.PrecompileLookupCounters.IsPrecompileCalls.Increment();
            bool isPrecompile = address.PrecompileIndexOrNegative() >= 0 && spec.Precompiles.Contains(address);
            if (isPrecompile) Precompiles.PrecompileLookupCounters.IsPrecompileHits.Increment();
            return isPrecompile;
        }
    }
}
