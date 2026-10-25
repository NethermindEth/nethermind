// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.TxPool")]

namespace Nethermind.Evm.GasPolicy;

/// <summary>
/// Unified Ethereum gas policy supporting both legacy single-dimensional behavior and EIP-8037
/// two-dimensional behavior when opcode dispatch and spec flags enable it.
/// </summary>
/// <remarks>
/// The spill split fields below follow EIP-8037 block-gas accounting.
/// </remarks>
public partial struct EthereumGasPolicy : IGasPolicy<EthereumGasPolicy>
{
    /// <summary>Execution gas budget (legacy gas_left).</summary>
    public ulong Value;
    /// <summary>State gas reservoir used by EIP-8037 paths.</summary>
    public long StateReservoir;
    /// <summary>Cumulative state gas used for block accounting.</summary>
    public long StateGasUsed;
    /// <summary>State gas drawn from this frame's execution gas pool.</summary>
    public long StateGasSpill;
    /// <summary>Most state gas this frame and its descendants can take from the reservoir (EIP-8037 allowance); used only when <see cref="HasStateGasAllowance"/> is set.</summary>
    public long StateGasAllowance;
    /// <summary>State gas this frame and its returned descendants took from the reservoir.</summary>
    /// <remarks>A revert or halt of a child drops the child's draws; refills never lower it.</remarks>
    public long StateGasDrawn;
    /// <summary>Set for call frames below the top level, which have an EIP-8037 state-gas allowance.</summary>
    public bool HasStateGasAllowance;
    /// <summary>Indicates that execution encountered an out of gas condition.</summary>
    public bool OutOfGas;
    /// <summary>When set, a state charge exceeding the reservoir halts rather than spilling into <see cref="Value"/> (EIP-8141: no EIP-8037 reservoir spill within a frame transaction).</summary>
    public bool IndependentStatePool;
    /// <summary>Legacy shares that this frame and its completed children paid from <see cref="Value"/> under <see cref="IndependentStatePool"/>.</summary>
    /// <remarks>EIP-8141 rebates them from the execution gas used of the frame transaction's frame at its exit. An exceptional halt discards them.</remarks>
    public long LegacySharePaid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy FromULong(ulong value) => new() { Value = value };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy FromFrameLimits(ulong executionGasLimit, ulong stateGasLimit) =>
        new()
        {
            Value = executionGasLimit,
            StateReservoir = stateGasLimit > long.MaxValue ? long.MaxValue : (long)stateGasLimit,
            IndependentStatePool = true
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) =>
        new()
        {
            Value = 0,
            StateReservoir = Eip8037Constants.SystemCallStateReservoir,
        };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryCreateSystemTransactionAvailableGas(ulong gasLimit, in EthereumGasPolicy intrinsicGas, IReleaseSpec spec, out EthereumGasPolicy available)
    {
        if (spec.IsEip8037Enabled)
        {
            long reservoir = Math.Min((long)gasLimit, intrinsicGas.StateReservoir);
            available = new EthereumGasPolicy
            {
                Value = gasLimit - (ulong)reservoir,
                StateReservoir = reservoir,
                StateGasUsed = intrinsicGas.StateGasUsed,
                StateGasSpill = 0,
            };
            return true;
        }

        return TryCreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec, out available);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ClearExecutionGas(ref EthereumGasPolicy gas) => gas.Value = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetRemainingGas(in EthereumGasPolicy gas) => gas.Value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateReservoir(in EthereumGasPolicy gas) => gas.StateReservoir;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasUsed(in EthereumGasPolicy gas) => gas.StateGasUsed;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetStateGasSpill(in EthereumGasPolicy gas) => gas.StateGasSpill;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetLegacySharePaid(in EthereumGasPolicy gas) => gas.LegacySharePaid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CalculateStateGasSpill(in EthereumGasPolicy gas, long stateGasCost)
    {
        if (stateGasCost <= 0)
        {
            return 0;
        }

        long reservoirContribution = Math.Min(gas.StateReservoir, RemainingStateGasAllowance(in gas));
        if (reservoirContribution <= 0)
        {
            return (ulong)stateGasCost;
        }

        return stateGasCost > reservoirContribution
            ? (ulong)(stateGasCost - reservoirContribution)
            : 0;
    }

