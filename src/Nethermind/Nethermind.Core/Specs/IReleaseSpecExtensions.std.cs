// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Core.Specs;

public static partial class IReleaseSpecExtensions
{
    extension(IReleaseSpec spec)
    {
        // STATE related
        public bool ClearEmptyAccountWhenTouched
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.IsEip158Enabled;
        }
        public bool UseHotAndColdStorage
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.IsEip2929Enabled;
        }
        public bool ChargeForTopLevelCreate
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.IsEip2Enabled;
        }
        public bool FailOnOutOfGasCodeDeposit
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.IsEip2Enabled;
        }
        public bool UseShanghaiDDosProtection
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.IsEip150Enabled;
        }
        public bool UseConstantinopleNetGasMetering
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.IsEip1283Enabled;
        }
        public bool UseIstanbulNetGasMetering
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.IsEip2200Enabled;
        }
        public bool UseNetGasMetering
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.UseConstantinopleNetGasMetering || spec.UseIstanbulNetGasMetering;
        }
        public bool UseNetGasMeteringWithAStipendFix
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.UseIstanbulNetGasMetering;
        }
        public bool Use63Over64Rule
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => spec.UseShanghaiDDosProtection;
        }

        /// <summary>
        /// Determines whether the specified address is a precompiled contract for this release specification.
        /// </summary>
        /// <param name="address">The address to check for precompile status.</param>
        /// <returns>True if the address is a precompiled contract; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPrecompile(Address address) => spec.Precompiles.Contains(address);
    }
}
