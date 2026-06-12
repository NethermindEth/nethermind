// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Specs;

/// <remarks>
/// The ZisK guest is single-threaded and validates one block (one spec) per run, so the hot
/// extension getters here are resolved ONCE per spec into static slots: each
/// <c>spec.IsEipXxxEnabled</c> would otherwise be an <see cref="IReleaseSpec"/> interface
/// dispatch (~3.45% of zkVM steps), and the precompile set would be a <c>FrozenSet</c>
/// hash+probe per access. The writes in <c>BuildSpecFlags</c>/<c>BuildPrecompileMask</c> are
/// not atomic; introducing concurrent EVM execution on the ZK target requires reworking
/// these caches.
/// </remarks>
public static partial class IReleaseSpecExtensions
{
    private static IReleaseSpec? _precompileMaskSpec;
    private static ulong _precompileMaskLow;
    private static bool _precompileMaskP256;

    private static void BuildPrecompileMask(IReleaseSpec spec)
    {
        ulong low = 0;
        bool p256 = false;
        foreach (AddressAsKey p in spec.Precompiles)
        {
            int idx = ((Address)p).PrecompileIndexOrNegative();
            if ((uint)idx < 64) low |= 1UL << idx;
            else if (idx == 0x100) p256 = true;
            // A precompile outside [0,64) ∪ {0x100} cannot be represented; IsPrecompile would
            // wrongly miss it (cold-access gas + bytecode execution). Extend the mask if this fires.
            else Debug.Assert(false,
                $"Precompile index 0x{idx:X} is outside the ZK precompile-mask range; extend BuildPrecompileMask and IsPrecompile.");
        }
        _precompileMaskLow = low;
        _precompileMaskP256 = p256;
        _precompileMaskSpec = spec;
    }

    private static IReleaseSpec? _flagsSpec;
    private static bool _f_clearEmptyAccountWhenTouched;
    private static bool _f_useHotAndColdStorage;
    private static bool _f_chargeForTopLevelCreate;
    private static bool _f_failOnOutOfGasCodeDeposit;
    private static bool _f_useShanghaiDDosProtection;
    private static bool _f_useConstantinopleNetGasMetering;
    private static bool _f_useIstanbulNetGasMetering;

    private static void BuildSpecFlags(IReleaseSpec spec)
    {
        _f_clearEmptyAccountWhenTouched = spec.IsEip158Enabled;
        _f_useHotAndColdStorage = spec.IsEip2929Enabled;
        _f_chargeForTopLevelCreate = spec.IsEip2Enabled;
        _f_failOnOutOfGasCodeDeposit = spec.IsEip2Enabled;
        _f_useShanghaiDDosProtection = spec.IsEip150Enabled;
        _f_useConstantinopleNetGasMetering = spec.IsEip1283Enabled;
        _f_useIstanbulNetGasMetering = spec.IsEip2200Enabled;
        _flagsSpec = spec;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EnsureSpecFlags(IReleaseSpec spec)
    {
        if (!ReferenceEquals(_flagsSpec, spec)) BuildSpecFlags(spec);
    }

    extension(IReleaseSpec spec)
    {
        // STATE related
        public bool ClearEmptyAccountWhenTouched
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_clearEmptyAccountWhenTouched; }
        }
        public bool UseHotAndColdStorage
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_useHotAndColdStorage; }
        }
        public bool ChargeForTopLevelCreate
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_chargeForTopLevelCreate; }
        }
        public bool FailOnOutOfGasCodeDeposit
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_failOnOutOfGasCodeDeposit; }
        }
        public bool UseShanghaiDDosProtection
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_useShanghaiDDosProtection; }
        }
        public bool UseConstantinopleNetGasMetering
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_useConstantinopleNetGasMetering; }
        }
        public bool UseIstanbulNetGasMetering
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_useIstanbulNetGasMetering; }
        }
        public bool UseNetGasMetering
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_useConstantinopleNetGasMetering || _f_useIstanbulNetGasMetering; }
        }
        public bool UseNetGasMeteringWithAStipendFix
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_useIstanbulNetGasMetering; }
        }
        public bool Use63Over64Rule
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { EnsureSpecFlags(spec); return _f_useShanghaiDDosProtection; }
        }

        /// <summary>
        /// Determines whether the specified address is a precompiled contract for this release specification.
        /// </summary>
        /// <param name="address">The address to check for precompile status.</param>
        /// <returns>True if the address is a precompiled contract; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsPrecompile(Address address)
        {
            if (!ReferenceEquals(_precompileMaskSpec, spec)) BuildPrecompileMask(spec);
            int idx = address.PrecompileIndexOrNegative();
            return (uint)idx < 64
                ? (_precompileMaskLow & (1UL << idx)) != 0
                : idx == 0x100 && _precompileMaskP256;
        }
    }
}
