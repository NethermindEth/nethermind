// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

/// <summary>
/// Implements various EVM instruction handlers for transient storage, memory, and persistent storage operations.
/// </summary>
internal static partial class EvmInstructions
{
    /// <summary>
    /// Enumeration for specifying the type of storage access.
    /// </summary>
    internal enum StorageAccessType
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

    /// <summary>
    /// Executes the transient load (TLOAD) instruction.
    /// <para>
    /// Pops an offset from the stack, uses it to construct a storage cell for the executing account,
    /// retrieves the corresponding transient storage value, and pushes it onto the stack.
    /// </para>
    /// </summary>
    /// <param name="vm">The virtual machine instance executing the instruction.</param>
    /// <param name="stack">The EVM stack.</param>
    /// <param name="gasAvailable">The available gas, which is reduced by the gas cost of the operation.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating the result of the operation.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTLoad<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Increment the opcode metric for TLOAD.
        Metrics.TloadOpcode++;

        // Deduct the fixed gas cost for TLOAD.
        gasAvailable -= GasCostOf.TLoad;

        // Attempt to pop the key (offset) from the stack; if unavailable, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        // Construct a transient storage cell using the executing account and the provided offset.
        StorageCell storageCell = new(vm.EvmState.Env.ExecutingAccount, in result);

        // Retrieve the value from transient storage.
        ReadOnlySpan<byte> value = vm.WorldState.GetTransientState(in storageCell);

        // Push the retrieved value onto the stack.
        stack.PushBytes<TTracingInst>(value);

        // If storage tracing is enabled, record the operation (ensuring gas remains non-negative).
        if (vm.TxTracer.IsTracingStorage)
        {
            if (gasAvailable < 0) goto OutOfGas;
            vm.TxTracer.LoadOperationTransientStorage(storageCell.Address, result, value);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes the transient store (TSTORE) instruction.
    /// <para>
    /// Pops a key and value from the stack, then updates the transient storage for the executing account.
    /// In a static call, state modification is disallowed.
    /// </para>
    /// </summary>
    /// <param name="vm">The virtual machine instance executing the instruction.</param>
    /// <param name="stack">The EVM stack.</param>
    /// <param name="gasAvailable">The available gas, reduced by the cost of TSTORE.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating success or failure.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionTStore(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Increment the opcode metric for TSTORE.
        Metrics.TstoreOpcode++;

        EvmState vmState = vm.EvmState;

        // Disallow storage modification during static calls.
        if (vmState.IsStatic) goto StaticCallViolation;

        // Deduct the gas cost for TSTORE.
        gasAvailable -= GasCostOf.TStore;

        // Pop the key (offset) from the stack; if unavailable, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        // Construct a transient storage cell for the executing account at the specified key.
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, in result);

        // Pop the 32-byte value from the stack.
        Span<byte> bytes = stack.PopWord256();

        // Store either the actual value (if non-zero) or a predefined zero constant.
        vm.WorldState.SetTransientState(in storageCell, !bytes.IsZero() ? bytes.ToArray() : BytesZero32);

        // If storage tracing is enabled, retrieve the current stored value and log the operation.
        if (vm.TxTracer.IsTracingStorage)
        {
            if (gasAvailable < 0) goto OutOfGas;
            ReadOnlySpan<byte> currentValue = vm.WorldState.GetTransientState(in storageCell);
            vm.TxTracer.SetOperationTransientStorage(storageCell.Address, result, bytes, currentValue);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    }

    /// <summary>
    /// Executes the memory store (MSTORE) instruction.
    /// <para>
    /// Pops an offset and a 32-byte word from the stack, updates the memory cost if needed,
    /// and then writes the word into memory.
    /// </para>
    /// </summary>
    /// <typeparam name="TTracingInst">A flag type indicating whether tracing is active.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The EVM stack.</param>
    /// <param name="gasAvailable">The remaining gas, which is decremented by both the base and memory extension costs.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> result.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionMStore<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.VeryLow;

        // Pop the memory offset; if not available, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        // Retrieve the 32-byte word to be stored.
        Span<byte> bytes = stack.PopWord256();

        EvmState vmState = vm.EvmState;

