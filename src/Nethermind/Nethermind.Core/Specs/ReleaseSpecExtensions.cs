// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using Nethermind.Int256;

namespace Nethermind.Core.Specs;

/// <summary>
/// Extension properties for IReleaseSpec that provide default implementations.
/// These were previously default interface members but were moved to extension properties
/// to improve compilation performance with ILCompiler.
/// </summary>
public static class ReleaseSpecExtensions
{
    /// <summary>
    /// EIP-3860: Limit and meter initcode
    /// </summary>
    public static long MaxInitCodeSize(this IReleaseSpec spec) => 2 * spec.MaxCodeSize;

    /// <summary>
    /// Should EIP158 be ignored for this account.
    /// </summary>
    /// <remarks>This is needed for SystemUser account compatibility with Parity.</remarks>
    public static bool IsEip158IgnoredAccount(this IReleaseSpec spec, Address address) => false;

    public static bool DepositsEnabled(this IReleaseSpec spec) => spec.IsEip6110Enabled;

    public static bool WithdrawalRequestsEnabled(this IReleaseSpec spec) => spec.IsEip7002Enabled;

    public static bool ConsolidationRequestsEnabled(this IReleaseSpec spec) => spec.IsEip7251Enabled;

    /// <summary>
    /// EIP-2935 ring buffer size for historical block hash storage.
    /// Defaults to 8,191 blocks for Ethereum mainnet.
    /// </summary>
    public static long Eip2935RingBufferSize(this IReleaseSpec spec) => Eip2935Constants.RingBufferSize;

    /// <summary>
    /// Should transactions be validated against chainId.
    /// </summary>
    /// <remarks>Backward compatibility for early Kovan blocks.</remarks>
    public static bool ValidateChainId(this IReleaseSpec spec) => true;

    // STATE related
    public static bool ClearEmptyAccountWhenTouched(this IReleaseSpec spec) => spec.IsEip158Enabled;

    // VM
    public static bool LimitCodeSize(this IReleaseSpec spec) => spec.IsEip170Enabled;

    public static bool UseHotAndColdStorage(this IReleaseSpec spec) => spec.IsEip2929Enabled;

    public static bool UseTxAccessLists(this IReleaseSpec spec) => spec.IsEip2930Enabled;

    public static bool AddCoinbaseToTxAccessList(this IReleaseSpec spec) => spec.IsEip3651Enabled;

    public static bool ModExpEnabled(this IReleaseSpec spec) => spec.IsEip198Enabled;

    public static bool BN254Enabled(this IReleaseSpec spec) => spec.IsEip196Enabled && spec.IsEip197Enabled;

    public static bool BlakeEnabled(this IReleaseSpec spec) => spec.IsEip152Enabled;

    public static bool Bls381Enabled(this IReleaseSpec spec) => spec.IsEip2537Enabled;

    public static bool ChargeForTopLevelCreate(this IReleaseSpec spec) => spec.IsEip2Enabled;

    public static bool FailOnOutOfGasCodeDeposit(this IReleaseSpec spec) => spec.IsEip2Enabled;

    public static bool UseShanghaiDDosProtection(this IReleaseSpec spec) => spec.IsEip150Enabled;

    public static bool UseExpDDosProtection(this IReleaseSpec spec) => spec.IsEip160Enabled;

    public static bool UseLargeStateDDosProtection(this IReleaseSpec spec) => spec.IsEip1884Enabled;

    public static bool ReturnDataOpcodesEnabled(this IReleaseSpec spec) => spec.IsEip211Enabled;

    public static bool ChainIdOpcodeEnabled(this IReleaseSpec spec) => spec.IsEip1344Enabled;

    public static bool Create2OpcodeEnabled(this IReleaseSpec spec) => spec.IsEip1014Enabled;

    public static bool DelegateCallEnabled(this IReleaseSpec spec) => spec.IsEip7Enabled;

    public static bool StaticCallEnabled(this IReleaseSpec spec) => spec.IsEip214Enabled;

    public static bool ShiftOpcodesEnabled(this IReleaseSpec spec) => spec.IsEip145Enabled;

    public static bool RevertOpcodeEnabled(this IReleaseSpec spec) => spec.IsEip140Enabled;

    public static bool ExtCodeHashOpcodeEnabled(this IReleaseSpec spec) => spec.IsEip1052Enabled;

    public static bool SelfBalanceOpcodeEnabled(this IReleaseSpec spec) => spec.IsEip1884Enabled;

    public static bool UseConstantinopleNetGasMetering(this IReleaseSpec spec) => spec.IsEip1283Enabled;

    public static bool UseIstanbulNetGasMetering(this IReleaseSpec spec) => spec.IsEip2200Enabled;

    public static bool UseNetGasMetering(this IReleaseSpec spec) => spec.UseConstantinopleNetGasMetering() | spec.UseIstanbulNetGasMetering();

    public static bool UseNetGasMeteringWithAStipendFix(this IReleaseSpec spec) => spec.UseIstanbulNetGasMetering();

    public static bool Use63Over64Rule(this IReleaseSpec spec) => spec.UseShanghaiDDosProtection();

    public static bool BaseFeeEnabled(this IReleaseSpec spec) => spec.IsEip3198Enabled;

    // EVM Related
    public static bool IncludePush0Instruction(this IReleaseSpec spec) => spec.IsEip3855Enabled;

    public static bool TransientStorageEnabled(this IReleaseSpec spec) => spec.IsEip1153Enabled;

    public static bool WithdrawalsEnabled(this IReleaseSpec spec) => spec.IsEip4895Enabled;

    public static bool SelfdestructOnlyOnSameTransaction(this IReleaseSpec spec) => spec.IsEip6780Enabled;

    public static bool IsBeaconBlockRootAvailable(this IReleaseSpec spec) => spec.IsEip4788Enabled;

    public static bool IsBlockHashInStateAvailable(this IReleaseSpec spec) => spec.IsEip7709Enabled;

    public static bool MCopyIncluded(this IReleaseSpec spec) => spec.IsEip5656Enabled;

    public static bool BlobBaseFeeEnabled(this IReleaseSpec spec) => spec.IsEip4844Enabled;

    public static bool IsAuthorizationListEnabled(this IReleaseSpec spec) => spec.IsEip7702Enabled;

    public static bool RequestsEnabled(this IReleaseSpec spec) => spec.ConsolidationRequestsEnabled() || spec.WithdrawalRequestsEnabled() || spec.DepositsEnabled();

    /// <summary>
    /// Determines whether the specified address is a precompiled contract for this release specification.
    /// </summary>
    /// <param name="spec">The release specification</param>
    /// <param name="address">The address to check for precompile status.</param>
    /// <returns>True if the address is a precompiled contract; otherwise, false.</returns>
    public static bool IsPrecompile(this IReleaseSpec spec, Address address) => spec.Precompiles.Contains(address);

    public static ProofVersion BlobProofVersion(this IReleaseSpec spec) => spec.IsEip7594Enabled ? ProofVersion.V1 : ProofVersion.V0;

    public static bool CLZEnabled(this IReleaseSpec spec) => spec.IsEip7939Enabled;
}