    /// <summary>Subtracts <paramref name="cost"/> from the execution gas budget with no affordability check.</summary>
    /// <remarks>The caller must have already proven <c>gas.Value &gt;= cost</c>; otherwise the value underflows.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ConsumeRaw(ref EthereumGasPolicy gas, ulong cost) => gas.Value -= cost;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsume(ref EthereumGasPolicy gas, ulong cost)
    {
        if (gas.Value < cost) return false;
        ConsumeRaw(ref gas, cost);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeStateGas(ref EthereumGasPolicy gas, long stateGasCost) =>
        TryConsumeStateGas(ref gas, stateGasCost, legacyShare: 0);

    /// <summary>Charges state gas: <paramref name="legacyShare"/> from <see cref="Value"/>, then the reservoir, then <see cref="Value"/>.</summary>
    /// <remarks>
    /// The legacy share keeps the <c>gas_left</c> cost of state creation at least its cost before EIP-8037, so a frame
    /// cannot create state with less gas than before because the reservoir pays for it. The reservoir pays at most the
    /// frame's remaining allowance and <paramref name="drawLimit"/>; the rest comes from <see cref="Value"/>. An
    /// EIP-8141 frame pays the whole state gas from its own pool, so its state use stays within its state limit, and
    /// pays the legacy share from <see cref="Value"/> as an extra execution-gas charge, which
    /// <see cref="LegacySharePaid"/> rebates at frame exit.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryConsumeStateGas(ref EthereumGasPolicy gas, long stateGasCost, long legacyShare, long drawLimit = long.MaxValue)
    {
        long reservoir = gas.StateReservoir;
        if (gas.IndependentStatePool)
        {
            ulong share = (ulong)Math.Min(stateGasCost, legacyShare);
            if (reservoir < stateGasCost || gas.Value < share)
            {
                gas.OutOfGas = true;
                return false;
            }

            ConsumeRaw(ref gas, share);
            gas.StateReservoir -= stateGasCost;
            gas.StateGasUsed += stateGasCost;
            gas.LegacySharePaid += (long)share;
            return true;
        }

        long fromGasLeft = Math.Min(stateGasCost, legacyShare);
        long reservoirLimit = Math.Min(Math.Min(reservoir, RemainingStateGasAllowance(in gas)), drawLimit);
        long fromReservoir = Math.Max(0, Math.Min(reservoirLimit, stateGasCost - fromGasLeft));
        fromGasLeft = stateGasCost - fromReservoir;
        if (!TryConsume(ref gas, (ulong)fromGasLeft)) return false;

        gas.StateReservoir -= fromReservoir;
        gas.StateGasDrawn += fromReservoir;
        gas.StateGasUsed += stateGasCost;
        gas.StateGasSpill += fromGasLeft;
        return true;
    }

    public static bool TryConsumeStateAndExecutionGas(ref EthereumGasPolicy gas, long stateGasCost, ulong executionGasCost) =>
        (executionGasCost <= 0 || UpdateGas(ref gas, executionGasCost)) &&
        (stateGasCost <= 0 || TryConsumeStateGas(ref gas, stateGasCost));

    public static bool TryConsumeSelfDestructGas(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.SelfDestructEip150);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Refund(ref EthereumGasPolicy gas, in EthereumGasPolicy childGas)
    {
        gas.Value += childGas.Value;
        gas.StateReservoir += childGas.StateReservoir;
        gas.StateGasUsed += childGas.StateGasUsed;
        gas.StateGasSpill += childGas.StateGasSpill;
        gas.StateGasDrawn += childGas.StateGasDrawn;
        gas.LegacySharePaid += childGas.LegacySharePaid;
    }

    // On explicit REVERT, restore the child's remaining state reservoir plus its reverted
    // state gas usage.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGas(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas)
    {
        // State gas the child drew from gas_left also returns to the reservoir, never to gas_left:
        // spent gas must not come back to the caller. It stays counted as spill, so a later halt
        // of the parent still burns it.
        parentGas.StateReservoir += childGas.StateReservoir + childGas.StateGasUsed;
        parentGas.StateGasSpill += childGas.StateGasSpill;
        parentGas.LegacySharePaid += childGas.LegacySharePaid;
    }

    // On a child exceptional halt state gas produced no durable growth: restore the child's
    // reservoir and usage to the parent reservoir without adding to parent block-state usage.
    // Only the reservoir-funded portion returns; the spilled portion is burned with the child's gas_left.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RestoreChildStateGasOnHalt(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas) =>
        parentGas.StateReservoir += childGas.StateReservoir + childGas.StateGasUsed - childGas.StateGasSpill;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RevertRefundToHalt(ref EthereumGasPolicy parentGas, in EthereumGasPolicy childGas)
    {
        // Code deposit failure halts the child create frame after it merged into the parent;
        // spilled state gas is burned, so only the reservoir-funded portion returns.
        parentGas.StateGasSpill -= childGas.StateGasSpill;
        parentGas.StateReservoir += childGas.StateGasUsed - childGas.StateGasSpill;
        parentGas.StateGasUsed -= childGas.StateGasUsed;
        parentGas.StateGasDrawn -= childGas.StateGasDrawn;
        parentGas.LegacySharePaid -= childGas.LegacySharePaid;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeAccountAccessGasWithDelegation(ref EthereumGasPolicy gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        Address? delegated)
    {
        if (!spec.UseHotAndColdStorage)
            return true;

        bool notOutOfGas = TryConsumeAccountAccessGas(ref gas, spec, in accessTracker, isTracingAccess, address);
        return notOutOfGas && (delegated is null || TryConsumeAccountAccessGas(ref gas, spec, in accessTracker, isTracingAccess, delegated));
    }

    public static bool TryConsumeAccountAccessGas(ref EthereumGasPolicy gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        AccountAccessKind kind = AccountAccessKind.Default) =>
        TryConsumeAccountAccessGasCore<ColdAccountGasCost, DynamicStorageMode>(ref gas, spec, in accessTracker, isTracingAccess, address, kind);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryConsumeAccountAccessGasCore<TColdCost, TMode>(ref EthereumGasPolicy gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        AccountAccessKind kind)
        where TColdCost : struct, ISpecGasCost
        where TMode : struct, IStorageMode
    {
        if (!TMode.UseHotAndColdStorage(spec)) return true;
        if (isTracingAccess)
        {
            // Ensure that tracing simulates access-list behavior.
            accessTracker.WarmUp(address);
        }

        // WarmUp first so the warm path skips IsPrecompile; precompiles are pre-warmed at tx start.
        return (accessTracker.WarmUp(address) && !spec.IsPrecompile(address)) switch
        {
            true => UpdateGas(ref gas, TColdCost.GasCost(spec)),
            false when kind == AccountAccessKind.SelfDestructBeneficiary => true,
            false => UpdateGas(ref gas, GasCostOf.WarmStateRead)
        };
    }

    private readonly struct ColdAccountGasCost : ISpecGasCost
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GasCost(IReleaseSpec spec) => GetColdAccountAccessCost(spec);
    }

    private readonly struct ColdAccountGasCost<Eip8038> : ISpecGasCost where Eip8038 : struct, IFlag
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong GasCost(IReleaseSpec spec) => Eip8038.IsActive ? Eip8038Constants.ColdAccountAccess : GasCostOf.ColdAccountAccess;
    }

