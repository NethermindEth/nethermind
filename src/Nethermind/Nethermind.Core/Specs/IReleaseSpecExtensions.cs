// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Specs;

/// <summary>
/// Extension members for <see cref="IReleaseSpec"/> providing computed properties
/// and helper methods based on EIP enablement flags.
/// </summary>
public static partial class IReleaseSpecExtensions
{
    extension(IReleaseSpec spec)
    {
        //EIP-3860: Limit and meter initcode
        public long MaxInitCodeSize => 2 * spec.MaxCodeSize;
        public bool DepositsEnabled => spec.IsEip6110Enabled;
        public bool WithdrawalRequestsEnabled => spec.IsEip7002Enabled;
        public bool ConsolidationRequestsEnabled => spec.IsEip7251Enabled;
        public bool BuilderRequestsEnabled => spec.IsEip8282Enabled;
        public bool LimitCodeSize => spec.IsEip170Enabled;

        public bool UseTxAccessLists => spec.IsEip2930Enabled;
        public bool AddCoinbaseToTxAccessList => spec.IsEip3651Enabled;
        public bool ModExpEnabled => spec.IsEip198Enabled;
        public bool BN254Enabled => spec.IsEip196Enabled && spec.IsEip197Enabled;
        public bool BlakeEnabled => spec.IsEip152Enabled;
        public bool Bls12381Enabled => spec.IsEip2537Enabled;

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

        public bool BaseFeeEnabled => spec.IsEip3198Enabled;

        // EVM Related
        public bool IncludePush0Instruction => spec.IsEip3855Enabled;
        public bool TransientStorageEnabled => spec.IsEip1153Enabled;
        public bool WithdrawalsEnabled => spec.IsEip4895Enabled;
        public bool SelfdestructOnlyOnSameTransaction => spec.IsEip6780Enabled;
        public bool RemoveSelfdestructBurn => spec.IsEip8246Enabled;
        public bool IsBeaconBlockRootAvailable => spec.IsEip4788Enabled;
        public bool IsBlockHashInStateAvailable => spec.IsEip7709Enabled;
        public bool MCopyIncluded => spec.IsEip5656Enabled;
        public bool BlobBaseFeeEnabled => spec.IsEip4844Enabled;
        public bool IsAuthorizationListEnabled => spec.IsEip7702Enabled;
        public bool RequestsEnabled => spec.ConsolidationRequestsEnabled || spec.WithdrawalRequestsEnabled || spec.DepositsEnabled || spec.BuilderRequestsEnabled;

        public ProofVersion BlobProofVersion => spec.IsEip7594Enabled ? ProofVersion.V1 : ProofVersion.V0;
        public bool CLZEnabled => spec.IsEip7939Enabled;
        public bool BlockLevelAccessListsEnabled => spec.IsEip7928Enabled;
        /// <summary>
        /// Returns a spec with EIP-158 disabled, preventing empty-account deletion on commit.
        /// Used when applying state overrides to preserve EIP-7610 CREATE collision detection.
        /// </summary>
        public IReleaseSpec WithoutEip158() =>
            spec.IsEip158Enabled ? GetNoEip158Spec(spec) : spec;

        /// <summary>
        /// Returns a spec with EIP-3607 disabled, allowing contract addresses to act as transaction senders.
        /// Used in <c>eth_simulateV1</c> where state-overridden contracts may be the <c>from</c> address.
        /// </summary>
        public IReleaseSpec WithoutEip3607() =>
            spec.IsEip3607Enabled ? GetNoEip3607Spec(spec) : spec;
    }
}
