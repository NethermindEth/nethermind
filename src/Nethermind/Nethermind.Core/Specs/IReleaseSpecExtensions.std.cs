// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

public static partial class IReleaseSpecExtensions
{
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
        public bool IsPrecompile(Address address) => spec.Precompiles.Contains(address);
    }
}