    /// <inheritdoc cref="IGasPolicy{TSelf}.GetColdAccountAccessCost"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetColdAccountAccessCost(IReleaseSpec spec) => spec.GasCosts.ColdAccountAccessCost;

    public static bool TryConsumeStorageAccessGas(ref EthereumGasPolicy gas,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec) =>
        TryConsumeStorageAccessGasCore<DynamicStorageMode>(ref gas, in accessTracker, isTracingAccess, in storageCell, storageAccessType, spec);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeNetMeteredSStoreGas<Eip8038>(ref EthereumGasPolicy gas, IReleaseSpec spec)
        where Eip8038 : struct, IFlag =>
        Eip8038.IsActive || UpdateGas(ref gas, spec.GasCosts.NetMeteredSStoreCost);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeExpBytes<Eip160>(ref EthereumGasPolicy gas, IReleaseSpec spec, ulong exponentByteSize)
        where Eip160 : struct, IFlag =>
        UpdateGas(ref gas, (Eip160.IsActive ? GasCostOf.ExpByteEip160 : GasCostOf.ExpByte) * exponentByteSize);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeSLoadBaseGas<Eip2929>(ref EthereumGasPolicy gas, IReleaseSpec spec)
        where Eip2929 : struct, IFlag =>
        Eip2929.IsActive || UpdateGas(ref gas, spec.GasCosts.SLoadCost);

