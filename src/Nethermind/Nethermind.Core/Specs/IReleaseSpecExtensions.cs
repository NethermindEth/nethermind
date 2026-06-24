// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// Extension members for <see cref="IReleaseSpec"/> providing computed properties
/// and helper methods based on EIP enablement flags.
/// </summary>
/// <remarks>
/// Under <c>ZK_EVM</c> this type caches the per-block-monomorphic spec data (EIP flags,
/// precompile-set bitmask, gas costs) in plain static fields keyed by the spec reference.
/// The writes in <see cref="BuildSpecFlags"/>/<c>BuildPrecompileMask</c> are NOT atomic, so
/// this is sound ONLY because the ZisK guest is single-threaded and validates one block
/// (one spec) per run. Do not introduce concurrent EVM execution on the ZK target without
/// reworking these caches.
/// </remarks>
public static class IReleaseSpecExtensions
{
#if ZK_EVM
    // Precompile membership as a bitmask instead of a FrozenSet hash+probe.
    // The set is fork-fixed, so it is derived once per spec and cached. Single
    // entry is sufficient: the ZisK guest validates one block (one spec).
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
            // Every entry of spec.Precompiles is a precompile, so an index outside the
            // representable set is a new precompile the mask cannot hold -> IsPrecompile would
            // wrongly return false (cold-access gas + executing it as bytecode). Enforce the
            // assumption; if it ever fires, extend the mask (and IsPrecompile) accordingly.
            else System.Diagnostics.Debug.Assert(false,
                $"Precompile index 0x{idx:X} is outside the ZK precompile-mask range [0,64) or 0x100; extend BuildPrecompileMask and IsPrecompile.");
        }
        _precompileMaskLow = low;
        _precompileMaskP256 = p256;
        _precompileMaskSpec = spec;
    }

    // "Dynamic GDV" for the release spec: the hot extension getters below are read
    // per-opcode / per-storage-account-access (UseHotAndColdStorage = EIP-2929 cold/
    // warm, the gas-metering flags, ...). Each `spec.IsEipXxxEnabled` is an IReleaseSpec
    // interface dispatch (RhpInterfaceDispatch1, ~3.45% of zkVM steps). The spec is
    // fork-fixed and monomorphic per block, so resolve the hot flags ONCE per spec into
    // static slots (single entry: the ZisK guest validates one block = one spec) and let
    // the getters read a cached bool (reference-compare + field load, no dispatch). Same
    // values as the dispatching versions; fork-agnostic (rebuilds if the spec changes).
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
        if (!ReferenceEquals(_flagsSpec, spec)) BuildSpecFlags(spec);
    }
