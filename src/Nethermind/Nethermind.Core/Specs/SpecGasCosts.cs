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
    private readonly int _hashCode;
    public readonly long SLoadCost;
    public readonly long BalanceCost;
    public readonly long ExtCodeCost;
    public readonly long ExtCodeHashCost;
    public readonly long CallCost;
    public readonly long ExpByteCost;
    public readonly long SStoreResetCost;
    public readonly long TxDataNonZeroMultiplier;

#if DEBUG
    public long NetMeteredSStoreCost
    {
        get => GetWithFreeGuard(field);
        init;
    }

    public long ClearReversalRefund
    {
        get => GetWithFreeGuard(field);
        init;
    }

    public long SetReversalRefund
    {
        get => GetWithFreeGuard(field);
        init;
    }

    private static long GetWithFreeGuard(long field) =>
        field == GasCostOf.Free
            ? throw new InvalidOperationException("Asking about the net metered cost when net metering not enabled")
            : field;

#else
    public readonly long NetMeteredSStoreCost;

    public readonly long ClearReversalRefund;
    public readonly long SetReversalRefund;
#endif
    public readonly long SClearRefund;
    public readonly long DestroyRefund;

    public readonly ulong MaxBlobGasPerBlock;
    public readonly ulong MaxBlobGasPerTx;
    public readonly ulong TargetBlobGasPerBlock;

    public SpecGasCosts(IReleaseSpec spec)
    {
        bool hotCold = spec.UseHotAndColdStorage;  // EIP-2929
        bool largeDDos = spec.UseLargeStateDDosProtection;  // EIP-1884
        bool shanghaiDDos = spec.UseShanghaiDDosProtection;   // EIP-150
        bool netIstanbul = spec.UseIstanbulNetGasMetering;  // EIP-2200
        bool netConstantinople = spec.UseConstantinopleNetGasMetering;  // EIP-1283

        ClearReversalRefund =
            hotCold ? RefundOf.SResetReversedHotCold
            : netIstanbul ? RefundOf.SResetReversedEip2200
            : netConstantinople ? RefundOf.SResetReversedEip1283
            : GasCostOf.Free;

        long setReversalRefund = SetReversalRefund =
            hotCold ? RefundOf.SSetReversedHotCold
            : netIstanbul ? RefundOf.SSetReversedEip2200
            : netConstantinople ? RefundOf.SSetReversedEip1283
            : GasCostOf.Free;

        long sStoreResetCost = SStoreResetCost = hotCold
            ? GasCostOf.SReset - GasCostOf.ColdSLoad
            : GasCostOf.SReset;

        long netMeteredSStoreCost = NetMeteredSStoreCost =
            hotCold ? GasCostOf.WarmStateRead
            : netIstanbul ? GasCostOf.SStoreNetMeteredEip2200
            : netConstantinople ? GasCostOf.SStoreNetMeteredEip1283
            : GasCostOf.Free;

        BalanceCost =
            hotCold ? GasCostOf.Free
            : largeDDos ? GasCostOf.BalanceEip1884
            : shanghaiDDos ? GasCostOf.BalanceEip150
            : GasCostOf.Balance;

        SLoadCost =
            hotCold ? GasCostOf.Free
            : largeDDos ? GasCostOf.SLoadEip1884
            : shanghaiDDos ? GasCostOf.SLoadEip150
            : GasCostOf.SLoad;

        ExtCodeHashCost =
            hotCold ? GasCostOf.Free
            : largeDDos ? GasCostOf.ExtCodeHashEip1884
            : GasCostOf.ExtCodeHash;

        ExtCodeCost =
            hotCold ? GasCostOf.Free
            : shanghaiDDos ? GasCostOf.ExtCodeEip150
            : GasCostOf.ExtCode;

        CallCost =
            hotCold ? GasCostOf.Free
            : shanghaiDDos ? GasCostOf.CallEip150
            : GasCostOf.Call;

        ExpByteCost = spec.UseExpDDosProtection
            ? GasCostOf.ExpByteEip160
            : GasCostOf.ExpByte;

        MaxBlobGasPerBlock = spec.MaxBlobCount * Eip4844Constants.GasPerBlob;
        MaxBlobGasPerTx = spec.MaxBlobsPerTx * Eip4844Constants.GasPerBlob;
        TargetBlobGasPerBlock = spec.TargetBlobCount * Eip4844Constants.GasPerBlob;

        TxDataNonZeroMultiplier = spec.IsEip2028Enabled
            ? GasCostOf.TxDataNonZeroMultiplierEip2028
            : GasCostOf.TxDataNonZeroMultiplier;

        SClearRefund = spec.IsEip3529Enabled
            ? RefundOf.SClearAfter3529
            : RefundOf.SClearBefore3529;

        DestroyRefund = spec.IsEip3529Enabled
            ? RefundOf.DestroyAfter3529
            : RefundOf.DestroyBefore3529;

        int hashCode1 = HashCode.Combine(SLoadCost, BalanceCost, ExtCodeCost, ExtCodeHashCost, CallCost, ExpByteCost, sStoreResetCost, netMeteredSStoreCost);
        int hashCode2 = HashCode.Combine(TxDataNonZeroMultiplier, ClearReversalRefund, setReversalRefund, SClearRefund, MaxBlobGasPerBlock, MaxBlobGasPerTx, TargetBlobGasPerBlock, DestroyRefund);
        _hashCode = HashCode.Combine(hashCode1, hashCode2);
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
            && DestroyRefund == other.DestroyRefund
            && MaxBlobGasPerBlock == other.MaxBlobGasPerBlock
            && MaxBlobGasPerTx == other.MaxBlobGasPerTx
            && TargetBlobGasPerBlock == other.TargetBlobGasPerBlock;
    }

    public override bool Equals(object? obj) => obj is SpecGasCosts other && Equals(other);

    public override int GetHashCode() => _hashCode;
}