    private readonly struct StorageMode<Eip2929, Eip8038> : IStorageMode
        where Eip2929 : struct, IFlag
        where Eip8038 : struct, IFlag
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool UseHotAndColdStorage(IReleaseSpec spec) => Eip2929.IsActive;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEip8038Enabled(IReleaseSpec spec) => Eip8038.IsActive;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeStorageAccessGas<Eip2929, Eip8038>(ref EthereumGasPolicy gas,
        ref readonly StackAccessTracker accessTracker, bool isTracingAccess,
        in StorageCell storageCell, StorageAccessType storageAccessType, IReleaseSpec spec)
        where Eip2929 : struct, IFlag
        where Eip8038 : struct, IFlag =>
        TryConsumeStorageAccessGasCore<StorageMode<Eip2929, Eip8038>>(ref gas, in accessTracker, isTracingAccess, in storageCell, storageAccessType, spec);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeAccountAccessGas<Eip2929, Eip8038>(ref EthereumGasPolicy gas, IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker, bool isTracingAccess, Address address,
        AccountAccessKind kind = AccountAccessKind.Default)
        where Eip2929 : struct, IFlag
        where Eip8038 : struct, IFlag =>
        TryConsumeAccountAccessGasCore<ColdAccountGasCost<Eip8038>, StorageMode<Eip2929, Eip8038>>(ref gas, spec, in accessTracker, isTracingAccess, address, kind);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeCreateGas<Eip8037, TOpCreate, Eip3860, Eip8038>(ref EthereumGasPolicy gas, IReleaseSpec spec, ulong initCodeWords)
        where Eip8037 : struct, IFlag
        where TOpCreate : struct, EvmInstructions.IOpCreate
        where Eip3860 : struct, IFlag
        where Eip8038 : struct, IFlag
    {
        ulong baseCost = CreateGasCost.Base<TOpCreate>(Eip8038.IsActive, Eip8037.IsActive);
        ulong initCodeWordCost = Eip3860.IsActive ? GasCostOf.InitCodeWord * initCodeWords : 0;
        return UpdateGas(ref gas, baseCost + initCodeWordCost + CreateGasCost.InitCodeHashing<TOpCreate>(initCodeWords));
    }

    private interface IStorageMode
    {
        static abstract bool IsEip8038Enabled(IReleaseSpec spec);
        static abstract bool UseHotAndColdStorage(IReleaseSpec spec);
    }

