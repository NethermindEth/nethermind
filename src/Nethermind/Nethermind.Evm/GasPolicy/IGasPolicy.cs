// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
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
    /// Main execution flow should pass TGasPolicy directly through EvmState.
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
    /// Consume gas for an EVM operation.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="cost">The gas cost to consume.</param>
    static abstract void Consume(ref TSelf gas, long cost);

    /// <summary>
    /// Consume gas for SelfDestruct operation.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    static abstract void ConsumeSelfDestructGas(ref TSelf gas);

    /// <summary>
    /// Refund gas from a child call frame.
    /// Merges the child gas state back into the parent, preserving any tracking data.
    /// </summary>
    /// <param name="gas">The parent gas state to refund into.</param>
    /// <param name="childGas">The child gas state to merge from.</param>
    static abstract void Refund(ref TSelf gas, in TSelf childGas);

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

    static abstract bool UpdateMemoryCost(ref TSelf gas,
        in UInt256 position,
        ulong length, VmState<TSelf> vmState);
    /// <summary>
    /// Deducts a specified gas cost from the available gas.
    /// </summary>
    /// <param name="gas">The gas state to update.</param>
    /// <param name="gasCost">The gas cost to deduct.</param>
    /// <returns><c>true</c> if there was enough gas; otherwise, <c>false</c>.</returns>
    static abstract bool UpdateGas(ref TSelf gas, long gasCost);

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
    static abstract bool ConsumeStorageWrite(ref TSelf gas, bool isSlotCreation, IReleaseSpec spec);

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
    static abstract bool ConsumeNewAccountCreation(ref TSelf gas);

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
    static abstract TSelf CalculateIntrinsicGas(Transaction tx, IReleaseSpec spec);

    /// <summary>
    /// Creates available gas from gas limit minus intrinsic gas, preserving any tracking data.
    /// For simple implementations, this is a subtraction. For multi-dimensional gas tracking,
    /// this preserves the breakdown categories from intrinsic gas.
    /// </summary>
    /// <param name="gasLimit">The transaction gas limit.</param>
    /// <param name="intrinsicGas">The intrinsic gas to subtract.</param>
    /// <returns>Available gas with preserved tracking data.</returns>
    static abstract TSelf CreateAvailableFromIntrinsic(long gasLimit, in TSelf intrinsicGas);

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
    /// Allows gas policies to capture post-execution state.
    /// </summary>
    /// <param name="gas">The current gas state after execution.</param>
    static abstract void OnAfterInstructionTrace(in TSelf gas);
}
