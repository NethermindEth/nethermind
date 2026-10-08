// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

public static partial class IReleaseSpecExtensions
{
    private static IReleaseSpec GetNoEip158Spec(IReleaseSpec spec) => new NoEip158Spec(spec);

    // Each `spec.IsEipXxxEnabled` is an IReleaseSpec interface dispatch, and the getters below are read
    // per-opcode / per-storage-access. The spec is fork-fixed and monomorphic per block, so resolve the
    // hot flags ONCE per spec into static slots (single slot: one block = one spec) and have the getters
    // read a cached bool (reference-compare + field load, no dispatch). Rebuilds if the spec changes.
    // Only profile-hot flags are cached; add others here if profiling shows them hot.
    private static IReleaseSpec? _flagsSpec;
    private static bool _clearEmptyAccountWhenTouched;
    private static bool _useHotAndColdStorage;
    private static bool _chargeForTopLevelCreate;
    private static bool _failOnOutOfGasCodeDeposit;
    private static bool _useShanghaiDDosProtection;
    private static bool _useConstantinopleNetGasMetering;
    private static bool _useIstanbulNetGasMetering;

    private static void BuildSpecFlags(IReleaseSpec spec)
    {
        _clearEmptyAccountWhenTouched = spec.IsEip158Enabled;
        _useHotAndColdStorage = spec.IsEip2929Enabled;
        _chargeForTopLevelCreate = spec.IsEip2Enabled;
        _failOnOutOfGasCodeDeposit = spec.IsEip2Enabled;
        _useShanghaiDDosProtection = spec.IsEip150Enabled;
        _useConstantinopleNetGasMetering = spec.IsEip1283Enabled;
        _useIstanbulNetGasMetering = spec.IsEip2200Enabled;
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
                return _clearEmptyAccountWhenTouched;
            }
        }

        public bool UseHotAndColdStorage
        {
            get
            {
                EnsureSpecFlags(spec);
                return _useHotAndColdStorage;
            }
        }

        public bool ChargeForTopLevelCreate
        {
            get
            {
                EnsureSpecFlags(spec);
                return _chargeForTopLevelCreate;
            }
        }

        public bool FailOnOutOfGasCodeDeposit
        {
            get
            {
                EnsureSpecFlags(spec);
                return _failOnOutOfGasCodeDeposit;
            }
        }

        public bool UseShanghaiDDosProtection
        {
            get
            {
                EnsureSpecFlags(spec);
                return _useShanghaiDDosProtection;
            }
        }

        public bool UseConstantinopleNetGasMetering
        {
            get
            {
                EnsureSpecFlags(spec);
                return _useConstantinopleNetGasMetering;
            }
        }

        public bool UseIstanbulNetGasMetering
        {
            get
            {
                EnsureSpecFlags(spec);
                return _useIstanbulNetGasMetering;
            }
        }

        public bool UseNetGasMetering
        {
            get
            {
                EnsureSpecFlags(spec);
                return _useConstantinopleNetGasMetering || _useIstanbulNetGasMetering;
            }
        }

        public bool UseNetGasMeteringWithAStipendFix
        {
            get
            {
                EnsureSpecFlags(spec);
                return _useIstanbulNetGasMetering;
            }
        }

        public bool Use63Over64Rule
        {
            get
            {
                EnsureSpecFlags(spec);
                return _useShanghaiDDosProtection;
            }
        }
    }
}