    private readonly struct DynamicStorageMode : IStorageMode
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool UseHotAndColdStorage(IReleaseSpec spec) => spec.UseHotAndColdStorage;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEip8038Enabled(IReleaseSpec spec) => spec.IsEip8038Enabled;
    }

    private readonly struct StorageMode<Eip8038> : IStorageMode where Eip8038 : struct, IFlag
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool UseHotAndColdStorage(IReleaseSpec spec) => spec.UseHotAndColdStorage;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsEip8038Enabled(IReleaseSpec spec) => Eip8038.IsActive;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryConsumeStorageAccessGasCore<TMode>(ref EthereumGasPolicy gas,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
        where TMode : struct, IStorageMode
    {
        if (!TMode.UseHotAndColdStorage(spec))
            return true;
        if (isTracingAccess)
        {
            bool isCold = accessTracker.IsCold(in storageCell);
            bool gasAvailable = storageAccessType != StorageAccessType.SLOAD && !TMode.IsEip8038Enabled(spec)
                || UpdateGas(ref gas, GasCostOf.WarmStateRead);

            if (gasAvailable && isCold)
                accessTracker.WarmUp(in storageCell);

            return gasAvailable;
        }

        // Warming before the charge is unobservable: running out of gas halts the frame, whose restore drops the cell again.
        if (accessTracker.WarmUp(in storageCell))
            return UpdateGas(ref gas, TMode.IsEip8038Enabled(spec) ? Eip8038Constants.ColdStorageAccess : GasCostOf.ColdSLoad);

        // EIP-8038 charges the warm-access cost on SSTORE too; the net-metered charge is dropped.
        if (storageAccessType == StorageAccessType.SLOAD || TMode.IsEip8038Enabled(spec))
            return UpdateGas(ref gas, GasCostOf.WarmStateRead);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(ref EthereumGasPolicy gas,
        in UInt256 position,
        in UInt256 length, ref EvmPooledMemory memory)
    {
        ulong size = memory.Size;
        if (length.IsUint64 && length.u0 <= size && position.IsUint64 && position.u0 <= size - length.u0)
            return true;

        ulong memoryCost = memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (memoryCost == 0L)
            return !outOfGas;
        return UpdateGas(ref gas, memoryCost);
    }

    /// <remarks>
    /// The fast paths of MLOAD and MSTORE in the untraced tables (<c>TryExecuteFast</c> in VirtualMachine.OpcodeHandlers.cs)
    /// charge a word's access without calling this; a change to this charge must be made there too.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(ref EthereumGasPolicy gas,
        in UInt256 position,
        ulong length, ref EvmPooledMemory memory)
    {
        ulong size = memory.Size;
        if (length <= size && position.IsUint64 && position.u0 <= size - length)
            return true;

        ulong memoryCost = memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (memoryCost == 0)
            return !outOfGas;
        return UpdateGas(ref gas, memoryCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(ref EthereumGasPolicy gas,
        ulong gasCost)
    {
        if (GetRemainingGas(in gas) < gasCost)
        {
            gas.Value = 0;
            return false;
        }

        ConsumeRaw(ref gas, gasCost);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref EthereumGasPolicy gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag =>
        TryConsumeStorageWriteCore<TEip8037, TIsSlotCreation, DynamicStorageMode>(ref gas, spec);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeStorageWrite<Eip8037, TIsSlotCreation, Eip8038>(ref EthereumGasPolicy gas, IReleaseSpec spec)
        where Eip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag
        where Eip8038 : struct, IFlag =>
        TryConsumeStorageWriteCore<Eip8037, TIsSlotCreation, StorageMode<Eip8038>>(ref gas, spec);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryConsumeStorageWriteCore<TEip8037, TIsSlotCreation, TMode>(ref EthereumGasPolicy gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag
        where TMode : struct, IStorageMode
    {
        // EIP-8038: STORAGE_WRITE is charged on the first change to a slot (fresh or reset).
        if (!TIsSlotCreation.IsActive)
            return UpdateGas(ref gas, TMode.IsEip8038Enabled(spec) ? Eip8038Constants.StorageWrite : spec.GasCosts.SStoreResetCost);

        ulong executionWriteCost = TMode.IsEip8038Enabled(spec) ? Eip8038Constants.StorageWrite : GasCostOf.SSetExecution;
        return TEip8037.IsActive switch
        {
            // EIP-8037: charge the execution component first so an OOG halt does not
            // spill state gas into gas_left and then restore it to the parent frame.
            true => UpdateGas(ref gas, executionWriteCost)
                && TryConsumeStateGas(ref gas, GasCostOf.SSetState, legacyShare: executionWriteCost < GasCostOf.SSet ? (long)(GasCostOf.SSet - executionWriteCost) : 0),
            false => UpdateGas(ref gas, GasCostOf.SSet),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(ref EthereumGasPolicy gas,
        ulong refund) => gas.Value += refund;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReserveChildGas(ref EthereumGasPolicy gas, in UInt256 requestedGas, IReleaseSpec spec, out ulong childGas) =>
        TryReserveChildGasCore(ref gas, in requestedGas, spec.Use63Over64Rule, out childGas);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReserveChildGas<Eip150>(ref EthereumGasPolicy gas, in UInt256 requestedGas, IReleaseSpec spec, out ulong childGas)
        where Eip150 : struct, IFlag =>
        TryReserveChildGasCore(ref gas, in requestedGas, Eip150.IsActive, out childGas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReserveChildGasCore(ref EthereumGasPolicy gas, in UInt256 requestedGas, bool use63Over64Rule, out ulong childGas)
    {
        ulong gasAvailable = GetRemainingGas(in gas);
        if (use63Over64Rule)
        {
            ulong cap = gasAvailable - gasAvailable / 64;
            childGas = requestedGas.IsUint64 && requestedGas.u0 <= cap ? requestedGas.u0 : cap;
        }
        else
        {
            if (!requestedGas.IsUint64)
            {
                childGas = 0;
                return false;
            }

            childGas = requestedGas.u0;
        }

        return TryReserveChildGas(ref gas, childGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReserveChildGas(ref EthereumGasPolicy gas, IReleaseSpec spec, out ulong childGas) =>
        TryReserveChildGasCore(ref gas, spec.Use63Over64Rule, out childGas);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryReserveChildGas<Eip150>(ref EthereumGasPolicy gas, IReleaseSpec spec, out ulong childGas)
        where Eip150 : struct, IFlag =>
        TryReserveChildGasCore(ref gas, Eip150.IsActive, out childGas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReserveChildGasCore(ref EthereumGasPolicy gas, bool use63Over64Rule, out ulong childGas)
    {
        ulong gasAvailable = GetRemainingGas(in gas);
        childGas = use63Over64Rule ? gasAvailable - gasAvailable / 64 : gasAvailable;
        return TryReserveChildGas(ref gas, childGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReserveChildGas(ref EthereumGasPolicy gas, ulong childGas)
    {
        if (gas.Value < childGas)
        {
            gas.Value = 0;
            return false;
        }

        gas.Value -= childGas;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RefundStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor)
    {
        Debug.Assert(amount >= 0, $"Negative state-gas refund ({amount}).");
        // Refills go to the reservoir, never to gas_left, so gas_left never increases within a frame.
        long refundableStateGas = Math.Max(0, gas.StateGasUsed - stateGasFloor);
        long appliedRefund = Math.Min(amount, refundableStateGas);
        gas.StateReservoir += appliedRefund;
        gas.StateGasUsed -= appliedRefund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long DiscardStateGas(ref EthereumGasPolicy gas, long amount, long stateGasFloor)
    {
        long discardableStateGas = Math.Max(0, gas.StateGasUsed - stateGasFloor);
        long appliedRefund = Math.Min(amount, discardableStateGas);
        gas.StateGasUsed -= appliedRefund;
        return amount - appliedRefund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddStateGasRefundToReservoir(ref EthereumGasPolicy gas, long amount) =>
        gas.StateReservoir += amount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RemainingStateGasAllowance(in EthereumGasPolicy gas) =>
        gas.HasStateGasAllowance ? Math.Max(0, gas.StateGasAllowance - gas.StateGasDrawn) : long.MaxValue;

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeCodeDepositGas(ref EthereumGasPolicy gas, in EthereumGasPolicy createFrameGas, long stateGasCost, ulong executionGasCost) =>
        (executionGasCost <= 0 || UpdateGas(ref gas, executionGasCost)) &&
        (stateGasCost <= 0 || TryConsumeStateGas(ref gas, stateGasCost, legacyShare: 0, RemainingStateGasAllowance(in createFrameGas)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RemoveStateGasRefundFromReservoir(ref EthereumGasPolicy gas, long amount)
    {
        // Revoke what is still parked in the reservoir (never fabricating spill debt), then what
        // the credit funded from usage; any remainder refilled a net-spill hole, so restore the debt.
        long fromReservoir = Math.Clamp(gas.StateReservoir, 0, amount);
        gas.StateReservoir -= fromReservoir;
        amount -= fromReservoir;

        if (amount > 0)
        {
            long fromUsed = Math.Min(amount, gas.StateGasUsed);
            gas.StateGasUsed -= fromUsed;
            gas.StateReservoir -= amount - fromUsed;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ResetForHalt(ref EthereumGasPolicy gas, long initialStateReservoir, long initialStateGasUsed)
    {
        // Snap state-gas back to its tx-start shape (reservoir=R0, used=intrinsic floor,
        // spill=0). The post-reset StateGasUsed feeds SpentGas so the user does not keep
        // paying for state-gas that did not commit.
        gas.StateReservoir = initialStateReservoir;
        gas.StateGasUsed = initialStateGasUsed;
        gas.StateGasSpill = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FoldTopFrameStateGas(ref EthereumGasPolicy gas, ref EthereumGasPolicy baseline, long stateGasUsed)
    {
        if (stateGasUsed <= 0)
        {
            return;
        }

        baseline.StateReservoir += stateGasUsed;
        baseline.StateGasUsed += stateGasUsed;
        gas.StateGasSpill = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetCodeInsertExecutionRefund(ulong codeInsertRefunds, IReleaseSpec spec)
    {
        if (codeInsertRefunds == 0UL) return 0;
        if (spec.IsEip8037Enabled) return 0;
        return (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ApplyCodeInsertRefunds(ref EthereumGasPolicy gas, ulong codeInsertRefunds, IReleaseSpec spec, long stateGasFloor)
        => GetCodeInsertExecutionRefund(codeInsertRefunds, spec);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeCallValueTransfer(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, GasCostOf.CallValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeCallValueTransferEip2780(ref EthereumGasPolicy gas)
        => UpdateGas(ref gas, Eip8038Constants.CallValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeNewAccountCreation<TEip8037>(ref EthereumGasPolicy gas) where TEip8037 : struct, IFlag => TEip8037.IsActive switch
    {
        true => TryConsumeStateGas(ref gas, GasCostOf.NewAccountState, Eip8037Constants.NewAccountLegacyShare),
        false => UpdateGas(ref gas, GasCostOf.NewAccount)
    };

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeCreateStateGas(ref EthereumGasPolicy gas) =>
        TryConsumeStateGas(ref gas, GasCostOf.CreateState, Eip8037Constants.CreateLegacyShare);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeLogEmission(ref EthereumGasPolicy gas, ulong topicCount, ulong dataSize)
    {
        ulong cost = GasCostOf.Log + topicCount * GasCostOf.LogTopic + dataSize * GasCostOf.LogData;
        return UpdateGas(ref gas, cost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryConsumeDataCopyGas(ref EthereumGasPolicy gas, IReleaseSpec spec, bool isExternalCode, ulong words)
        => UpdateGas(ref gas, (isExternalCode ? spec.GasCosts.ExtCodeCost : GasCostOf.VeryLow) + GasCostOf.Memory * words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy Max(in EthereumGasPolicy a, in EthereumGasPolicy b) =>
        a.Value >= b.Value ? a : b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CombineBlockGas(ulong blockExecutionGas, ulong blockStateGas) =>
        Math.Max(blockExecutionGas, blockStateGas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EthereumGasPolicy CreateChildFrameGas(ref EthereumGasPolicy parentGas, ulong childExecutionGas)
    {
        long childStateReservoir = parentGas.StateReservoir;
        parentGas.StateReservoir = 0;

        // EIP-8037: the child can take from the reservoir what its gas bought at the pre-EIP-8037 code-deposit price,
        // within what is left of its parent's allowance. EIP-8141 frame transactions keep their own state-gas limits.
        bool hasAllowance = !parentGas.IndependentStatePool;
        long allowance = 0;
        if (hasAllowance)
        {
            allowance = (long)Math.Min(childExecutionGas, (ulong)(long.MaxValue / GasCostOf.CostPerStateByte))
                * GasCostOf.CostPerStateByte / Eip8037Constants.LegacyGasPerStateByte;
            allowance = Math.Min(allowance, RemainingStateGasAllowance(in parentGas));
        }

        return new EthereumGasPolicy
        {
            Value = childExecutionGas,
            StateReservoir = childStateReservoir,
            IndependentStatePool = parentGas.IndependentStatePool,
            StateGasAllowance = allowance,
            HasStateGasAllowance = hasAllowance,
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);

    /// <inheritdoc/>
    /// <param name="blockGasLimit">Unused by the Ethereum policy; intrinsic gas is independent of the block gas limit.</param>
    public static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit)
    {
        bool isEip2780SelfTransfer = IsEip2780SelfTransfer(tx, spec);
        if (TryGetMemoizedIntrinsicGas(tx, spec, isEip2780SelfTransfer, out IntrinsicGas<EthereumGasPolicy> gas))
        {
            return gas;
        }

        gas = CalculateIntrinsicGasWithoutMemo(tx, spec, isEip2780SelfTransfer);
        Volatile.Write(ref tx.IntrinsicGasMemo, new IntrinsicGasMemo(spec, isEip2780SelfTransfer, gas));
        return gas;
    }

    /// <remarks>Reads the intrinsic-gas memo but never writes it, so a caller that skips memoization keeps it unset.</remarks>
    internal static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGasAsEip2780SelfTransfer(Transaction tx, IReleaseSpec spec)
    {
        bool isEip2780SelfTransfer = IsEip2780SelfTransfer(tx, spec);
        if (!TryGetMemoizedIntrinsicGas(tx, spec, isEip2780SelfTransfer, out IntrinsicGas<EthereumGasPolicy> gas))
        {
            gas = CalculateIntrinsicGasWithoutMemo(tx, spec, isEip2780SelfTransfer);
        }

        ulong eip2780ExtraGas = Eip2780ExtraGas(tx, spec, isEip2780SelfTransfer);
        EthereumGasPolicy standard = gas.Standard;
        standard.Value -= eip2780ExtraGas;
        EthereumGasPolicy floor = gas.FloorGas;
        floor.Value = floor.Value.SaturatingSub(eip2780ExtraGas);
        return new IntrinsicGas<EthereumGasPolicy>(standard, floor);
    }

    private static bool TryGetMemoizedIntrinsicGas(Transaction tx, IReleaseSpec spec, bool isEip2780SelfTransfer, out IntrinsicGas<EthereumGasPolicy> gas)
    {
        if (Volatile.Read(ref tx.IntrinsicGasMemo) is IntrinsicGasMemo memo
            && ReferenceEquals(memo.Spec, spec)
            && memo.IsEip2780SelfTransfer == isEip2780SelfTransfer)
        {
            gas = memo.Gas;
            return true;
        }

        gas = default;
        return false;
    }

    private sealed record IntrinsicGasMemo(IReleaseSpec Spec, bool IsEip2780SelfTransfer, IntrinsicGas<EthereumGasPolicy> Gas) : IIntrinsicGasMemo;

    internal static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGasWithoutMemo(Transaction tx, IReleaseSpec spec) =>
        CalculateIntrinsicGasWithoutMemo(tx, spec, IsEip2780SelfTransfer(tx, spec));

    /// <summary>
    /// Whether EIP-2780 prices <paramref name="tx"/> as a transfer to its own sender. Read once per calculation: a
    /// sender recovered concurrently must not price one part of the charge as a self-transfer and another as not.
    /// </summary>
    private static bool IsEip2780SelfTransfer(Transaction tx, IReleaseSpec spec) =>
        spec.IsEip2780Enabled && tx.To is not null && tx.SenderAddress == tx.To;

    private static IntrinsicGas<EthereumGasPolicy> CalculateIntrinsicGasWithoutMemo(Transaction tx, IReleaseSpec spec, bool isEip2780SelfTransfer)
    {
        ulong tokensInCallData = IntrinsicGasCalculator.CalculateTokensInCallData(tx, spec);
        ulong floorTokensInAccessList = IntrinsicGasCalculator.CalculateFloorTokensInAccessList(tx, spec);
        (ulong authExecutionCost, long authStateCost) = IntrinsicGasCalculator.AuthorizationListCost(tx, spec);
        ulong accessListCost = IntrinsicGasCalculator.AccessListCost(tx, spec, floorTokensInAccessList);

        ulong baseCost = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        ulong createCost = CreateCost(tx, spec);
        ulong eip2780ExtraGas = Eip2780ExtraGas(tx, spec, isEip2780SelfTransfer);
        ulong executionGas = baseCost
                          + DataCost(tx, spec, tokensInCallData)
                          + createCost
                          + accessListCost
                          + authExecutionCost
                          + eip2780ExtraGas;
        ulong floorBase = spec.IsEip2780Enabled ? baseCost + createCost + eip2780ExtraGas : baseCost;
        ulong floorCost = IntrinsicGasCalculator.CalculateFloorCost(tx, spec, floorBase, tokensInCallData, floorTokensInAccessList);
        long totalStateCost = authStateCost;
        return spec.IsEip8037Enabled
            ? new IntrinsicGas<EthereumGasPolicy>(
                new EthereumGasPolicy
                {
                    Value = executionGas,
                    StateReservoir = totalStateCost,
                    StateGasUsed = totalStateCost,
                },
                FromULong(floorCost))
            : new IntrinsicGas<EthereumGasPolicy>(FromULong(executionGas), FromULong(floorCost));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryCreateAvailableFromIntrinsic(ulong gasLimit, in EthereumGasPolicy intrinsicGas, IReleaseSpec spec, out EthereumGasPolicy available, bool capExecutionGas = true)
    {
        ulong intrinsicTotal = intrinsicGas.Value + (ulong)intrinsicGas.StateReservoir;
        if (gasLimit < intrinsicTotal)
        {
            available = default;
            return false;
        }

        ulong evmGas = gasLimit - intrinsicTotal;
        ulong reservoir = 0;

        if (spec.IsEip8037Enabled && capExecutionGas)
        {
            // EIP-8037: cap gas_left at TX_MAX_GAS_LIMIT - intrinsic_execution, overflow goes to reservoir
            ulong maxGasLeft = Eip7825Constants.DefaultTxGasLimitCap.SaturatingSub(intrinsicGas.Value);
            reservoir = evmGas.SaturatingSub(maxGasLeft);
            evmGas -= reservoir;
        }

        available = new EthereumGasPolicy
        {
            Value = evmGas,
            StateReservoir = (long)reservoir,
            StateGasUsed = intrinsicGas.StateReservoir,
            StateGasSpill = 0,
        };
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CreateCost(Transaction tx, IReleaseSpec spec) =>
        tx.IsContractCreation && spec.IsEip2Enabled
            ? (spec.IsEip8038Enabled ? Eip8038Constants.CreateAccess
                : spec.IsEip8037Enabled ? GasCostOf.CreateExecution
                : GasCostOf.TxCreate)
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong DataCost(Transaction tx, IReleaseSpec spec, ulong tokensInCallData) =>
        spec.GetBaseDataCost(tx) + tokensInCallData * GasCostOf.TxDataZero;

    /// <summary>
    /// EIP-2780 recipient charge on top of TX_BASE_COST.
    /// </summary>
    /// <remarks>
    /// State-independent by design: a flat cold touch for a non-self recipient, plus TX_VALUE_COST on
    /// value transfers (the EIP-7708 transfer log is folded into TX_VALUE_COST). Contract creation's
    /// recipient balance write is already covered by the create charge (CREATE_ACCESS under EIP-8038),
    /// so it adds nothing here even with value. New-account and delegation costs are charged elsewhere.
    /// </remarks>
    private static ulong Eip2780ExtraGas(Transaction tx, IReleaseSpec spec, bool isEip2780SelfTransfer)
    {
        if (!spec.IsEip2780Enabled) return 0;

        // The create charge (CREATE_ACCESS under EIP-8038) already covers the recipient balance write; a value endowment adds nothing.
        if (tx.IsContractCreation) return 0;

        // Self-transfers coalesce into the sender leaf write already priced into TX_BASE_COST.
        if (isEip2780SelfTransfer) return 0;

        ulong cost = GetColdAccountAccessCost(spec);
        if (!tx.Value.IsZero)
            cost += GasCostOf.TxValueCostEip2780;

        return cost;
    }
}
