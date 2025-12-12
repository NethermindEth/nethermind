// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm.Gas;

/// <summary>
/// Defines a gas policy for EVM execution.
/// </summary>
/// <typeparam name="TSelf">The implementing type</typeparam>
public interface IGasPolicy<TSelf> where TSelf : struct, IGasPolicy<TSelf>
{
    /// <summary>
    /// Initialize gas state for a new transaction with intrinsic gas already deducted.
    /// Called by TransactionProcessor before ExecuteTransaction.
    /// </summary>
    /// <param name="gasLimit">The gas limit provided for the transaction.</param>
    /// <param name="intrinsicGas">Intrinsic gas already calculated.</param>
    /// <returns>Initialized gas state with remaining gas</returns>
    static abstract GasState<TSelf> InitializeForTransaction(long gasLimit, long intrinsicGas);

    /// <summary>
    /// Get the remaining single-dimensional gas available for execution.
    /// This is what's checked against zero to detect out-of-gas conditions.
    /// </summary>
    /// <param name="gasState">The current gas state.</param>
    /// <returns>Remaining gas (negative values indicate out-of-gas)</returns>
    static abstract long GetRemainingGas(in GasState<TSelf> gasState);

    /// <summary>
    /// Consume gas for an EVM operation.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="gasCost">The gas cost to charge for this operation.</param>
    static abstract void ConsumeGas(ref GasState<TSelf> gasState, long gasCost);

    /// <summary>
    /// Consume gas for SelfDestruct operation.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    static abstract void ChargeSelfDestructGas(ref GasState<TSelf> gasState);

    /// <summary>
    /// Refund gas.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="gasAmount">The amount of gas to return.</param>
    static abstract void RefundGas(ref GasState<TSelf> gasState, long gasAmount);

    /// <summary>
    /// Mark the gas state as out of gas.
    /// Called when execution exhausts all gas.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    static abstract void SetOutOfGas(ref GasState<TSelf> gasState);

    /// <summary>
    /// Charges gas for accessing an account, including potential delegation lookups.
    /// This method ensures that both the requested account and its delegated account (if any) are properly charged.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, charge even if the account is already warm.</param>
    /// <returns>True if gas was successfully charged; otherwise false.</returns>
    static abstract bool ChargeAccountAccessGasWithDelegation(
        ref GasState<TSelf> gasState,
        VirtualMachine<TSelf> vm,
        Address address,
        bool chargeForWarm = true);

    /// <summary>
    /// Charges gas for accessing an account based on its storage state (cold vs. warm).
    /// Precompiles are treated as exceptions to the cold/warm gas charge.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, applies the warm read gas cost even if the account is warm.</param>
    /// <returns>True if the gas charge was successful; otherwise false.</returns>
    static abstract bool ChargeAccountAccessGas(
        ref GasState<TSelf> gasState,
        VirtualMachine<TSelf> vm,
        Address address,
        bool chargeForWarm = true);

    /// <summary>
    /// Charges the appropriate gas cost for accessing a storage cell, taking into account whether the access is cold or warm.
    /// <para>
    /// For cold storage accesses (or if not previously warmed up), a higher gas cost is applied. For warm access during SLOAD,
    /// a lower cost is deducted.
    /// </para>
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="storageCell">The target storage cell being accessed.</param>
    /// <param name="storageAccessType">Indicates whether the access is for a load (SLOAD) or store (SSTORE) operation.</param>
    /// <param name="spec">The release specification which governs gas metering and storage access rules.</param>
    /// <returns><c>true</c> if the gas charge was successfully applied; otherwise, <c>false</c> indicating an out-of-gas condition.</returns>
    static abstract bool ChargeStorageAccessGas(
        ref GasState<TSelf> gasState,
        VirtualMachine<TSelf> vm,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec);

    /// <summary>
    /// Calculates and deducts the gas cost for accessing a specific memory region.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="position">The starting position in memory.</param>
    /// <param name="length">The length of the memory region.</param>
    /// <param name="vmState">The current EVM state.</param>
    /// <returns><c>true</c> if sufficient gas was available and deducted; otherwise, <c>false</c>.</returns>
    static abstract bool UpdateMemoryCost(ref GasState<TSelf> gasState,
        in UInt256 position,
        in UInt256 length, EvmState vmState);

    /// <summary>
    /// Deducts a specified gas cost from the available gas.
    /// </summary>
    /// <param name="gasCost">The gas cost to deduct.</param>
    /// <param name="gasState">The gas state to update.</param>
    /// <returns><c>true</c> if there was enough gas; otherwise, <c>false</c>.</returns>
    static abstract bool UpdateGas(ref GasState<TSelf> gasState, long gasCost);

    /// <summary>
    /// Refunds gas by adding the specified amount back to the available gas.
    /// </summary>
    /// <param name="refund">The gas amount to refund.</param>
    /// <param name="gasState">The gas state to update.</param>
    static abstract void UpdateGasUp(ref GasState<TSelf> gasState, long refund);

    /// <summary>
    /// Charges gas for SSTORE write operation (after cold/warm access cost).
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="cost">The gas cost to charge.</param>
    /// <param name="isSlotCreation">True if creating a new slot (original was zero).</param>
    /// <returns>True if sufficient gas available</returns>
    static abstract bool ChargeStorageWrite(ref GasState<TSelf> gasState, long cost, bool isSlotCreation);

    /// <summary>
    /// Charges gas for CALL value transfer or new account creation.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="cost">The gas cost to charge.</param>
    /// <param name="isNewAccount">True if creating a new account (address was empty).</param>
    /// <returns>True if sufficient gas available</returns>
    static abstract bool ChargeCallExtra(ref GasState<TSelf> gasState, long cost, bool isNewAccount);

    /// <summary>
    /// Charges gas for LOG emission with topic and data costs.
    /// </summary>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="topicCount">Number of topics.</param>
    /// <param name="dataSize">Size of log data in bytes.</param>
    /// <param name="totalCost">Total gas cost (base + topics + data).</param>
    /// <returns>True if sufficient gas available</returns>
    static abstract bool ChargeLogEmission(ref GasState<TSelf> gasState, long topicCount, long dataSize, long totalCost);
}
