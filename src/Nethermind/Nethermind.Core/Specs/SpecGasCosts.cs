// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Specs;

/// <summary>
/// Precomputed gas cost and refund values derived from an <see cref="IReleaseSpec"/> instance.
/// Since specs are per-fork singletons, these values are computed once and cached on the spec,
/// avoiding repeated interface dispatch chains on every EVM opcode execution.
/// </summary>
public sealed class SpecGasCosts : IEquatable<SpecGasCosts>
{
    // Opcode gas costs
    // Values verified against GasCostOf in Nethermind.Evm
    public readonly long SLoadCost;
    public readonly long BalanceCost;
    public readonly long ExtCodeCost;
    public readonly long ExtCodeHashCost;
    public readonly long CallCost;
    public readonly long ExpByteCost;
    public readonly long SStoreResetCost;
    public readonly long NetMeteredSStoreCost;
    public readonly long TxDataNonZeroMultiplier;

    // Refund values
    // Values verified against RefundOf in Nethermind.Evm
    public readonly long ClearReversalRefund;
    public readonly long SetReversalRefund;
    public readonly long SClearRefund;

    // Blob gas (uses Eip4844Constants.GasPerBlob = 131072 from Nethermind.Core)
    public readonly ulong MaxBlobGasPerBlock;
    public readonly ulong MaxBlobGasPerTx;
    public readonly ulong TargetBlobGasPerBlock;

    public SpecGasCosts(IReleaseSpec spec)
    {
        bool hotCold = spec.IsEip2929Enabled;  // EIP-2929: hot/cold storage
        bool largeDDos = spec.IsEip1884Enabled;  // EIP-1884: repricing
        bool shanghaiDDos = spec.IsEip150Enabled;   // EIP-150: Tangerine Whistle repricing
        bool netIstanbul = spec.IsEip2200Enabled;  // EIP-2200: net-metered SSTORE
        bool netConstpl = spec.IsEip1283Enabled;  // EIP-1283: net-metered SSTORE (Constantinople)

        // SLOAD: 50 base → 200 (EIP-150) → 800 (EIP-1884) → 0 upfront (EIP-2929, charged via access tracker)
        SLoadCost = hotCold ? 0L : largeDDos ? 800L : shanghaiDDos ? 200L : 50L;
        // BALANCE: 20 base → 400 (EIP-150) → 700 (EIP-1884) → 0 upfront (EIP-2929)
        BalanceCost = hotCold ? 0L : largeDDos ? 700L : shanghaiDDos ? 400L : 20L;
        // EXTCODE*: 20 base → 700 (EIP-150) → 0 upfront (EIP-2929)
        ExtCodeCost = hotCold ? 0L : shanghaiDDos ? 700L : 20L;
        // EXTCODEHASH: 400 base → 700 (EIP-1884) → 0 upfront (EIP-2929)
        ExtCodeHashCost = hotCold ? 0L : largeDDos ? 700L : 400L;
        // CALL: 40 base → 700 (EIP-150) → 0 upfront (EIP-2929)
        CallCost = hotCold ? 0L : shanghaiDDos ? 700L : 40L;
        // EXP byte: 10 base → 50 (EIP-160)
        ExpByteCost = spec.IsEip160Enabled ? 50L : 10L;
        // SSTORE reset: SReset=5000, or SReset-ColdSLoad=2900 under EIP-2929
        SStoreResetCost = hotCold ? 2900L : 5000L;
        // Tx calldata non-zero byte multiplier (ratio to TxDataZero=4):
        // pre-EIP-2028: 68/4=17, EIP-2028: 16/4=4
        TxDataNonZeroMultiplier = spec.IsEip2028Enabled ? 4L : 17L;

        // Net-metered SSTORE cost — 0L default is safe: only read when net metering is active
        // WarmStateRead=100 (EIP-2929), SStoreNetMeteredEip2200=800, SStoreNetMeteredEip1283=200
        NetMeteredSStoreCost = hotCold ? 100L : netIstanbul ? 800L : netConstpl ? 200L : 0L;

        // SClear refund: 15000 before EIP-3529; SReset(5000)-ColdSLoad(2100)+AccessStorageListEntry(1900)=4800 after
        SClearRefund = spec.IsEip3529Enabled ? 4800L : 15000L;

        // Reversal refunds (verified against RefundOf in Nethermind.Evm):
        // SSet=20000, SReset=5000, ColdSLoad=2100, WarmStateRead=100
        // SStoreNetMeteredEip2200=800, SStoreNetMeteredEip1283=200
        ClearReversalRefund = hotCold ? 2800L   // SReset(5000) - ColdSLoad(2100) - WarmStateRead(100)
                            : netIstanbul ? 4200L   // SReset(5000) - SStoreNetMeteredEip2200(800)
                            : netConstpl ? 4800L   // SReset(5000) - SStoreNetMeteredEip1283(200)
                            : 0L;
        SetReversalRefund = hotCold ? 19900L  // SSet(20000) - WarmStateRead(100)
                            : netIstanbul ? 19200L  // SSet(20000) - SStoreNetMeteredEip2200(800)
                            : netConstpl ? 19800L  // SSet(20000) - SStoreNetMeteredEip1283(200)
                            : 0L;

        // Blob gas: GasPerBlob = 131072 (Eip4844Constants, in Nethermind.Core)
        MaxBlobGasPerBlock = spec.MaxBlobCount * Eip4844Constants.GasPerBlob;
        MaxBlobGasPerTx = spec.MaxBlobsPerTx * Eip4844Constants.GasPerBlob;
        TargetBlobGasPerBlock = spec.TargetBlobCount * Eip4844Constants.GasPerBlob;
    }

    public bool Equals(SpecGasCosts? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return SLoadCost == other.SLoadCost
            && BalanceCost == other.BalanceCost
            && ExtCodeCost == other.ExtCodeCost
            && ExtCodeHashCost == other.ExtCodeHashCost
            && CallCost == other.CallCost
            && ExpByteCost == other.ExpByteCost
            && SStoreResetCost == other.SStoreResetCost
            && NetMeteredSStoreCost == other.NetMeteredSStoreCost
            && TxDataNonZeroMultiplier == other.TxDataNonZeroMultiplier
            && ClearReversalRefund == other.ClearReversalRefund
            && SetReversalRefund == other.SetReversalRefund
            && SClearRefund == other.SClearRefund
            && MaxBlobGasPerBlock == other.MaxBlobGasPerBlock
            && MaxBlobGasPerTx == other.MaxBlobGasPerTx
            && TargetBlobGasPerBlock == other.TargetBlobGasPerBlock;
    }

    public override bool Equals(object? obj) => obj is SpecGasCosts other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        HashCode.Combine(SLoadCost, BalanceCost, ExtCodeCost, ExtCodeHashCost, CallCost, ExpByteCost, SStoreResetCost, NetMeteredSStoreCost),
        HashCode.Combine(TxDataNonZeroMultiplier, ClearReversalRefund, SetReversalRefund, SClearRefund, MaxBlobGasPerBlock, MaxBlobGasPerTx, TargetBlobGasPerBlock));
}
