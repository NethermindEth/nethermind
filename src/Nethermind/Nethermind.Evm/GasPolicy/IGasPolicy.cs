// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    static abstract TSelf FromULong(ulong value);

    static virtual TSelf CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) => TSelf.FromULong(0);

    static virtual TSelf CreateSystemTransactionAvailableGas(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec) =>
        TSelf.CreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec);

    static abstract ulong GetRemainingGas(in TSelf gas);

    // EIP-8037 state-cost accessors. Pre-EIP-8037 policies return the constant fallback.
    static virtual ulong GetStorageSetStateCost(in TSelf gas) => GasCostOf.SSetState;
    static virtual ulong GetCreateStateCost(in TSelf gas) => GasCostOf.CreateState;
    static virtual ulong GetNewAccountStateCost(in TSelf gas) => GasCostOf.NewAccountState;
    static virtual ulong GetPerAuthBaseStateCost(in TSelf gas) => GasCostOf.PerAuthBaseState;
    static virtual ulong GetCodeDepositStateCost(in TSelf gas, int byteCodeLength) => GasCostOf.CodeDepositState * (ulong)byteCodeLength;
    static virtual ulong GetStorageSetReversalRefund(in TSelf gas) => RefundOf.SSetReversedEip8037;

    // EIP-8037 state-accounting accessors. Pre-EIP-8037 policies return 0.
    static virtual ulong GetStateReservoir(in TSelf gas) => 0;
    static virtual ulong GetStateGasUsed(in TSelf gas) => 0;
    static virtual ulong GetStateGasSpill(in TSelf gas) => 0;
    // Tx-wide cumulative spill paid via gas_left in reverted child frames; never undone.
    // Used by top-level halt to reattribute burned spill from state to regular dimension.
    static virtual ulong GetStateGasSpillBurned(in TSelf gas) => 0;
    // Spill from reverted children that remains in block regular after in-frame state refund.
    static virtual ulong GetStateGasSpillReclassified(in TSelf gas) => 0;
    // Spill whose state side was refunded but regular side stays spent; excluded from
    // Calculate8037BlockRegularGas subtraction.
    static virtual ulong GetStateGasSpillRefunded(in TSelf gas) => 0;

    static abstract void Consume(ref TSelf gas, ulong cost);
    static virtual void ConsumeUnchecked(ref TSelf gas, ulong cost) => TSelf.Consume(ref gas, cost);

    static abstract bool ConsumeSelfDestructGas(ref TSelf gas);
    static abstract void ConsumeCodeDeposit(ref TSelf gas, ulong cost);
    static abstract void Refund(ref TSelf gas, in TSelf childGas);

    // Revert path: restore the child's state gas into the parent reservoir.
    static virtual void RestoreChildStateGas(ref TSelf parentGas, in TSelf childGas) { }
    // Halt path: preserve inline state-gas refunds (call chain resets to top-most failing call).
    static virtual void RestoreChildStateGasOnHalt(ref TSelf parentGas, in TSelf childGas) { }
    // Code-deposit-failure path: undo prior Refund's state-gas merge and apply halt restoration.
    static virtual void RevertRefundToHalt(ref TSelf parentGas, in TSelf childGas) { }

    static abstract bool IsOutOfGas(in TSelf gas);

    static abstract void SetOutOfGas(ref TSelf gasState);

    static abstract bool ConsumeAccountAccessGasWithDelegation(ref TSelf gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        Address? delegated);

    static abstract bool ConsumeAccountAccessGas(ref TSelf gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        AccountAccessKind kind = AccountAccessKind.Default);

    static abstract bool ConsumeStorageAccessGas(ref TSelf gas,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec);

    static abstract bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, in UInt256 length, VmState<TSelf> vmState);

    static virtual bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, ulong length, VmState<TSelf> vmState)
    {
        UInt256 uint256Length = new(length);
        return TSelf.UpdateMemoryCost(ref gas, in position, in uint256Length, vmState);
    }

    static abstract bool UpdateGas(ref TSelf gas, ulong gasCost);

    // Pre-EIP-8037 fallback: state gas folded into regular gas.
    static virtual bool ConsumeStateGas(ref TSelf gas, ulong stateGasCost) => TSelf.UpdateGas(ref gas, stateGasCost);

    // Regular gas charged first to prevent state-gas spill-then-halt from inflating
    // the reservoir via the error refund path.
    static abstract bool TryConsumeStateAndRegularGas(ref TSelf gas, ulong stateGasCost, ulong regularGasCost);

    static abstract void UpdateGasUp(ref TSelf gas, ulong refund);

    static abstract bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref TSelf gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag;

    // Pre-EIP-8037 fallback: refund into regular gas.
    static virtual void RefundStateGas(ref TSelf gas, ulong amount, ulong stateGasFloor) => TSelf.UpdateGasUp(ref gas, amount);
    static virtual void RefundStateGas(ref TSelf gas, ulong amount, ulong stateGasFloor, bool trackSpillRefund) =>
        TSelf.RefundStateGas(ref gas, amount, stateGasFloor);

    // Drop state-gas from block-state accounting without refunding to the gas budget;
    // reverted state charges stay paid by the tx but don't contribute to committed state gas.
    static virtual ulong DiscardStateGas(ref TSelf gas, ulong amount, ulong stateGasFloor, bool trackSpillRefund) => amount;

    static virtual void AddStateGasRefundToReservoir(ref TSelf gas, ulong amount, bool trackSpillRefund) =>
        TSelf.UpdateGasUp(ref gas, amount);

    static virtual void RemoveStateGasRefundFromReservoir(ref TSelf gas, ulong amount) { }

    // EIP-8037 top-level halt: snap state-gas back to (R0, intrinsicStateUsed, 0); the
    // post-reset StateGasUsed feeds SpentGas so the user doesn't pay for uncommitted state.
    static virtual void ResetForHalt(ref TSelf gas, ulong initialStateReservoir, ulong initialStateGasUsed) { }

    // EIP-7702 code-insert refund regular-gas portion. Pre-EIP-8037: (NewAccount - PerAuthBaseCost) each.
    static virtual ulong GetCodeInsertRegularRefund(ulong codeInsertRefunds, IReleaseSpec spec) =>
        codeInsertRefunds > 0UL ? (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds : 0UL;

    // EIP-8037: replenishes tx state reservoir before exec (intrinsic state gas already charged).
    static virtual ulong ApplyCodeInsertRefunds(ref TSelf gas, ulong codeInsertRefunds, IReleaseSpec spec, ulong stateGasFloor) =>
        TSelf.GetCodeInsertRegularRefund(codeInsertRefunds, spec);

    static abstract bool ConsumeCallValueTransfer(ref TSelf gas);
    static abstract bool ConsumeNewAccountCreation<TEip8037>(ref TSelf gas) where TEip8037 : struct, IFlag;
    static abstract bool ConsumeLogEmission(ref TSelf gas, ulong topicCount, ulong dataSize);
    static abstract TSelf Max(in TSelf a, in TSelf b);

    static virtual IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        TSelf.CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);
    static abstract IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit);

    static abstract TSelf CreateAvailableFromIntrinsic(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec);

    static virtual TSelf CreateChildFrameGas(ref TSelf parentGas, ulong childRegularGas) => TSelf.FromULong(childRegularGas);

    // EXTCODECOPY may need different categorization (state trie access) for some policies.
    static abstract void ConsumeDataCopyGas(ref TSelf gas, bool isExternalCode, ulong baseCost, ulong dataCost);

    static abstract void OnBeforeInstructionTrace(in TSelf gas, int pc, Instruction instruction, int depth);
    static abstract void OnAfterInstructionTrace(in TSelf gas);

    protected static ulong CalculateTokensInCallData(Transaction transaction, IReleaseSpec spec)
    {
        ReadOnlySpan<byte> data = transaction.Data.Span;
        int totalZeros = data.CountZeros();
        return (ulong)totalZeros + (ulong)(data.Length - totalZeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }

    // 0 when floor pricing is not active.
    public static ulong CalculateFloorTokensInAccessList(Transaction transaction, IReleaseSpec spec) =>
        spec.IsEip7981Enabled && transaction.AccessList is { Count: (int addressesCount, int storageKeysCount) }
            ? (ulong)(addressesCount * Address.Size + storageKeysCount * AccessList.StorageKeySize) * spec.GasCosts.TxDataNonZeroMultiplier
            : 0;

    public static ulong AccessListCost(Transaction transaction, IReleaseSpec spec, ulong floorTokensInAccessList)
    {
        AccessList? accessList = transaction.AccessList;
        if (accessList is null) return 0;

        if (!spec.UseTxAccessLists)
        {
            ThrowInvalidDataException(spec);
        }

        (int addressesCount, int storageKeysCount) = accessList.Count;
        return (ulong)addressesCount * GasCostOf.AccessAccountListEntry
            + (ulong)storageKeysCount * GasCostOf.AccessStorageListEntry
            + spec.GasCosts.TotalCostFloorPerToken * floorTokensInAccessList;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidDataException(IReleaseSpec spec) =>
            throw new InvalidDataException($"Transaction with an access list received within the context of {spec.Name}. EIP-2930 is not enabled.");
    }

    public static (ulong RegularCost, ulong StateCost) AuthorizationListCost(Transaction transaction, IReleaseSpec spec)
    {
        AuthorizationTuple[]? authList = transaction.AuthorizationList;
        if (authList is null)
        {
            return (0, 0);
        }

        if (!spec.IsAuthorizationListEnabled)
        {
            ThrowAuthorizationListNotEnabled(spec);
        }

        ulong authCount = (ulong)authList.Length;
        return spec.IsEip8037Enabled
            ? (
                authCount * GasCostOf.PerAuthBaseRegular,
                authCount * (GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState)
            )
            : (authCount * GasCostOf.NewAccount, 0);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowAuthorizationListNotEnabled(IReleaseSpec releaseSpec) =>
            throw new InvalidDataException($"Transaction with an authorization list received within the context of {releaseSpec.Name}. EIP-7702 is not enabled.");
    }

    private static ulong CalculateFloorTokensInCallData(Transaction transaction, IReleaseSpec spec) =>
        (ulong)transaction.Data.Length * spec.GasCosts.TxDataNonZeroMultiplier;

    protected static ulong CalculateFloorCost(Transaction transaction, IReleaseSpec spec, ulong tokensInCallData, ulong floorTokensInAccessList) => spec switch
    {
        { IsEip7976Enabled: true } => GasCostOf.Transaction + (CalculateFloorTokensInCallData(transaction, spec) + floorTokensInAccessList) * spec.GasCosts.TotalCostFloorPerToken,
        { IsEip7623Enabled: true } => GasCostOf.Transaction + tokensInCallData * spec.GasCosts.TotalCostFloorPerToken,
        _ => 0
    };
}

public readonly record struct IntrinsicGas<TGasPolicy>(TGasPolicy Standard, TGasPolicy FloorGas)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public TGasPolicy MinimalGas { get; } = TGasPolicy.Max(Standard, FloorGas);
    public static explicit operator TGasPolicy(IntrinsicGas<TGasPolicy> gas) => gas.MinimalGas;

    /// <summary>
    /// EIP-8037: rejects a transaction whose intrinsic regular or floor gas exceeds <paramref name="cap"/>.
    /// </summary>
    public bool ExceedsCap(ulong cap, out ulong regular, out ulong floor)
    {
        TGasPolicy standard = Standard;
        TGasPolicy floorGas = FloorGas;
        regular = TGasPolicy.GetRemainingGas(in standard);
        floor = TGasPolicy.GetRemainingGas(in floorGas);
        return regular > cap || floor > cap;
    }
}