        // Update the memory cost for a 32-byte store; if insufficient gas, signal out-of-gas.
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) goto OutOfGas;

        // Write the 32-byte word into memory.
        vmState.Memory.SaveWord(in result, bytes);

        // Report memory changes if tracing is active.
        if (TTracingInst.IsActive)
            vm.TxTracer.ReportMemoryChange((long)result, bytes);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes the memory store byte (MSTORE8) instruction.
    /// <para>
    /// Pops an offset and a byte from the stack, updates the memory cost accordingly,
    /// and then stores the single byte at the specified memory location.
    /// </para>
    /// </summary>
    /// <typeparam name="TTracingInst">A flag type indicating whether tracing is active.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The EVM stack.</param>
    /// <param name="gasAvailable">The remaining gas, reduced by the operation cost and any memory extension costs.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> result.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionMStore8<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.VeryLow;

        // Pop the memory offset from the stack; if missing, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        // Pop a single byte from the stack.
        byte data = stack.PopByte();

        EvmState vmState = vm.EvmState;

        // Update the memory cost for a single-byte extension; if insufficient, signal out-of-gas.
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in UInt256.One)) goto OutOfGas;

        // Write the single byte to memory.
        vmState.Memory.SaveByte(in result, data);

        // Report the memory change if tracing is active.
        if (TTracingInst.IsActive)
            vm.TxTracer.ReportMemoryChange(result, data);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes the memory load (MLOAD) instruction.
    /// <para>
    /// Pops an offset from the stack, updates the memory cost for a 32-byte load,
    /// retrieves the corresponding memory word, and pushes it onto the stack.
    /// </para>
    /// </summary>
    /// <typeparam name="TTracingInst">A flag type indicating whether tracing is active.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The EVM stack.</param>
    /// <param name="gasAvailable">The remaining gas, adjusted for memory access.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> result.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionMLoad<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.VeryLow;

        // Pop the memory offset; if missing, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        EvmState vmState = vm.EvmState;

        // Update memory cost for a 32-byte load.
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) goto OutOfGas;

        // Load the 32-byte word from memory.
        Span<byte> bytes = vmState.Memory.LoadSpan(in result);

        // Report the memory load if tracing is active.
        if (TTracingInst.IsActive)
            vm.TxTracer.ReportMemoryChange(result, bytes);

        // Push the loaded bytes onto the stack.
        stack.PushBytes<TTracingInst>(bytes);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes the memory copy (MCOPY) instruction.
    /// <para>
    /// Pops destination offset, source offset, and length from the stack, then copies the specified
    /// memory region from the source to the destination after verifying that enough gas is available.
    /// </para>
    /// </summary>
    /// <typeparam name="TTracingInst">A flag type indicating whether tracing is active.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The EVM stack.</param>
    /// <param name="gasAvailable">The available gas, reduced by both the base cost and the dynamic cost calculated from the length.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> result.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionMCopy<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Increment the opcode metric for MCOPY.
        Metrics.MCopyOpcode++;

        // Pop destination, source, and length values; if any are missing, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b) || !stack.PopUInt256(out UInt256 c)) goto StackUnderflow;

        // Calculate additional gas cost based on the length (using a division rounding-up method) and deduct the total cost.
        gasAvailable -= GasCostOf.VeryLow + GasCostOf.VeryLow * EvmPooledMemory.Div32Ceiling(c, out bool outOfGas);
        if (outOfGas) goto OutOfGas;

        EvmState vmState = vm.EvmState;

        // Update memory cost for the destination area (largest offset among source and destination) over the specified length.
        if (!UpdateMemoryCost(vmState, ref gasAvailable, UInt256.Max(b, a), c)) goto OutOfGas;

        // Load the specified memory segment from the source offset.
        Span<byte> bytes = vmState.Memory.LoadSpan(in b, c);

        // Report the memory change at the source if tracing is active.
        if (TTracingInst.IsActive)
            vm.TxTracer.ReportMemoryChange(b, bytes);

        // Write the bytes into memory at the destination offset.
        vmState.Memory.Save(in a, bytes);

        // Report the memory change at the destination if tracing is active.
        if (TTracingInst.IsActive)
            vm.TxTracer.ReportMemoryChange(a, bytes);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes the storage store (SSTORE) instruction.
    /// <para>
    /// Pops a key and a value from the stack, performs necessary gas metering (including refunds),
    /// and updates persistent storage for the executing account. This method handles both net metered
    /// and legacy gas calculations.
    /// </para>
    /// </summary>
    /// <typeparam name="TTracingInst">A flag type indicating whether detailed tracing is enabled.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The EVM stack.</param>
    /// <param name="gasAvailable">The available gas, which is decremented by multiple cost adjustments during storage modification.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating the outcome.</returns>
    [SkipLocalsInit]
    internal static EvmExceptionType InstructionSStore<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Increment the SSTORE opcode metric.
        Metrics.IncrementSStoreOpcode();

        EvmState vmState = vm.EvmState;
        // Disallow storage modifications in static calls.
        if (vmState.IsStatic) goto StaticCallViolation;

        IReleaseSpec spec = vm.Spec;

        // For legacy metering: ensure there is enough gas for the SSTORE reset cost before reading storage.
        if (!spec.UseNetGasMetering && !UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable))
            goto OutOfGas;

        // In net metering with stipend fix, ensure extra gas pressure is reported and that sufficient gas remains.
        if (spec.UseNetGasMeteringWithAStipendFix)
        {
            if (vm.TxTracer.IsTracingRefunds)
                vm.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend - spec.GetNetMeteredSStoreCost() + 1);
            if (gasAvailable <= GasCostOf.CallStipend)
                goto OutOfGas;
        }

        // Pop the key and then the new value for storage; signal underflow if unavailable.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;
        ReadOnlySpan<byte> bytes = stack.PopWord256();

        // Determine if the new value is effectively zero and normalize non-zero values by stripping leading zeros.
        bool newIsZero = bytes.IsZero();
        bytes = !newIsZero ? bytes.WithoutLeadingZeros() : BytesZero;

        // Construct the storage cell for the executing account.
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, in result);

        // Charge gas based on whether this is a cold or warm storage access.
        if (!ChargeStorageAccessGas(ref gasAvailable, vm, in storageCell, StorageAccessType.SSTORE, spec))
            goto OutOfGas;

        // Retrieve the current value from persistent storage.
        ReadOnlySpan<byte> currentValue = vm.WorldState.Get(in storageCell);
        bool currentIsZero = currentValue.IsZero();

        // Determine whether the new value is identical to the current stored value.
        bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, bytes);

        // Retrieve the refund value associated with clearing storage.
        long sClearRefunds = RefundOf.SClear(spec.IsEip3529Enabled);

        // Handle gas adjustments and refunds based on the metering mode.
        if (!spec.UseNetGasMetering)
        {
            // Legacy metering: if storing zero and the value changes, grant a clearing refund.
            if (newIsZero)
            {
                if (!newSameAsCurrent)
                {
                    vmState.Refund += sClearRefunds;
                    if (vm.TxTracer.IsTracingRefunds)
                        vm.TxTracer.ReportRefund(sClearRefunds);
                }
            }
            // When setting a non-zero value over an existing zero, apply the difference in gas costs.
            else if (currentIsZero)
            {
                if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable))
                    goto OutOfGas;
            }
        }
        else // Net metered calculations.
        {
            if (newSameAsCurrent)
            {
                if (!UpdateGas(spec.GetNetMeteredSStoreCost(), ref gasAvailable))
                    goto OutOfGas;
            }
            else
            {
                // Retrieve the original storage value to determine if this is a reversal.
                Span<byte> originalValue = vm.WorldState.GetOriginal(in storageCell);
                bool originalIsZero = originalValue.IsZero();
                bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);

                if (currentSameAsOriginal)
                {
                    if (currentIsZero)
                    {
                        if (!UpdateGas(GasCostOf.SSet, ref gasAvailable))
                            goto OutOfGas;
                    }
                    else
                    {
                        if (!UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable))
                            goto OutOfGas;

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (vm.TxTracer.IsTracingRefunds)
                                vm.TxTracer.ReportRefund(sClearRefunds);
                        }
                    }
                }
                else
                {
                    long netMeteredStoreCost = spec.GetNetMeteredSStoreCost();
                    if (!UpdateGas(netMeteredStoreCost, ref gasAvailable))
                        goto OutOfGas;

                    if (!originalIsZero)
                    {
                        // Adjust refunds based on a change from or to a zero value.
                        if (currentIsZero)
                        {
                            vmState.Refund -= sClearRefunds;
                            if (vm.TxTracer.IsTracingRefunds)
                                vm.TxTracer.ReportRefund(-sClearRefunds);
                        }

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (vm.TxTracer.IsTracingRefunds)
                                vm.TxTracer.ReportRefund(sClearRefunds);
                        }
                    }

                    // If the new value reverts to the original, grant a reversal refund.
                    bool newSameAsOriginal = Bytes.AreEqual(originalValue, bytes);
                    if (newSameAsOriginal)
                    {
                        long refundFromReversal = originalIsZero
                            ? spec.GetSetReversalRefund()
                            : spec.GetClearReversalRefund();

                        vmState.Refund += refundFromReversal;
                        if (vm.TxTracer.IsTracingRefunds)
                            vm.TxTracer.ReportRefund(refundFromReversal);
                    }
                }
            }
        }

        // Only update storage if the new value differs from the current value.
        if (!newSameAsCurrent)
        {
            vm.WorldState.Set(in storageCell, newIsZero ? BytesZero : bytes.ToArray());
        }

        // Report storage changes for tracing if enabled.
        if (TTracingInst.IsActive)
        {
            ReadOnlySpan<byte> valueToStore = newIsZero ? BytesZero.AsSpan() : bytes;
            byte[] storageBytes = new byte[32]; // Allocated on the heap to avoid stack allocation.
            storageCell.Index.ToBigEndian(storageBytes);
            vm.TxTracer.ReportStorageChange(storageBytes, valueToStore);
        }

        if (vm.TxTracer.IsTracingStorage)
        {
            vm.TxTracer.SetOperationStorage(storageCell.Address, result, bytes, currentValue);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    }

    /// <summary>
    /// Executes the storage load (SLOAD) instruction.
    /// <para>
    /// Pops a key from the stack, retrieves the corresponding persistent storage value for the executing account,
    /// and pushes that value onto the stack.
    /// </para>
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The EVM stack.</param>
    /// <param name="gasAvailable">The remaining gas, reduced by the SLOAD cost and any storage access gas adjustments.</param>
    /// <param name="programCounter">The program counter (unused in this instruction).</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating the result of the operation.</returns>
    [SkipLocalsInit]
    internal static EvmExceptionType InstructionSLoad<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;

        // Increment the SLOAD opcode metric.
        Metrics.IncrementSLoadOpcode();

        // Deduct the gas cost for performing an SLOAD.
        gasAvailable -= spec.GetSLoadCost();

        // Pop the key from the stack; if unavailable, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        // Construct the storage cell for the executing account.
        Address executingAccount = vm.EvmState.Env.ExecutingAccount;
        StorageCell storageCell = new(executingAccount, in result);

        // Charge additional gas based on whether the storage cell is hot or cold.
        if (!ChargeStorageAccessGas(ref gasAvailable, vm, in storageCell, StorageAccessType.SLOAD, spec))
        {
            goto OutOfGas;
        }

        // Retrieve the persistent storage value and push it onto the stack.
        ReadOnlySpan<byte> value = vm.WorldState.Get(in storageCell);
        stack.PushBytes<TTracingInst>(value);

        // Log the storage load operation if tracing is enabled.
        if (vm.TxTracer.IsTracingStorage)
        {
            vm.TxTracer.LoadOperationStorage(executingAccount, result, value);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements the CALLDATALOAD opcode.
    /// Loads 32 bytes of call data starting from a position specified on the stack,
    /// zero-padding if necessary.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCallDataLoad<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.VeryLow;

        // Pop the offset from which to load call data.
        if (!stack.PopUInt256(out UInt256 result))
            goto StackUnderflow;
        // Load 32 bytes from input data, applying zero padding as needed.
        stack.PushBytes<TTracingInst>(vm.EvmState.Env.InputData.SliceWithZeroPadding(result, 32));

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
