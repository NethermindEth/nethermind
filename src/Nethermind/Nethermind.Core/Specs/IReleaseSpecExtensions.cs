// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// Extension members for <see cref="IReleaseSpec"/> providing computed properties
/// and helper methods based on EIP enablement flags.
/// </summary>
public static class IReleaseSpecExtensions
{
    extension(IReleaseSpec spec)
    {
        //EIP-3860: Limit and meter initcode
        public long MaxInitCodeSize => 2 * spec.MaxCodeSize;
        public bool DepositsEnabled => spec.IsEip6110Enabled;
        public bool WithdrawalRequestsEnabled => spec.IsEip7002Enabled;
        public bool ConsolidationRequestsEnabled => spec.IsEip7251Enabled;
        // STATE related
        public bool ClearEmptyAccountWhenTouched => spec.IsEip158Enabled;
        // VM
        public bool LimitCodeSize => spec.IsEip170Enabled;
        public bool UseHotAndColdStorage => spec.IsEip2929Enabled;
        public bool UseTxAccessLists => spec.IsEip2930Enabled;
        public bool AddCoinbaseToTxAccessList => spec.IsEip3651Enabled;
        public bool ModExpEnabled => spec.IsEip198Enabled;
        public bool BN254Enabled => spec.IsEip196Enabled && spec.IsEip197Enabled;
        public bool BlakeEnabled => spec.IsEip152Enabled;
        public bool Bls381Enabled => spec.IsEip2537Enabled;
        public bool ChargeForTopLevelCreate => spec.IsEip2Enabled;
        public bool FailOnOutOfGasCodeDeposit => spec.IsEip2Enabled;
        public bool UseShanghaiDDosProtection => spec.IsEip150Enabled;
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
        public bool UseConstantinopleNetGasMetering => spec.IsEip1283Enabled;
        public bool UseIstanbulNetGasMetering => spec.IsEip2200Enabled;
        public bool UseNetGasMetering => spec.UseConstantinopleNetGasMetering || spec.UseIstanbulNetGasMetering;
        public bool UseNetGasMeteringWithAStipendFix => spec.UseIstanbulNetGasMetering;
        public bool Use63Over64Rule => spec.UseShanghaiDDosProtection;
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
        public bool IsPrecompile(Address address) => spec.Precompiles.Contains(address);
        public ProofVersion BlobProofVersion => spec.IsEip7594Enabled ? ProofVersion.V1 : ProofVersion.V0;
        public bool CLZEnabled => spec.IsEip7939Enabled;
    }
}
