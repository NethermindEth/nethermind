// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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

/// <summary>
/// Defines a gas policy for EVM execution.
/// </summary>
/// <typeparam name="TSelf">The implementing type</typeparam>
public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    /// <summary>
    /// Creates a new gas instance from a long value.
    /// This is primarily used for warmup/testing scenarios.
    /// The main execution flow should pass TGasPolicy directly through EvmState.
    /// </summary>
    /// <param name="value">The initial gas value</param>
    /// <returns>A new gas instance</returns>
    static abstract TSelf FromLong(long value);

    /// <summary>
    /// Get the remaining single-dimensional gas available for execution.
    /// This is what's checked against zero to detect out-of-gas conditions.
    /// </summary>
    /// <param name="gas">The gas state to query.</param>
    /// <returns>Remaining gas (negative values indicate out-of-gas)</returns>
    static abstract long GetRemainingGas(in TSelf gas);

    /// <summary>
    /// Gets the remaining state gas reservoir.
    /// Pre-EIP-8037 policies return 0.
    /// </summary>
    /// <param name="gas">The gas state to query.</param>
    /// <returns>Remaining state reservoir gas.</returns>
    static virtual long GetStateReservoir(in TSelf gas) => 0;

    /// <summary>
    /// Gets state gas consumed by the current execution.
    /// Pre-EIP-8037 policies return 0.
    /// </summary>
    /// <param name="gas">The gas state to query.</param>
    /// <returns>Consumed state gas.</returns>
    static virtual long GetStateGasUsed(in TSelf gas) => 0;

    /// <summary>
    /// Gets the amount of state gas that spilled into gas_left.
    /// Used for block regular gas accounting (excluded from regular gas).
    /// Pre-EIP-8037 policies return 0.
    /// </summary>
    /// <param name="gas">The gas state to query.</param>
    /// <returns>State gas drawn from gas_left when reservoir was empty.</returns>
    static virtual long GetStateGasSpill(in TSelf gas) => 0;

    /// <summary>
    /// Consume gas for an EVM operation.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="cost">The gas cost to consume.</param>
    static abstract void Consume(ref TSelf gas, long cost);

    /// <summary>
    /// Consume gas for SelfDestruct operation.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    static abstract bool ConsumeSelfDestructGas(ref TSelf gas);

    /// <summary>
    /// Consume gas for code deposit during CREATE/CREATE2.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="cost">The gas cost (GasCostOf.CodeDeposit * codeLength).</param>
    static abstract void ConsumeCodeDeposit(ref TSelf gas, long cost);

    /// <summary>
    /// Refund gas from a child call frame.
    /// Merges the child gas state back into the parent, preserving any tracking data.
    /// </summary>
    /// <param name="gas">The parent gas state to refund into.</param>
    /// <param name="childGas">The child gas state to merge from.</param>
    static abstract void Refund(ref TSelf gas, in TSelf childGas);

    /// <summary>
    /// Restores all state gas from a failed child frame back to the parent's state reservoir.
    /// On child revert or exceptional halt, state changes are rolled back so consumed state gas is returned.
    /// Pre-EIP-8037 policies are no-ops.
    /// </summary>
    /// <param name="parentGas">The parent gas state to restore into.</param>
    /// <param name="childGas">The child gas state to restore from.</param>
    /// <param name="initialStateReservoir">The initial state reservoir that was assigned to the child frame.</param>
    static virtual void RestoreChildStateGas(ref TSelf parentGas, in TSelf childGas, long initialStateReservoir) { }

    /// <summary>
    /// Adjusts parent gas state when a child <see cref="Refund"/> was already applied but the child
    /// frame should actually be treated as halted (e.g., code deposit failure).
    /// Undoes the state gas portion of Refund and applies halt restoration instead.
    /// Pre-EIP-8037 policies are no-ops.
    /// </summary>
    /// <param name="parentGas">The parent gas state to adjust.</param>
    /// <param name="childGas">The child gas state that was previously merged via Refund.</param>
    /// <param name="initialStateReservoir">The initial state reservoir that was assigned to the child frame.</param>
    static virtual void RevertRefundToHalt(ref TSelf parentGas, in TSelf childGas, long initialStateReservoir) { }

    /// <summary>
    /// Mark the gas state as out of gas.
    /// Called when execution exhausts all gas.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    static abstract void SetOutOfGas(ref TSelf gasState);

    /// <summary>
    /// Charges gas for accessing an account, including potential delegation lookups.
    /// This method ensures that both the requested account and its delegated account (if any) are properly charged.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="spec">The release specification governing gas costs.</param>
    /// <param name="accessTracker">The access tracker for cold/warm state.</param>
    /// <param name="isTracingAccess">Whether access tracing is enabled.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="delegated">The delegated account address, if any.</param>
    /// <param name="chargeForWarm">If true, charge even if the account is already warm.</param>
    /// <returns>True if gas was successfully charged; otherwise false.</returns>
    static abstract bool ConsumeAccountAccessGasWithDelegation(ref TSelf gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        Address? delegated,
        bool chargeForWarm = true);

    /// <summary>
    /// Charges gas for accessing an account based on its storage state (cold vs. warm).
    /// Precompiles are treated as exceptions to the cold/warm gas charge.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="spec">The release specification governing gas costs.</param>
    /// <param name="accessTracker">The access tracker for cold/warm state.</param>
    /// <param name="isTracingAccess">Whether access tracing is enabled.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, applies the warm read gas cost even if the account is warm.</param>
    /// <returns>True if the gas charge was successful; otherwise false.</returns>
    static abstract bool ConsumeAccountAccessGas(ref TSelf gas,
        IReleaseSpec spec,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        Address address,
        bool chargeForWarm = true);

    /// <summary>
    /// Charges the appropriate gas cost for accessing a storage cell, taking into account whether the access is cold or warm.
    /// <para>
    /// For cold storage accesses (or if not previously warmed up), a higher gas cost is applied. For warm access during SLOAD,
    /// a lower cost is deducted.
    /// </para>
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="accessTracker">The access tracker for cold/warm state.</param>
    /// <param name="isTracingAccess">Whether access tracing is enabled.</param>
    /// <param name="storageCell">The target storage cell being accessed.</param>
    /// <param name="storageAccessType">Indicates whether the access is for a load (SLOAD) or store (SSTORE) operation.</param>
    /// <param name="spec">The release specification which governs gas metering and storage access rules.</param>
    /// <returns><c>true</c> if the gas charge was successfully applied; otherwise, <c>false</c> indicating an out-of-gas condition.</returns>
    static abstract bool ConsumeStorageAccessGas(ref TSelf gas,
        ref readonly StackAccessTracker accessTracker,
        bool isTracingAccess,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec);

    /// <summary>
    /// Calculates and deducts the gas cost for accessing a specific memory region.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="position">The starting position in memory.</param>
    /// <param name="length">The length of the memory region.</param>
    /// <param name="vmState">The current EVM state.</param>
    /// <returns><c>true</c> if sufficient gas was available and deducted; otherwise, <c>false</c>.</returns>
    static abstract bool UpdateMemoryCost(ref TSelf gas,
        in UInt256 position,
        in UInt256 length, VmState<TSelf> vmState);

    /// <summary>
    /// Deducts a specified gas cost from the available gas.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="gasCost">The gas cost to deduct.</param>
    /// <returns><c>true</c> if there was enough gas; otherwise, <c>false</c>.</returns>
    static abstract bool UpdateGas(ref TSelf gas, long gasCost);

    /// <summary>
    /// Consumes state gas for state-expansion operations.
    /// Pre-EIP-8037 fallback treats state gas as regular gas.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="stateGasCost">The state gas cost to deduct.</param>
    /// <returns><c>true</c> if there was enough gas; otherwise, <c>false</c>.</returns>
    static virtual bool ConsumeStateGas(ref TSelf gas, long stateGasCost) =>
        TSelf.UpdateGas(ref gas, stateGasCost);

    /// <summary>
    /// Attempts to consume regular gas and then state gas in sequence.
    /// Regular gas (e.g. keccak hash cost) is charged first to prevent
    /// state gas spill-then-halt from inflating the reservoir via the error refund path.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="stateGasCost">State gas component.</param>
    /// <param name="regularGasCost">Regular gas component.</param>
    /// <returns><c>true</c> if both deductions succeeded; otherwise, <c>false</c>.</returns>
    static abstract bool TryConsumeStateAndRegularGas(ref TSelf gas, long stateGasCost, long regularGasCost);

    /// <summary>
    /// Refunds gas by adding the specified amount back to the available gas.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="refund">The gas amount to refund.</param>
    static abstract void UpdateGasUp(ref TSelf gas, long refund);

    /// <summary>
    /// Charges gas for SSTORE write operation (after cold/warm access cost).
    /// Cost is calculated internally based on whether it's a slot creation or update.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="isSlotCreation">True if creating a new slot (original was zero).</param>
    /// <param name="spec">The release specification for determining reset cost.</param>
    /// <returns>True if sufficient gas available</returns>
    static abstract bool ConsumeStorageWrite<TEip8037, TIsSlotCreation>(ref TSelf gas, IReleaseSpec spec)
        where TEip8037 : struct, IFlag
        where TIsSlotCreation : struct, IFlag;

    /// <summary>
    /// Refunds state gas back to the state reservoir.
    /// Pre-EIP-8037 fallback refunds into regular gas.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="amount">Refunded state gas amount.</param>
    /// <param name="stateGasFloor">Minimum state gas used (intrinsic state gas).</param>
    static virtual void RefundStateGas(ref TSelf gas, long amount, long stateGasFloor) => TSelf.UpdateGasUp(ref gas, amount);

    /// <summary>
    /// Returns the regular gas portion of EIP-7702 code insert refunds (for end-of-tx refund cap).
    /// Pre-EIP-8037: (NewAccount - PerAuthBaseCost) per refund. EIP-8037: zero (state refund only).
    /// </summary>
    static virtual long GetCodeInsertRegularRefund(int codeInsertRefunds, IReleaseSpec spec) =>
        codeInsertRefunds > 0 ? (GasCostOf.NewAccount - GasCostOf.PerAuthBaseCost) * codeInsertRefunds : 0;

    /// <summary>
    /// Applies EIP-7702 code insert refunds: state refund to reservoir + returns regular refund amount.
    /// Only call on success paths (state gas accounting must not be modified on error).
    /// </summary>
    /// <param name="stateGasFloor">Minimum state gas used (intrinsic state gas), for clamping refunds.</param>
    static virtual long ApplyCodeInsertRefunds(ref TSelf gas, int codeInsertRefunds, IReleaseSpec spec, long stateGasFloor) =>
        TSelf.GetCodeInsertRegularRefund(codeInsertRefunds, spec);

    /// <summary>
    /// Charges gas for CALL value transfer.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <returns>True if sufficient gas available</returns>
    static abstract bool ConsumeCallValueTransfer(ref TSelf gas);

    /// <summary>
    /// Charges gas for new account creation (25000 gas).
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <returns>True if sufficient gas available</returns>
    static abstract bool ConsumeNewAccountCreation<TEip8037>(ref TSelf gas) where TEip8037 : struct, IFlag;

    /// <summary>
    /// Charges gas for LOG emission with topic and data costs.
    /// Cost is calculated internally: GasCostOf.Log + topicCount * GasCostOf.LogTopic + dataSize * GasCostOf.LogData
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="topicCount">Number of topics.</param>
    /// <param name="dataSize">Size of log data in bytes.</param>
    /// <returns>True if sufficient gas available</returns>
    static abstract bool ConsumeLogEmission(ref TSelf gas, long topicCount, long dataSize);

    /// <summary>
    /// Returns the maximum of two gas values.
    /// Used for MinimalGas calculation in IntrinsicGas.
    /// </summary>
    /// <param name="a">First gas value.</param>
    /// <param name="b">Second gas value.</param>
    /// <returns>The gas value with greater remaining gas.</returns>
    static abstract TSelf Max(in TSelf a, in TSelf b);

    /// <summary>
    /// Calculates intrinsic gas for a transaction.
    /// Returns TGasPolicy allowing implementations to track gas breakdown by category.
    /// </summary>
    /// <param name="tx">The transaction to calculate intrinsic gas for.</param>
    /// <param name="spec">The release specification governing gas costs.</param>
    /// <returns>The intrinsic gas as TGasPolicy.</returns>
    static abstract IntrinsicGas<TSelf> CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec);

    /// <summary>
    /// Creates available gas from gas limit minus intrinsic gas, preserving any tracking data.
    /// For simple implementations, this is a subtraction. For multidimensional gas tracking,
    /// this preserves the breakdown categories from intrinsic gas.
    /// </summary>
    /// <param name="gasLimit">The transaction gas limit.</param>
    /// <param name="intrinsicGas">The intrinsic gas to subtract.</param>
    /// <param name="spec">The release specification for EIP feature detection.</param>
    /// <returns>Available gas with preserved tracking data.</returns>
    static abstract TSelf CreateAvailableFromIntrinsic(long gasLimit, in TSelf intrinsicGas, IReleaseSpec spec);

    /// <summary>
    /// Creates a gas state for a child call/create frame.
    /// Default behavior initializes child state with regular gas only.
    /// EIP-8037 policies can transfer additional state-gas reservoir.
    /// </summary>
    /// <param name="parentGas">Parent gas state (can be mutated when splitting gas dimensions).</param>
    /// <param name="childRegularGas">Regular gas assigned to the child frame.</param>
    /// <returns>Child frame gas state.</returns>
    static virtual TSelf CreateChildFrameGas(ref TSelf parentGas, long childRegularGas) => TSelf.FromLong(childRegularGas);

    /// <summary>
    /// Consumes gas for code copy operations (CODECOPY, CALLDATACOPY, EXTCODECOPY, etc.).
    /// Allows policies to categorize external code copy differently (state trie access).
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="isExternalCode">True for EXTCODECOPY (external account code).</param>
    /// <param name="baseCost">Fixed opcode cost.</param>
    /// <param name="dataCost">Per-word copy cost.</param>
    static abstract void ConsumeDataCopyGas(ref TSelf gas, bool isExternalCode, long baseCost, long dataCost);

    /// <summary>
    /// Hook called before instruction execution when tracing is active.
    /// Allows gas policies to capture pre-execution state.
    /// </summary>
    /// <param name="gas">The current gas state.</param>
    /// <param name="pc">The program counter before incrementing.</param>
    /// <param name="instruction">The instruction about to be executed.</param>
    /// <param name="depth">The current call depth.</param>
    static abstract void OnBeforeInstructionTrace(in TSelf gas, int pc, Instruction instruction, int depth);

    /// <summary>
    /// Hook called after instruction execution when tracing is active.
    /// Allows gas policies to capture the post-execution state.
    /// </summary>
    /// <param name="gas">The current gas state after execution.</param>
    static abstract void OnAfterInstructionTrace(in TSelf gas);

    protected static long CalculateTokensInCallData(Transaction transaction, IReleaseSpec spec)
    {
        ReadOnlySpan<byte> data = transaction.Data.Span;
        int totalZeros = data.CountZeros();
        return totalZeros + (data.Length - totalZeros) * spec.GasCosts.TxDataNonZeroMultiplier;
    }


    public static long AccessListCost(Transaction transaction, IReleaseSpec spec)
    {
        AccessList? accessList = transaction.AccessList;
        if (accessList is not null)
        {
            if (!spec.UseTxAccessLists)
            {
                ThrowInvalidDataException(spec);
            }

            (int addressesCount, int storageKeysCount) = accessList.Count;
            return addressesCount * GasCostOf.AccessAccountListEntry + storageKeysCount * GasCostOf.AccessStorageListEntry;
        }

        return 0;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowInvalidDataException(IReleaseSpec spec) =>
            throw new InvalidDataException($"Transaction with an access list received within the context of {spec.Name}. EIP-2930 is not enabled.");
    }

    public static (long RegularCost, long StateCost) AuthorizationListCost(Transaction transaction, IReleaseSpec spec)
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

        long authCount = authList.Length;
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

    /// <summary>
    /// Calculates the calldata floor cost for a transaction.
    /// </summary>
    protected static long CalculateFloorCost(Transaction transaction, IReleaseSpec spec, long tokensInCallData)
    {
        if (spec.IsEip7976Enabled)
        {
            long floorTokensInCallData = transaction.Data.Length * spec.GasCosts.TxDataNonZeroMultiplier;
            return GasCostOf.Transaction + floorTokensInCallData * GasCostOf.TotalCostFloorPerTokenEip7976;
        }
        else if (spec.IsEip7623Enabled)
        {
            return GasCostOf.Transaction + tokensInCallData * GasCostOf.TotalCostFloorPerTokenEip7623;
        }

        return 0L;
    }
}

/// <summary>
/// Generic intrinsic gas result with TGasPolicy-typed Standard and FloorGas.
/// </summary>
public readonly record struct IntrinsicGas<TGasPolicy>(TGasPolicy Standard, TGasPolicy FloorGas)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    public TGasPolicy MinimalGas { get; } = TGasPolicy.Max(Standard, FloorGas);
    public static explicit operator TGasPolicy(IntrinsicGas<TGasPolicy> gas) => gas.MinimalGas;
}
