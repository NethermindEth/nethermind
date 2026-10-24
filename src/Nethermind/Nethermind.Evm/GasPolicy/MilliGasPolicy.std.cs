// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// EIP-8053 gas policy: meters milli-gas inside the EVM while contracts, transactions and tracers see gas units.
/// </summary>
/// <typeparam name="TPrices">The milli-gas prices of fixed opcode costs.</typeparam>
/// <remarks>
/// <para>
/// Prototype of EIP-8053 as amended to keep legacy gas units observable. Costs passed to the policy stay in gas
/// units and are charged as <see cref="Eip8053Constants.MilliGasPerGas"/> milli-gas each, except fixed opcode costs,
/// which <typeparamref name="TPrices"/> prices. Frame gas, reserved child gas, returned child gas and the call stipend
/// are milli-gas, so a child returns its unused milli-gas exactly.
/// </para>
/// <para>
/// Contracts observe gas units: <c>GAS</c> pushes the remaining milli-gas divided by 1,000, rounded down, and the gas
/// argument of a call is in gas units. The transaction keeps gas units: execution starts with
/// <c>(tx.gas - intrinsic) * 1000</c> milli-gas and ends with <c>gas_used = tx.gas - gas_left / 1000</c>, which rounds
/// the charge up to a whole gas unit. Intrinsic and floor gas from <see cref="CalculateIntrinsicGas(Transaction, IReleaseSpec, ulong)"/>
/// are in gas units.
/// </para>
/// <para>
/// EIP-8037: only <c>gas_left</c> is milli-gas. The state gas reservoir, state gas used and state gas charges stay in
/// gas units, and the part of a state charge the reservoir cannot pay is taken from <c>gas_left</c> at 1,000 milli-gas
/// per gas unit. State gas steps run <see cref="EthereumGasPolicy"/> on the whole gas units of <c>gas_left</c> and keep
/// its fraction, so the two policies follow one set of state gas rules.
/// </para>
/// <para>
/// Prices come from <see cref="EthereumGasPolicy"/>: composite charges run against an Ethereum probe that can pay any
/// charge, and the policy then charges the probe's execution gas x1000 and its state gas through the reservoir, so the
/// two policies cannot price an operation differently.
/// </para>
/// </remarks>
public struct MilliGasPolicy<TPrices> : IGasPolicy<MilliGasPolicy<TPrices>>
    where TPrices : struct, IMilliGasPrices
{
    private const ulong MilliGasPerGas = Eip8053Constants.MilliGasPerGas;
    private const ulong ProbeGas = ulong.MaxValue;
    private const long ProbeStateGas = long.MaxValue / 2;
    private const ulong MaxGasUnits = ulong.MaxValue / MilliGasPerGas;

    /// <summary><c>gas_left</c> in milli-gas in a frame and in gas units in intrinsic and floor gas; state gas in gas units.</summary>
    private EthereumGasPolicy _gas;

    /// <summary>The remaining milli-gas in a frame; gas units in intrinsic and floor gas.</summary>
    public readonly ulong Value => _gas.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MilliGasPolicy<TPrices> FromULong(ulong value) => new() { _gas = EthereumGasPolicy.FromULong(value) };

    /// <inheritdoc/>
    /// <remarks>
    /// EIP-8141: a frame starts with <c>limits.execution * 1000</c> milli-gas, and <see cref="GetPreRefundGas"/> rounds its
    /// gas left down when it completes, so each frame rounds its execution gas up once.
    /// </remarks>
    public static MilliGasPolicy<TPrices> FromFrameLimits(ulong executionGasLimit, ulong stateGasLimit) =>
        FromExecutionGasUnits(EthereumGasPolicy.FromFrameLimits(executionGasLimit, stateGasLimit));

    /// <inheritdoc/>
    public static MilliGasPolicy<TPrices> CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) =>
        new() { _gas = EthereumGasPolicy.CreateSystemTransactionIntrinsicGas(blockGasLimit) };

    /// <inheritdoc/>
    public static bool TryCreateSystemTransactionAvailableGas(ulong gasLimit, in MilliGasPolicy<TPrices> intrinsicGas, IReleaseSpec spec, out MilliGasPolicy<TPrices> available)
    {
        bool created = EthereumGasPolicy.TryCreateSystemTransactionAvailableGas(gasLimit, in intrinsicGas._gas, spec, out EthereumGasPolicy availableGas);
        available = FromExecutionGasUnits(availableGas);
        return created;
    }

    /// <inheritdoc/>
    public static ulong ExecutionGasPerGasUnit
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => MilliGasPerGas;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetRemainingGas(in MilliGasPolicy<TPrices> gas) => gas._gas.Value;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearExecutionGas(ref MilliGasPolicy<TPrices> gas) => gas._gas.Value = 0;

    /// <inheritdoc/>
    /// <remarks>Rounds the remaining milli-gas down, so the execution gas dimension pays any fraction of a gas unit.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetPreRefundGas(in MilliGasPolicy<TPrices> gas, ulong txGasLimit)
    {
        EthereumGasPolicy gasUnits = gas._gas;
        gasUnits.Value /= MilliGasPerGas;
        return PreRefundGas(in gasUnits, txGasLimit);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateReservoir(in MilliGasPolicy<TPrices> gas) => gas._gas.StateReservoir;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasUsed(in MilliGasPolicy<TPrices> gas) => gas._gas.StateGasUsed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasSpill(in MilliGasPolicy<TPrices> gas) => gas._gas.StateGasSpill;

    /// <inheritdoc/>
    /// <remarks>In gas units.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CalculateStateGasSpill(in MilliGasPolicy<TPrices> gas, long stateGasCost) =>
        EthereumGasPolicy.CalculateStateGasSpill(in gas._gas, stateGasCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(ref MilliGasPolicy<TPrices> gas, ulong gasCost)
    {
        if (gasCost > gas._gas.Value / MilliGasPerGas)
        {
            gas._gas.Value = 0;
            return false;
        }

        gas._gas.Value -= gasCost * MilliGasPerGas;
        return true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas<TCost>(ref MilliGasPolicy<TPrices> gas) where TCost : struct, IGasCost
    {
        ulong milliGasCost = TPrices.MilliGasCost<TCost>();
        if (gas._gas.Value < milliGasCost)
        {
            gas._gas.Value = 0;
            return false;
        }

        gas._gas.Value -= milliGasCost;
        return true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsume(ref MilliGasPolicy<TPrices> gas, ulong cost) => EthereumGasPolicy.TryConsume(ref gas._gas, cost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(ref MilliGasPolicy<TPrices> gas, ulong refund) => gas._gas.Value += refund;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Refund(ref MilliGasPolicy<TPrices> gas, in MilliGasPolicy<TPrices> childGas) =>
        EthereumGasPolicy.Refund(ref gas._gas, in childGas._gas);

    /// <inheritdoc/>
    /// <remarks>The gas argument is in gas units; EIP-150 caps it at <see cref="MaxChildGas"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReserveChildGas(ref MilliGasPolicy<TPrices> gas, in UInt256 requestedGas, IReleaseSpec spec, out ulong childGas)
    {
        ulong gasAvailable = gas._gas.Value;
        if (spec.Use63Over64Rule)
        {
            ulong maxChildGas = MaxChildGas(gasAvailable);
            childGas = (requestedGas.IsUint64 && requestedGas.u0 <= maxChildGas ? requestedGas.u0 : maxChildGas) * MilliGasPerGas;
        }
        else if (requestedGas.IsUint64 && requestedGas.u0 <= gasAvailable / MilliGasPerGas)
        {
            childGas = requestedGas.u0 * MilliGasPerGas;
        }
        else
        {
            childGas = 0;
            gas._gas.Value = 0;
            return false;
        }

        gas._gas.Value -= childGas;
        return true;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReserveChildGas(ref MilliGasPolicy<TPrices> gas, IReleaseSpec spec, out ulong childGas)
    {
        ulong gasAvailable = gas._gas.Value;
        childGas = spec.Use63Over64Rule ? MaxChildGas(gasAvailable) * MilliGasPerGas : gasAvailable;
        gas._gas.Value -= childGas;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MilliGasPolicy<TPrices> CreateChildFrameGas(ref MilliGasPolicy<TPrices> parentGas, ulong childExecutionGas) =>
        new() { _gas = EthereumGasPolicy.CreateChildFrameGas(ref parentGas._gas, childExecutionGas) };

    public static bool UpdateMemoryCost(ref MilliGasPolicy<TPrices> gas, in UInt256 position, in UInt256 length, ref EvmPooledMemory memory)
    {
        ulong size = memory.Size;
        if (length.IsUint64 && length.u0 <= size && position.IsUint64 && position.u0 <= size - length.u0)
            return true;

        ulong memoryCost = memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        return memoryCost == 0 ? !outOfGas : UpdateGas(ref gas, memoryCost);
    }

    public static bool UpdateMemoryCost(ref MilliGasPolicy<TPrices> gas, in UInt256 position, ulong length, ref EvmPooledMemory memory)
    {
        ulong size = memory.Size;
        if (length <= size && position.IsUint64 && position.u0 <= size - length)
            return true;

        ulong memoryCost = memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        return memoryCost == 0 ? !outOfGas : UpdateGas(ref gas, memoryCost);
    }

    public static bool TryConsumeStateGas(ref MilliGasPolicy<TPrices> gas, long stateGasCost)
    {
        ulong fraction = EnterGasUnits(ref gas);
        bool consumed = EthereumGasPolicy.TryConsumeStateGas(ref gas._gas, stateGasCost);
        LeaveGasUnits(ref gas, fraction);
        return consumed;
    }

    public static bool TryConsumeStateAndExecutionGas(ref MilliGasPolicy<TPrices> gas, long stateGasCost, ulong executionGasCost) =>
        (executionGasCost == 0 || UpdateGas(ref gas, executionGasCost)) &&
        (stateGasCost <= 0 || TryConsumeStateGas(ref gas, stateGasCost));

    public static void RepayStateGasSpill(ref MilliGasPolicy<TPrices> gas)
    {
        ulong fraction = EnterGasUnits(ref gas);
        EthereumGasPolicy.RepayStateGasSpill(ref gas._gas);
        LeaveGasUnits(ref gas, fraction);
    }

    public static void RestoreChildStateGas(ref MilliGasPolicy<TPrices> parentGas, in MilliGasPolicy<TPrices> childGas)
    {
        ulong fraction = EnterGasUnits(ref parentGas);
        EthereumGasPolicy.RestoreChildStateGas(ref parentGas._gas, in childGas._gas);
        LeaveGasUnits(ref parentGas, fraction);
    }

    public static void RestoreChildStateGasOnHalt(ref MilliGasPolicy<TPrices> parentGas, in MilliGasPolicy<TPrices> childGas) =>
        EthereumGasPolicy.RestoreChildStateGasOnHalt(ref parentGas._gas, in childGas._gas);

    public static void RevertRefundToHalt(ref MilliGasPolicy<TPrices> parentGas, in MilliGasPolicy<TPrices> childGas) =>
        EthereumGasPolicy.RevertRefundToHalt(ref parentGas._gas, in childGas._gas);

    public static void RefundStateGas(ref MilliGasPolicy<TPrices> gas, long amount, long stateGasFloor) =>
        RefundStateGas(ref gas, amount, stateGasFloor, trackSpillRefund: true);

    public static void RefundStateGas(ref MilliGasPolicy<TPrices> gas, long amount, long stateGasFloor, bool trackSpillRefund)
    {
        ulong fraction = EnterGasUnits(ref gas);
        EthereumGasPolicy.RefundStateGas(ref gas._gas, amount, stateGasFloor, trackSpillRefund);
        LeaveGasUnits(ref gas, fraction);
    }

    public static long DiscardStateGas(ref MilliGasPolicy<TPrices> gas, long amount, long stateGasFloor) =>
        EthereumGasPolicy.DiscardStateGas(ref gas._gas, amount, stateGasFloor);

    public static void AddStateGasRefundToReservoir(ref MilliGasPolicy<TPrices> gas, long amount, bool trackSpillRefund)
    {
        ulong fraction = EnterGasUnits(ref gas);
        EthereumGasPolicy.AddStateGasRefundToReservoir(ref gas._gas, amount, trackSpillRefund);
        LeaveGasUnits(ref gas, fraction);
    }

    public static void RemoveStateGasRefundFromReservoir(ref MilliGasPolicy<TPrices> gas, long amount) =>
        EthereumGasPolicy.RemoveStateGasRefundFromReservoir(ref gas._gas, amount);

    public static void ResetForHalt(ref MilliGasPolicy<TPrices> gas, long initialStateReservoir, long initialStateGasUsed) =>
        EthereumGasPolicy.ResetForHalt(ref gas._gas, initialStateReservoir, initialStateGasUsed);

    public static void FoldTopFrameStateGas(ref MilliGasPolicy<TPrices> gas, ref MilliGasPolicy<TPrices> baseline, long stateGasUsed) =>
        EthereumGasPolicy.FoldTopFrameStateGas(ref gas._gas, ref baseline._gas, stateGasUsed);

    public static ulong GetCodeInsertExecutionRefund(ulong codeInsertRefunds, IReleaseSpec spec) =>
        EthereumGasPolicy.GetCodeInsertExecutionRefund(codeInsertRefunds, spec);

    public static ulong ApplyCodeInsertRefunds(ref MilliGasPolicy<TPrices> gas, ulong codeInsertRefunds, IReleaseSpec spec, long stateGasFloor) =>
        EthereumGasPolicy.ApplyCodeInsertRefunds(ref gas._gas, codeInsertRefunds, spec, stateGasFloor);

    public static bool TryConsumeSelfDestructGas(ref MilliGasPolicy<TPrices> gas)
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeSelfDestructGas(ref probe) && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeAccountAccessGasWithDelegation(ref MilliGasPolicy<TPrices> gas, IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker, bool isTracingAccess, Address address, Address? delegated)
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeAccountAccessGasWithDelegation(ref probe, spec, in accessTracker, isTracingAccess, address, delegated)
            && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeAccountAccessGas(ref MilliGasPolicy<TPrices> gas, IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker, bool isTracingAccess, Address address, AccountAccessKind kind = AccountAccessKind.Default)
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeAccountAccessGas(ref probe, spec, in accessTracker, isTracingAccess, address, kind)
            && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeStorageAccessGas(ref MilliGasPolicy<TPrices> gas, ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess, in StorageCell storageCell, StorageAccessType storageAccessType, IReleaseSpec spec)
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeStorageAccessGas(ref probe, in accessTracker, isTracingAccess, in storageCell, storageAccessType, spec)
            && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref MilliGasPolicy<TPrices> gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref probe, spec) && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeCallValueTransfer(ref MilliGasPolicy<TPrices> gas)
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeCallValueTransfer(ref probe) && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeCallValueTransferEip2780(ref MilliGasPolicy<TPrices> gas)
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeCallValueTransferEip2780(ref probe) && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeNewAccountCreation<TEip8037>(ref MilliGasPolicy<TPrices> gas) where TEip8037 : struct, IFlag
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeNewAccountCreation<TEip8037>(ref probe) && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeLogEmission(ref MilliGasPolicy<TPrices> gas, ulong topicCount, ulong dataSize)
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeLogEmission(ref probe, topicCount, dataSize) && ChargeProbe(ref gas, in probe);
    }

    public static bool TryConsumeDataCopyGas(ref MilliGasPolicy<TPrices> gas, IReleaseSpec spec, bool isExternalCode, ulong words)
    {
        EthereumGasPolicy probe = Probe();
        return EthereumGasPolicy.TryConsumeDataCopyGas(ref probe, spec, isExternalCode, words) && ChargeProbe(ref gas, in probe);
    }

    public static MilliGasPolicy<TPrices> Max(in MilliGasPolicy<TPrices> a, in MilliGasPolicy<TPrices> b) => a._gas.Value >= b._gas.Value ? a : b;

    public static IntrinsicGas<MilliGasPolicy<TPrices>> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit)
    {
        IntrinsicGas<EthereumGasPolicy> gas = EthereumGasPolicy.CalculateIntrinsicGas(tx, spec, blockGasLimit);
        return new IntrinsicGas<MilliGasPolicy<TPrices>>(new() { _gas = gas.Standard }, new() { _gas = gas.FloorGas });
    }

    /// <inheritdoc/>
    /// <remarks>An execution budget above <c>ulong.MaxValue / 1000</c> gas units is capped there.</remarks>
    public static bool TryCreateAvailableFromIntrinsic(ulong gasLimit, in MilliGasPolicy<TPrices> intrinsicGas, IReleaseSpec spec, out MilliGasPolicy<TPrices> available, bool capExecutionGas = true)
    {
        bool created = EthereumGasPolicy.TryCreateAvailableFromIntrinsic(gasLimit, in intrinsicGas._gas, spec, out EthereumGasPolicy availableGas, capExecutionGas);
        available = FromExecutionGasUnits(availableGas);
        return created;
    }

    private static ulong PreRefundGas<TGasPolicy>(in TGasPolicy gas, ulong txGasLimit) where TGasPolicy : struct, IGasPolicy<TGasPolicy> =>
        TGasPolicy.GetPreRefundGas(in gas, txGasLimit);

    /// <summary>The most gas a call or create can forward: all but one 64th of the whole gas units left (EIP-150).</summary>
    /// <remarks>Forwarded gas is always whole; the fraction of a gas unit stays with the caller.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MaxChildGas(ulong gasAvailable)
    {
        ulong wholeGas = gasAvailable / MilliGasPerGas;
        return wholeGas - wholeGas / 64;
    }

    private static MilliGasPolicy<TPrices> FromExecutionGasUnits(EthereumGasPolicy gas)
    {
        gas.Value = Math.Min(gas.Value, MaxGasUnits) * MilliGasPerGas;
        return new() { _gas = gas };
    }

    /// <summary>Turns <c>gas_left</c> into whole gas units for an <see cref="EthereumGasPolicy"/> state gas step.</summary>
    /// <returns>The milli-gas fraction for <see cref="LeaveGasUnits"/> to restore.</returns>
    /// <remarks>
    /// Exact: a step compares <c>gas_left</c> with whole gas units and adds or takes whole gas units, and
    /// <c>w * 1000 + f &gt;= c * 1000</c> holds for a fraction <c>f &lt; 1000</c> exactly when <c>w &gt;= c</c>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong EnterGasUnits(ref MilliGasPolicy<TPrices> gas)
    {
        ulong fraction = gas._gas.Value % MilliGasPerGas;
        gas._gas.Value /= MilliGasPerGas;
        return fraction;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LeaveGasUnits(ref MilliGasPolicy<TPrices> gas, ulong fraction) =>
        gas._gas.Value = gas._gas.Value * MilliGasPerGas + fraction;

    /// <summary>An <see cref="EthereumGasPolicy"/> that can pay any charge: execution gas from its gas left, state gas from its reservoir.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EthereumGasPolicy Probe() => new() { Value = ProbeGas, StateReservoir = ProbeStateGas };

    /// <summary>Charges what <paramref name="probe"/> paid: its execution gas, then its state gas, in the order EIP-8037 charges them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ChargeProbe(ref MilliGasPolicy<TPrices> gas, in EthereumGasPolicy probe) =>
        UpdateGas(ref gas, ProbeGas - probe.Value) && (probe.StateGasUsed == 0 || TryConsumeStateGas(ref gas, probe.StateGasUsed));
}

/// <summary>EIP-8053 constants.</summary>
public static class Eip8053Constants
{
    /// <summary>Milli-gas per gas unit.</summary>
    public const ulong MilliGasPerGas = 1_000;
}

/// <summary>Milli-gas prices of fixed opcode costs for <see cref="MilliGasPolicy{TPrices}"/>.</summary>
public interface IMilliGasPrices
{
    /// <summary>The milli-gas charged for the fixed cost <typeparamref name="TCost"/>.</summary>
    static abstract ulong MilliGasCost<TCost>() where TCost : struct, IGasCost;
}

/// <summary>Every fixed opcode cost at 1,000 milli-gas per gas unit.</summary>
/// <remarks>Metering then matches legacy gas exactly, which makes the legacy path a test oracle for the milli-gas one.</remarks>
public readonly struct ScaledGasPrices : IMilliGasPrices
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong MilliGasCost<TCost>() where TCost : struct, IGasCost => TCost.GasCost * Eip8053Constants.MilliGasPerGas;
}

/// <summary>Illustrative EIP-8053 compute prices in milli-gas.</summary>
/// <remarks>
/// Not the EIP-7904 numbers. They only show compute dropping below one gas unit while state, storage, log, call,
/// memory-expansion and precompile prices stay: <c>JUMPDEST</c> 1 to 0.1 gas, the very-low group (arithmetic,
/// comparison, bitwise and shift opcodes, <c>CALLDATALOAD</c>, <c>MLOAD</c>, <c>MSTORE</c>, <c>MSTORE8</c>,
/// <c>PUSHn</c>, <c>DUPn</c>, <c>SWAPn</c>) 3 to 1.5 gas, and <c>POP</c> 2 to 1 gas.
/// </remarks>
public readonly struct Eip8053IllustrativePrices : IMilliGasPrices
{
    private const ulong JumpDest = 100;
    private const ulong VeryLow = 1_500;
    private const ulong Pop = 1_000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong MilliGasCost<TCost>() where TCost : struct, IGasCost =>
        typeof(TCost) == typeof(JumpDestGasCost) ? JumpDest
        : typeof(TCost) == typeof(PopGasCost) ? Pop
        // BLOBHASH costs as much as the very-low group but is not part of it.
        : TCost.GasCost == GasCostOf.VeryLow && typeof(TCost) != typeof(BlobHashGasCost) ? VeryLow
        : ScaledGasPrices.MilliGasCost<TCost>();
}
