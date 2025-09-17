// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Evm;


/// <summary>
/// Enumeration for specifying the type of storage access.
/// </summary>
public enum StorageAccessType
{
    /// <summary>
    /// Indicates a persistent storage read (SLOAD) operation.
    /// </summary>
    SLOAD,

    /// <summary>
    /// Indicates a persistent storage write (SSTORE) operation.
    /// </summary>
    SSTORE
}

public static class EvmInstructionsUtils
{
    /// <summary>
    /// Charges gas for accessing an account, including potential delegation lookups.
    /// This method ensures that both the requested account and its delegated account (if any) are properly charged.
    /// </summary>
    /// <param name="gasAvailable">Reference to the available gas which will be updated.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, charge even if the account is already warm.</param>
    /// <returns>True if gas was successfully charged; otherwise false.</returns>
    public static bool ChargeAccountAccessGasWithDelegation(ref long gasAvailable, VirtualMachine vm, Address address, bool chargeForWarm = true)
    {
        IReleaseSpec spec = vm.Spec;
        if (!spec.UseHotAndColdStorage)
        {
            // No extra cost if hot/cold storage is not used.
            return true;
        }
        bool notOutOfGas = ChargeAccountAccessGas(ref gasAvailable, vm, address, chargeForWarm);
        return notOutOfGas
               && (!vm.TxExecutionContext.CodeInfoRepository.TryGetDelegation(address, spec, out Address delegated)
                   // Charge additional gas for the delegated account if it exists.
                   || ChargeAccountAccessGas(ref gasAvailable, vm, delegated, chargeForWarm));
    }

    /// <summary>
    /// Charges gas for accessing an account based on its storage state (cold vs. warm).
    /// Precompiles are treated as exceptions to the cold/warm gas charge.
    /// </summary>
    /// <param name="gasAvailable">Reference to the available gas which will be updated.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, applies the warm read gas cost even if the account is warm.</param>
    /// <returns>True if the gas charge was successful; otherwise false.</returns>
    public static bool ChargeAccountAccessGas(ref long gasAvailable, VirtualMachine vm, Address address, bool chargeForWarm = true)
    {
        bool result = true;
        IReleaseSpec spec = vm.Spec;
        if (spec.UseHotAndColdStorage)
        {
            EvmState vmState = vm.EvmState;
            if (vm.TxTracer.IsTracingAccess)
            {
                // Ensure that tracing simulates access-list behavior.
                vmState.AccessTracker.WarmUp(address);
            }

            // If the account is cold (and not a precompile), charge the cold access cost.
            if (!vm.CodeInfoRepository.IsPrecompile(address, spec) && vmState.AccessTracker.WarmUp(address))
            {
                result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
            }
            else if (chargeForWarm)
            {
                // Otherwise, if warm access should be charged, apply the warm read cost.
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
        }

        return result;
    }

    /// <summary>
    /// Charges the appropriate gas cost for accessing a storage cell, taking into account whether the access is cold or warm.
    /// <para>
    /// For cold storage accesses (or if not previously warmed up), a higher gas cost is applied. For warm accesses during SLOAD,
    /// a lower cost is deducted.
    /// </para>
    /// </summary>
    /// <param name="gasAvailable">The remaining gas, passed by reference and reduced by the access cost.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="storageCell">The target storage cell being accessed.</param>
    /// <param name="storageAccessType">Indicates whether the access is for a load (SLOAD) or store (SSTORE) operation.</param>
    /// <param name="spec">The release specification which governs gas metering and storage access rules.</param>
    /// <returns><c>true</c> if the gas charge was successfully applied; otherwise, <c>false</c> indicating an out-of-gas condition.</returns>
    public static bool ChargeStorageAccessGas(
        ref long gasAvailable,
        VirtualMachine vm,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
    {
        EvmState vmState = vm.EvmState;
        bool result = true;

        // If the spec requires hot/cold storage tracking, determine if extra gas should be charged.
        if (spec.UseHotAndColdStorage)
        {
            // When tracing access, ensure the storage cell is marked as warm to simulate inclusion in the access list.
            ref readonly StackAccessTracker accessTracker = ref vmState.AccessTracker;
            if (vm.TxTracer.IsTracingAccess)
            {
                accessTracker.WarmUp(in storageCell);
            }

            // If the storage cell is still cold, apply the higher cold access cost and mark it as warm.
            if (accessTracker.WarmUp(in storageCell))
            {
                result = UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
            }
            // For SLOAD operations on already warmed-up storage, apply a lower warm-read cost.
            else if (storageAccessType == StorageAccessType.SLOAD)
            {
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates and deducts the gas cost for accessing a specific memory region.
    /// </summary>
    /// <param name="vmState">The current EVM state.</param>
    /// <param name="gasAvailable">The remaining gas available.</param>
    /// <param name="position">The starting position in memory.</param>
    /// <param name="length">The length of the memory region.</param>
    /// <returns><c>true</c> if sufficient gas was available and deducted; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        // Calculate additional gas cost for any memory expansion.
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length);
        if (memoryCost != 0L)
        {
            if (!UpdateGas(memoryCost, ref gasAvailable))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deducts a specified gas cost from the available gas.
    /// </summary>
    /// <param name="gasCost">The gas cost to deduct.</param>
    /// <param name="gasAvailable">The remaining gas available.</param>
    /// <returns><c>true</c> if there was sufficient gas; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost)
        {
            return false;
        }

        gasAvailable -= gasCost;
        return true;
    }

    /// <summary>
    /// Refunds gas by adding the specified amount back to the available gas.
    /// </summary>
    /// <param name="refund">The gas amount to refund.</param>
    /// <param name="gasAvailable">The current gas available.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp(long refund, ref long gasAvailable)
    {
        gasAvailable += refund;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Div32Ceiling(in UInt256 length, out bool outOfGas)
    {
        if (length.IsLargerThanULong())
        {
            outOfGas = true;
            return 0;
        }

        return Div32Ceiling(length.u0, out outOfGas);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Div32Ceiling(ulong result, out bool outOfGas)
    {
        ulong rem = result & 31;
        result >>= 5;
        if (rem > 0)
        {
            result++;
        }

        if (result > uint.MaxValue)
        {
            outOfGas = true;
            return 0;
        }

        outOfGas = false;
        return (long)result;
    }

    public static long Div32Ceiling(in UInt256 length)
    {
        long result = Div32Ceiling(in length, out bool outOfGas);
        if (outOfGas)
        {
            ThrowOutOfGasException();
        }

        return result;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowOutOfGasException()
        {
            Metrics.EvmExceptions++;
            throw new OutOfGasException();
        }
    }
}
