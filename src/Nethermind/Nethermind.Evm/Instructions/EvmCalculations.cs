// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Gas;
using Nethermind.Int256;

namespace Nethermind.Evm;

public static class EvmCalculations
{
    /// <summary>
    /// Charges gas for accessing an account, including potential delegation lookups.
    /// This method ensures that both the requested account and its delegated account (if any) are properly charged.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type.</typeparam>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, charge even if the account is already warm.</param>
    /// <param name="instruction">The instruction being executed.</param>
    /// <returns>True if gas was successfully charged; otherwise false.</returns>
    public static bool ChargeAccountAccessGasWithDelegation<TGasPolicy>(
        ref GasState gasState,
        VirtualMachine<TGasPolicy> vm,
        Address address,
        Instruction instruction,
        bool chargeForWarm = true)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        IReleaseSpec spec = vm.Spec;
        if (!spec.UseHotAndColdStorage)
        {
            // No extra cost if hot/cold storage is not used.
            return true;
        }

        bool notOutOfGas = ChargeAccountAccessGas(ref gasState, vm, address, instruction, chargeForWarm);
        return notOutOfGas
               && (!vm.TxExecutionContext.CodeInfoRepository.TryGetDelegation(address, spec, out Address delegated)
                   // Charge additional gas for the delegated account if it exists.
                   || ChargeAccountAccessGas(ref gasState, vm, delegated, instruction, chargeForWarm));
    }

    /// <summary>
    /// Charges gas for accessing an account based on its storage state (cold vs. warm).
    /// Precompiles are treated as exceptions to the cold/warm gas charge.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type.</typeparam>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="instruction">The instruction being executed.</param>
    /// <param name="chargeForWarm">If true, applies the warm read gas cost even if the account is warm.</param>
    /// <returns>True if the gas charge was successful; otherwise false.</returns>
    public static bool ChargeAccountAccessGas<TGasPolicy>(
        ref GasState gasState,
        VirtualMachine<TGasPolicy> vm,
        Address address,
        Instruction instruction,
        bool chargeForWarm = true)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
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
            if (!spec.IsPrecompile(address) && vmState.AccessTracker.WarmUp(address))
            {
                result = UpdateGas<TGasPolicy>(ref gasState, GasCostOf.ColdAccountAccess, instruction);
            }
            else if (chargeForWarm)
            {
                // Otherwise, if warm access should be charged, apply the warm read cost.
                result = UpdateGas<TGasPolicy>(ref gasState, GasCostOf.WarmStateRead, instruction);
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
    /// <typeparam name="TGasPolicy">The gas policy type.</typeparam>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="storageCell">The target storage cell being accessed.</param>
    /// <param name="storageAccessType">Indicates whether the access is for a load (SLOAD) or store (SSTORE) operation.</param>
    /// <param name="spec">The release specification which governs gas metering and storage access rules.</param>
    /// <param name="instruction">The instruction being executed.</param>
    /// <returns><c>true</c> if the gas charge was successfully applied; otherwise, <c>false</c> indicating an out-of-gas condition.</returns>
    public static bool ChargeStorageAccessGas<TGasPolicy>(
        ref GasState gasState,
        VirtualMachine<TGasPolicy> vm,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec,
        Instruction instruction)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
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
                result = UpdateGas<TGasPolicy>(ref gasState, GasCostOf.ColdSLoad, instruction);
            }
            // For SLOAD operations on already warmed-up storage, apply a lower warm-read cost.
            else if (storageAccessType == StorageAccessType.SLOAD)
            {
                result = UpdateGas<TGasPolicy>(ref gasState, GasCostOf.WarmStateRead, instruction);
            }
        }

        return result;
    }

    /// <summary>
    /// Updates the memory cost using the gas policy abstraction.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type.</typeparam>
    /// <param name="vmState">The current EVM state.</param>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="position">The starting position in memory.</param>
    /// <param name="length">The length of the memory region.</param>
    /// <param name="instruction">The instruction being executed.</param>
    /// <returns><c>true</c> if sufficient gas was available and deducted; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateMemoryCost<TGasPolicy>(EvmState vmState, ref GasState gasState, in UInt256 position,
        in UInt256 length, Instruction instruction)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // Calculate additional gas cost for any memory expansion.
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length);
        if (memoryCost != 0L)
        {
            if (!UpdateGas<TGasPolicy>(ref gasState, memoryCost, instruction))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deducts a specified gas cost using the gas policy abstraction.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type.</typeparam>
    /// <param name="gasState">The gas state to update.</param>
    /// <param name="gasCost">The gas cost to deduct.</param>
    /// <param name="instruction">The instruction being executed.</param>
    /// <returns><c>true</c> if there was sufficient gas; otherwise, <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool UpdateGas<TGasPolicy>(ref GasState gasState, long gasCost, Instruction instruction)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (TGasPolicy.GetRemainingGas(in gasState) < gasCost)
        {
            return false;
        }

        TGasPolicy.ConsumeGas(ref gasState, gasCost, instruction);
        return true;
    }

    /// <summary>
    /// Refunds gas by adding the specified amount back to the available gas.
    /// </summary>
    /// <param name="refund">The gas amount to refund.</param>
    /// <param name="gasState">The gas state to update.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void UpdateGasUp<TGasPolicy>(ref GasState gasState, long refund)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        TGasPolicy.RefundGas(ref gasState, refund);
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