#endif

    extension(IReleaseSpec spec)
    {
        // GasCostsFast is an ALIAS for spec.GasCosts (NOT a cache, despite the name). A cached value-static
        // variant (GasCostsView over per-field statics) was tried but bflat's riscv64 codegen
        // miscompiles it (crash PC=0x80034cd4), and a GC-static SpecGasCosts cache crashes too
        // (PC=0xBEC8xxxx). The trivial getter inlines, so `spec.GasCostsFast.X` == `spec.GasCosts.X`.
        public SpecGasCosts GasCostsFast => spec.GasCosts;

        //EIP-3860: Limit and meter initcode
        public long MaxInitCodeSize => 2 * spec.MaxCodeSize;
        public bool DepositsEnabled => spec.IsEip6110Enabled;
        public bool WithdrawalRequestsEnabled => spec.IsEip7002Enabled;
        public bool ConsolidationRequestsEnabled => spec.IsEip7251Enabled;
        // STATE related
#if ZK_EVM
        public bool ClearEmptyAccountWhenTouched { get { EnsureSpecFlags(spec); return _f_clearEmptyAccountWhenTouched; } }
#else
        public bool ClearEmptyAccountWhenTouched => spec.IsEip158Enabled;
#endif
        // VM
        public bool LimitCodeSize => spec.IsEip170Enabled;
#if ZK_EVM
        public bool UseHotAndColdStorage { get { EnsureSpecFlags(spec); return _f_useHotAndColdStorage; } }
#else
        public bool UseHotAndColdStorage => spec.IsEip2929Enabled;
#endif
        public bool UseTxAccessLists => spec.IsEip2930Enabled;
        public bool AddCoinbaseToTxAccessList => spec.IsEip3651Enabled;
        public bool ModExpEnabled => spec.IsEip198Enabled;
        public bool BN254Enabled => spec.IsEip196Enabled && spec.IsEip197Enabled;
        public bool BlakeEnabled => spec.IsEip152Enabled;
        public bool Bls12381Enabled => spec.IsEip2537Enabled;
#if ZK_EVM
        public bool ChargeForTopLevelCreate { get { EnsureSpecFlags(spec); return _f_chargeForTopLevelCreate; } }
        public bool FailOnOutOfGasCodeDeposit { get { EnsureSpecFlags(spec); return _f_failOnOutOfGasCodeDeposit; } }
        public bool UseShanghaiDDosProtection { get { EnsureSpecFlags(spec); return _f_useShanghaiDDosProtection; } }
#else
        public bool ChargeForTopLevelCreate => spec.IsEip2Enabled;
        public bool FailOnOutOfGasCodeDeposit => spec.IsEip2Enabled;
        public bool UseShanghaiDDosProtection => spec.IsEip150Enabled;
#endif
        public bool UseExpDDosProtection => spec.IsEip160Enabled;
        public bool UseLargeStateDDosProtection => spec.IsEip1884Enabled;
        public bool ReturnDataOpcodesEnabled => spec.IsEip211Enabled;
        public bool ChainIdOpcodeEnabled => spec.IsEip1344Enabled;
        public bool Create2OpcodeEnabled => spec.IsEip1014Enabled;
        public bool DelegateCallEnabled => spec.IsEip7Enabled;
        public bool StaticCallEnabled => spec.IsEip214Enabled;
        public bool ShiftOpcodesEnabled => spec.IsEip145Enabled;
        public bool RevertOpcodeEnabled => spec.IsEip140Enabled;
        public bool ExtCodeHashOpcodeEnabled => spec.IsEip1052Enabled;
        public bool SelfBalanceOpcodeEnabled => spec.IsEip1884Enabled;
#if ZK_EVM
        public bool UseConstantinopleNetGasMetering { get { EnsureSpecFlags(spec); return _f_useConstantinopleNetGasMetering; } }
        public bool UseIstanbulNetGasMetering { get { EnsureSpecFlags(spec); return _f_useIstanbulNetGasMetering; } }
        public bool UseNetGasMetering { get { EnsureSpecFlags(spec); return _f_useConstantinopleNetGasMetering || _f_useIstanbulNetGasMetering; } }
        public bool UseNetGasMeteringWithAStipendFix { get { EnsureSpecFlags(spec); return _f_useIstanbulNetGasMetering; } }
        public bool Use63Over64Rule { get { EnsureSpecFlags(spec); return _f_useShanghaiDDosProtection; } }
#else
        public bool UseConstantinopleNetGasMetering => spec.IsEip1283Enabled;
        public bool UseIstanbulNetGasMetering => spec.IsEip2200Enabled;
        public bool UseNetGasMetering => spec.UseConstantinopleNetGasMetering || spec.UseIstanbulNetGasMetering;
        public bool UseNetGasMeteringWithAStipendFix => spec.UseIstanbulNetGasMetering;
        public bool Use63Over64Rule => spec.UseShanghaiDDosProtection;
#endif
        public bool BaseFeeEnabled => spec.IsEip3198Enabled;
        // EVM Related
        public bool IncludePush0Instruction => spec.IsEip3855Enabled;
        public bool TransientStorageEnabled => spec.IsEip1153Enabled;
        public bool WithdrawalsEnabled => spec.IsEip4895Enabled;
        public bool SelfdestructOnlyOnSameTransaction => spec.IsEip6780Enabled;
        public bool IsBeaconBlockRootAvailable => spec.IsEip4788Enabled;
        public bool IsBlockHashInStateAvailable => spec.IsEip7709Enabled;
        public bool MCopyIncluded => spec.IsEip5656Enabled;
        public bool BlobBaseFeeEnabled => spec.IsEip4844Enabled;
        public bool IsAuthorizationListEnabled => spec.IsEip7702Enabled;
        public bool RequestsEnabled => spec.ConsolidationRequestsEnabled || spec.WithdrawalRequestsEnabled || spec.DepositsEnabled;
        /// <summary>
        /// Determines whether the specified address is a precompiled contract for this release specification.
        /// </summary>
        /// <param name="address">The address to check for precompile status.</param>
        /// <returns>True if the address is a precompiled contract; otherwise, false.</returns>
#if ZK_EVM
        public bool IsPrecompile(Address address)
        {
            if (!ReferenceEquals(_precompileMaskSpec, spec)) BuildPrecompileMask(spec);
            int idx = address.PrecompileIndexOrNegative();
            return (uint)idx < 64
                ? (_precompileMaskLow & (1UL << idx)) != 0
                : idx == 0x100 && _precompileMaskP256;
        }
#else
        public bool IsPrecompile(Address address) => spec.Precompiles.Contains(address);
#endif
        public ProofVersion BlobProofVersion => spec.IsEip7594Enabled ? ProofVersion.V1 : ProofVersion.V0;
        public bool CLZEnabled => spec.IsEip7939Enabled;
        public bool BlockLevelAccessListsEnabled => spec.IsEip7928Enabled;
        /// <summary>
        /// Returns a spec with EIP-158 disabled, preventing empty-account deletion on commit.
        /// Used when applying state overrides to preserve EIP-7610 CREATE collision detection.
        /// </summary>
        public IReleaseSpec WithoutEip158() =>
            spec.IsEip158Enabled ? new NoEip158Spec(spec) : spec;

        /// <summary>
        /// Returns a spec with EIP-3607 disabled, allowing contract addresses to act as transaction senders.
        /// Used in <c>eth_simulateV1</c> where state-overridden contracts may be the <c>from</c> address.
        /// </summary>
        public IReleaseSpec WithoutEip3607() =>
            spec.IsEip3607Enabled ? new NoEip3607Spec(spec) : spec;
    }
}
