// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Evm.Precompiles;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm.GasPolicy;

public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    static abstract TSelf FromULong(ulong value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual TSelf CreateSystemTransactionIntrinsicGas(ulong blockGasLimit) => TSelf.FromULong(0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual TSelf CreateSystemTransactionAvailableGas(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec) =>
        TSelf.CreateAvailableFromIntrinsic(gasLimit, in intrinsicGas, spec);

    static abstract ulong GetRemainingGas(in TSelf gas);

    // EIP-8037 state-cost accessors. Pre-EIP-8037 policies return the constant fallback.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStorageSetStateCost() => (long)GasCostOf.SSetState;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetCreateStateCost() => (long)GasCostOf.CreateState;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetNewAccountStateCost() => (long)GasCostOf.NewAccountState;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetPerAuthBaseStateCost() => (long)GasCostOf.PerAuthBaseState;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetCodeDepositStateCost(int byteCodeLength) => (long)GasCostOf.CodeDepositState * byteCodeLength;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStorageSetReversalRefund() => (long)RefundOf.SSetReversedEip8037;

    // EIP-8037 state-accounting accessors. Pre-EIP-8037 policies return 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateReservoir(in TSelf gas) => 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateGasUsed(in TSelf gas) => 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateGasSpill(in TSelf gas) => 0;
    // Tx-wide cumulative spill paid via gas_left in reverted child frames; never undone.
    // Used by top-level halt to reattribute burned spill from state to regular dimension.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateGasSpillBurned(in TSelf gas) => 0;
    // Spill from reverted children that remains in block regular after in-frame state refund.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateGasSpillReclassified(in TSelf gas) => 0;
    // Spill whose state side was refunded but regular side stays spent; excluded from
    // Calculate8037BlockRegularGas subtraction.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long GetStateGasSpillRefunded(in TSelf gas) => 0;

    static abstract void Consume(ref TSelf gas, ulong cost);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryConsume(ref TSelf gas, ulong cost)
    {
        if (TSelf.GetRemainingGas(in gas) < cost) return false;
        TSelf.Consume(ref gas, cost);
        return true;
    }

    static abstract bool ConsumeSelfDestructGas(ref TSelf gas);
    static abstract void ConsumeCodeDeposit(ref TSelf gas, ulong cost);
    static abstract void Refund(ref TSelf gas, in TSelf childGas);

    // Revert path: restore the child's state gas into the parent reservoir.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RestoreChildStateGas(ref TSelf parentGas, in TSelf childGas) { }
    // Halt path: preserve inline state-gas refunds (call chain resets to top-most failing call).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RestoreChildStateGasOnHalt(ref TSelf parentGas, in TSelf childGas) { }
    // Code-deposit-failure path: undo prior Refund's state-gas merge and apply halt restoration.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    static abstract bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, in UInt256 length, ref EvmPooledMemory memory);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool UpdateMemoryCost(ref TSelf gas, in UInt256 position, ulong length, ref EvmPooledMemory memory)
    {
        UInt256 uint256Length = length;
        return TSelf.UpdateMemoryCost(ref gas, in position, in uint256Length, ref memory);
    }

    static abstract bool UpdateGas(ref TSelf gas, ulong gasCost);

    // Pre-EIP-8037 fallback: state gas folded into regular gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeStateGas(ref TSelf gas, long stateGasCost) => TSelf.UpdateGas(ref gas, (ulong)stateGasCost);

    // Regular gas charged first to prevent state-gas spill-then-halt from inflating
    // the reservoir via the error refund path.
    static abstract bool TryConsumeStateAndRegularGas(ref TSelf gas, long stateGasCost, ulong regularGasCost);

    static abstract void UpdateGasUp(ref TSelf gas, ulong refund);

    static abstract bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref TSelf gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag;

    // Pre-EIP-8037 fallback: refund into regular gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RefundStateGas(ref TSelf gas, long amount, long stateGasFloor) => TSelf.UpdateGasUp(ref gas, (ulong)amount);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RefundStateGas(ref TSelf gas, long amount, long stateGasFloor, bool trackSpillRefund) =>
        TSelf.RefundStateGas(ref gas, amount, stateGasFloor);

    // Drop state-gas from block-state accounting without refunding to the gas budget;
    // reverted state charges stay paid by the tx but don't contribute to committed state gas.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual long DiscardStateGas(ref TSelf gas, long amount, long stateGasFloor, bool trackSpillRefund) => amount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void AddStateGasRefundToReservoir(ref TSelf gas, long amount, bool trackSpillRefund) =>
        TSelf.UpdateGasUp(ref gas, (ulong)amount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void RemoveStateGasRefundFromReservoir(ref TSelf gas, long amount) { }

    // EIP-8037 top-level halt: snap state-gas back to (R0, intrinsicStateUsed, 0); the
    // post-reset StateGasUsed feeds SpentGas so the user doesn't pay for uncommitted state.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ResetForHalt(ref TSelf gas, long initialStateReservoir, long initialStateGasUsed) { }

    // EIP-7702 code-insert refund regular-gas portion. Pre-EIP-8037: (NewAccount - PerAuthBaseCost) each.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong GetCodeInsertRegularRefund(ulong codeInsertRefunds, IReleaseSpec spec) =>
        codeInsertRefunds > 0UL ? (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds : 0UL;

    // EIP-8037: replenishes tx state reservoir before exec (intrinsic state gas already charged).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong ApplyCodeInsertRefunds(ref TSelf gas, ulong codeInsertRefunds, IReleaseSpec spec, long stateGasFloor) =>
        TSelf.GetCodeInsertRegularRefund(codeInsertRefunds, spec);

    static abstract bool ConsumeCallValueTransfer(ref TSelf gas);

    // EIP-2780/EIP-8038 flat value-moving call cost.
    static abstract bool ConsumeCallValueTransferEip2780(ref TSelf gas);
    static abstract bool ConsumeNewAccountCreation<TEip8037>(ref TSelf gas) where TEip8037 : struct, IFlag;
    static abstract bool ConsumeLogEmission(ref TSelf gas, ulong topicCount, ulong dataSize);
    static abstract TSelf Max(in TSelf a, in TSelf b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec) =>
        TSelf.CalculateIntrinsicGas(tx, spec, blockGasLimit: 0);
    static abstract IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec, ulong blockGasLimit);

    static abstract TSelf CreateAvailableFromIntrinsic(ulong gasLimit, in TSelf intrinsicGas, IReleaseSpec spec);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual TSelf CreateChildFrameGas(ref TSelf parentGas, ulong childRegularGas) => TSelf.FromULong(childRegularGas);

    // EXTCODECOPY may need different categorization (state trie access) for some policies.
    static abstract void ConsumeDataCopyGas(ref TSelf gas, bool isExternalCode, ulong baseCost, ulong dataCost);


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void Consume<TCost>(ref TSelf gas) where TCost : struct, IGasCost =>
        TSelf.Consume(ref gas, TCost.GasCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void Consume<TCost>(ref TSelf gas, IReleaseSpec spec) where TCost : struct, ISpecGasCost =>
        TSelf.Consume(ref gas, TCost.GasCost(spec));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ConsumeKeccak(ref TSelf gas, ulong words) =>
        TSelf.Consume(ref gas, GasCostOf.Sha3 + GasCostOf.Sha3Word * words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ConsumeMemoryCopy(ref TSelf gas, ulong words) =>
        TSelf.Consume(ref gas, GasCostOf.VeryLow + GasCostOf.VeryLow * words);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ConsumeExpBytes(ref TSelf gas, IReleaseSpec spec, ulong exponentByteSize) =>
        TSelf.Consume(ref gas, spec.GasCosts.ExpByteCost * exponentByteSize);

    // EIP-8038 reprices the CREATE/CREATE2 account-creation regular cost to CREATE_ACCESS.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeCreateGas<TEip8037, TOpCreate>(ref TSelf gas, IReleaseSpec spec, ulong initCodeWords)
        where TEip8037 : struct, IFlag
        where TOpCreate : struct, EvmInstructions.IOpCreate
    {
        ulong baseCost = spec.IsEip8038Enabled ? Eip8038Constants.CreateAccess
            : TEip8037.IsActive ? GasCostOf.CreateRegular
            : GasCostOf.Create;
        ulong initCodeWordCost = spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * initCodeWords : 0;
        ulong create2HashCost = typeof(TOpCreate) == typeof(EvmInstructions.OpCreate2) ? GasCostOf.Sha3Word * initCodeWords : 0;
        return TSelf.UpdateGas(ref gas, baseCost + initCodeWordCost + create2HashCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeCallBaseGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.CallCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeSStoreResetGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.SStoreResetCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeNetMeteredSStoreGas(ref TSelf gas, IReleaseSpec spec) =>
        TSelf.UpdateGas(ref gas, spec.GasCosts.NetMeteredSStoreCost);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeSSetFromCleanGas(ref TSelf gas) =>
        TSelf.UpdateGas(ref gas, GasCostOf.SSet - GasCostOf.SReset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumePrecompileGas(ref TSelf gas, IPrecompile precompile, ReadOnlyMemory<byte> inputData, IReleaseSpec spec)
    {
        ulong baseGasCost = precompile.BaseGasCost(spec);
        ulong dataGasCost = precompile.DataGasCost(inputData, spec);
        return baseGasCost <= ulong.MaxValue - dataGasCost && TSelf.UpdateGas(ref gas, baseGasCost + dataGasCost);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool ConsumeCreateStateGas(ref TSelf gas) =>
        TSelf.ConsumeStateGas(ref gas, TSelf.GetCreateStateCost());


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryReserveChildGas(ref TSelf gas, in UInt256 requestedGas, IReleaseSpec spec, out ulong childGas)
    {
        ulong gasAvailable = TSelf.GetRemainingGas(in gas);
        if (spec.Use63Over64Rule)
        {
            ulong cap = gasAvailable - gasAvailable / 64;
            childGas = requestedGas.IsUint64 && requestedGas.u0 <= cap ? requestedGas.u0 : cap;
        }
        else
        {
            // Pre-EIP-150: the full requested gas is charged; over-asking exceptionally halts the caller.
            if (!requestedGas.IsUint64)
            {
                childGas = 0;
                return false;
            }
            childGas = requestedGas.u0;
        }
        return TSelf.UpdateGas(ref gas, childGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual bool TryReserveChildGas(ref TSelf gas, IReleaseSpec spec, out ulong childGas)
    {
        ulong gasAvailable = TSelf.GetRemainingGas(in gas);
        childGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64 : gasAvailable;
        return TSelf.UpdateGas(ref gas, childGas);
    }

    // EXTCODECOPY may need different categorization (state trie access) for some policies.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual void ConsumeDataCopyGas(ref TSelf gas, IReleaseSpec spec, bool isExternalCode, ulong words) =>
        TSelf.Consume(ref gas, (isExternalCode ? spec.GasCosts.ExtCodeCost : GasCostOf.VeryLow) + GasCostOf.Memory * words);


    // EIP-8037 block gas rule: header gasUsed is the bottleneck dimension, max(regular, state).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static virtual ulong CombineBlockGas(ulong blockRegularGas, ulong blockStateGas) => Math.Max(blockRegularGas, blockStateGas);

    static abstract void OnBeforeInstructionTrace(in TSelf gas, int pc, Instruction instruction, int depth);
    static abstract void OnAfterInstructionTrace(in TSelf gas);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static ulong CalculateTokensInCallData(Transaction transaction, IReleaseSpec spec)
    {
        ReadOnlySpan<byte> data = transaction.Data.Span;
        int totalZeros = data.CountZeros();
        return (ulong)totalZeros + (ulong)(data.Length - totalZeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }

    // 0 when floor pricing is not active.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CalculateFloorTokensInAccessList(Transaction transaction, IReleaseSpec spec) =>
        spec.IsEip7981Enabled && transaction.AccessList is { Count: (int addressesCount, int storageKeysCount) }
            ? (ulong)(addressesCount * Address.Size + storageKeysCount * AccessList.StorageKeySize) * spec.GasCosts.TxDataNonZeroMultiplier
            : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong AccessListCost(Transaction transaction, IReleaseSpec spec, ulong floorTokensInAccessList)
    {
        AccessList? accessList = transaction.AccessList;
        if (accessList is null) return 0;

        if (!spec.UseTxAccessLists)
        {
            ThrowInvalidDataException(spec);
        }

        (int addressesCount, int storageKeysCount) = accessList.Count;
        // EIP-8038 realigns access-list entry costs with the cold-access costs they pre-warm.
        ulong addressCost = spec.IsEip8038Enabled ? Eip8038Constants.AccessListAddressCost : GasCostOf.AccessAccountListEntry;
        ulong storageKeyCost = spec.IsEip8038Enabled ? Eip8038Constants.AccessListStorageKeyCost : GasCostOf.AccessStorageListEntry;
        return (ulong)addressesCount * addressCost
            + (ulong)storageKeysCount * storageKeyCost
            + spec.GasCosts.TotalCostFloorPerToken * floorTokensInAccessList;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidDataException(IReleaseSpec spec) =>
            throw new InvalidDataException($"Transaction with an access list received within the context of {spec.Name}. EIP-2930 is not enabled.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        // EIP-8038 reprices the per-authorization regular cost (ACCOUNT_WRITE + auth-base).
        ulong perAuthRegular = spec.IsEip8038Enabled ? Eip8038Constants.PerAuthBaseRegular : GasCostOf.PerAuthBaseRegular;
        return spec.IsEip8037Enabled
            ? (
                authCount * perAuthRegular,
                authCount * (GasCostOf.NewAccountState + GasCostOf.PerAuthBaseState)
            )
            : (authCount * GasCostOf.NewAccount, 0);

        [DoesNotReturn, StackTraceHidden]
        static void ThrowAuthorizationListNotEnabled(IReleaseSpec releaseSpec) =>
            throw new InvalidDataException($"Transaction with an authorization list received within the context of {releaseSpec.Name}. EIP-7702 is not enabled.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CalculateFloorTokensInCallData(Transaction transaction, IReleaseSpec spec) =>
        (ulong)transaction.Data.Length * spec.GasCosts.TxDataNonZeroMultiplier;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected static ulong CalculateFloorCost(Transaction transaction, IReleaseSpec spec, ulong tokensInCallData, ulong floorTokensInAccessList)
    {
        // EIP-2780 reduces the intrinsic base; the calldata floor must track it, otherwise the
        // legacy 21,000 floor would dominate and negate the reduction for value transfers.
        ulong floorBase = spec.IsEip2780Enabled ? GasCostOf.TransactionEip2780 : GasCostOf.Transaction;
        return spec switch
        {
            { IsEip7976Enabled: true } => floorBase + (CalculateFloorTokensInCallData(transaction, spec) + floorTokensInAccessList) * spec.GasCosts.TotalCostFloorPerToken,
            { IsEip7623Enabled: true } => floorBase + tokensInCallData * spec.GasCosts.TotalCostFloorPerToken,
            _ => 0
        };
    }
}

public readonly record struct IntrinsicGas<TGasPolicy>(TGasPolicy Standard, TGasPolicy FloorGas)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public TGasPolicy MinimalGas { get; } = TGasPolicy.Max(Standard, FloorGas);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
