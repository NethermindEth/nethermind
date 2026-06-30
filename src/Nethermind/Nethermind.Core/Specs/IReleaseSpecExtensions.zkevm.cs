// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Specs;

public static partial class IReleaseSpecExtensions
{
    // Precompile membership as a bitmask instead of a FrozenSet hash+probe.
    // The set is fork-fixed, so it is built once per spec; a single slot suffices
    // because the zkVM guest validates one block (one spec).
    private static IReleaseSpec? _precompileMaskSpec;
    private static ulong _precompileMaskLow;
    private static bool _precompileMaskP256;

    private static void BuildPrecompileMask(IReleaseSpec spec)
    {
        ulong low = 0UL;
        bool p256 = false;

        foreach (AddressAsKey p in spec.Precompiles)
        {
            int idx = ((Address)p).PrecompileIndexOrNegative();

            if ((uint)idx < 64)
            {
                low |= 1UL << idx;
            }
            else if (idx == 0x100)
            {
                p256 = true;
            }
            // An out-of-range index is a new precompile the mask cannot hold; IsPrecompile would then
            // wrongly return false and the guest would skip it, mis-validating the block.
            else
            {
                throw new InvalidOperationException(
                    $"Precompile index 0x{idx:x} is outside the precompile-mask range [0,64) or 0x100; extend BuildPrecompileMask and IsPrecompile.");
            }
        }

        _precompileMaskLow = low;
        _precompileMaskP256 = p256;
        _precompileMaskSpec = spec;
    }

    // Each `spec.IsEipXxxEnabled` is an IReleaseSpec interface dispatch (~3.45% of zkVM
    // steps), and the getters below are read per-opcode / per-storage-access. The spec is
    // fork-fixed and monomorphic per block, so resolve the hot flags ONCE per spec into
    // static slots (single slot: one block = one spec) and have the getters read a cached
    // bool (reference-compare + field load, no dispatch). Rebuilds if the spec changes.
    //
    // Only flags found hot in the step profile are cached; every other extension flag still
    // dispatches per call. If a new hot path reads one of those, re-profile and add it here.
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

    private static void EnsureSpecFlags(IReleaseSpec spec)
    {
        if (!ReferenceEquals(_flagsSpec, spec))
            BuildSpecFlags(spec);
    }

    extension(IReleaseSpec spec)
    {
        public bool ClearEmptyAccountWhenTouched
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_clearEmptyAccountWhenTouched;
            }
        }

        public bool UseHotAndColdStorage
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_useHotAndColdStorage;
            }
        }

        public bool ChargeForTopLevelCreate
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_chargeForTopLevelCreate;
            }
        }

        public bool FailOnOutOfGasCodeDeposit
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_failOnOutOfGasCodeDeposit;
            }
        }

        public bool UseShanghaiDDosProtection
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_useShanghaiDDosProtection;
            }
        }

        public bool UseConstantinopleNetGasMetering
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_useConstantinopleNetGasMetering;
            }
        }

        public bool UseIstanbulNetGasMetering
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_useIstanbulNetGasMetering;
            }
        }

        public bool UseNetGasMetering
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_useConstantinopleNetGasMetering || _f_useIstanbulNetGasMetering;
            }
        }

        public bool UseNetGasMeteringWithAStipendFix
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_useIstanbulNetGasMetering;
            }
        }

        public bool Use63Over64Rule
        {
            get
            {
                EnsureSpecFlags(spec);
                return _f_useShanghaiDDosProtection;
            }
        }

        /// <summary>
        /// Determines whether the specified address is a precompiled contract for this release specification.
        /// </summary>
        /// <param name="address">The address to check for precompile status.</param>
        /// <returns>True if the address is a precompiled contract; otherwise, false.</returns>
        public bool IsPrecompile(Address address)
        {
            if (!ReferenceEquals(_precompileMaskSpec, spec))
                BuildPrecompileMask(spec);

            int idx = address.PrecompileIndexOrNegative();

            return (uint)idx < 64
                ? (_precompileMaskLow & (1UL << idx)) != 0
                : idx == 0x100 && _precompileMaskP256;
        }
    }
}
