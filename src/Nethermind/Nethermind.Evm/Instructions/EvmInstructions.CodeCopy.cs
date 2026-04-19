// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

using Int256;
using Nethermind.Evm.State;

public static partial class EvmInstructions
{
    /// <summary>
    /// Shared copy-to-memory core used by CODECOPY and CALLDATACOPY. Pops three parameters from
    /// the stack (destination offset, source offset, length), deducts the fixed + per-word gas,
    /// and performs the copy from <paramref name="source"/> into memory with zero-padding.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType DataCopy<TGasPolicy, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        scoped ReadOnlySpan<byte> source)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!stack.PopUInt256(out UInt256 a, out UInt256 b, out UInt256 result))
            goto StackUnderflow;

        TGasPolicy.ConsumeDataCopyGas(ref gas, isExternalCode: false, GasCostOf.VeryLow, GasCostOf.Memory * EvmCalculations.Div32Ceiling(in result, out bool outOfGas));
        if (outOfGas) goto OutOfGas;

        if (!result.IsZero)
        {
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in a, result, vm.VmState))
                goto OutOfGas;

            ZeroPaddedSpan slice = source.SliceWithZeroPadding(in b, (int)result);
            if (!vm.VmState.Memory.TrySave(in a, in slice)) goto OutOfGas;

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportMemoryChange(a, in slice);
            }
        }

        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// CODECOPY - copies a portion of the executing contract's code into memory.
    /// Sources bytes from <c>stack.Code</c>/<c>stack.CodeLength</c> (hoisted at frame entry)
    /// rather than re-walking <c>vm.VmState.Env.CodeInfo.CodeSpan</c>.
    /// </summary>
    public static EvmExceptionType InstructionCodeCopy<TGasPolicy, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
        => DataCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas,
            MemoryMarshal.CreateReadOnlySpan(ref stack.Code, stack.CodeLength));

    /// <summary>
    /// CALLDATACOPY - copies a portion of the transaction's calldata into memory.
    /// </summary>
    public static EvmExceptionType InstructionCallDataCopy<TGasPolicy, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
        => DataCopy<TGasPolicy, TTracingInst>(vm, ref stack, ref gas,
            vm.VmState.Env.InputData.Span);

    /// <summary>
    /// Copies data from the previous call's return buffer into memory.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturnDataCopy<TGasPolicy, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        if (!stack.PopUInt256(out UInt256 destOffset, out UInt256 sourceOffset, out UInt256 size))
            goto StackUnderflow;

        TGasPolicy.ConsumeDataCopyGas(ref gas, isExternalCode: false, GasCostOf.VeryLow, GasCostOf.Memory * EvmCalculations.Div32Ceiling(in size, out bool outOfGas));
        if (outOfGas) goto OutOfGas;

        ReadOnlyMemory<byte> returnDataBuffer = vm.ReturnDataBuffer;
        if (UInt256.AddOverflow(size, sourceOffset, out UInt256 result) || result > returnDataBuffer.Length)
            goto AccessViolation;

        if (!size.IsZero)
        {
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in destOffset, size, vm.VmState))
                goto OutOfGas;

            ZeroPaddedSpan slice = returnDataBuffer.Span.SliceWithZeroPadding(sourceOffset, (int)size);
            if (!vm.VmState.Memory.TrySave(in destOffset, in slice)) goto OutOfGas;

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportMemoryChange(destOffset, in slice);
            }
        }

        return EvmExceptionType.None;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    AccessViolation:
        return EvmExceptionType.AccessViolation;
    }

    /// <summary>
    /// Copies external code (from another account) into memory.
    /// Pops an address and three parameters (destination offset, source offset, and length) from the stack.
    /// Validates account access and memory expansion, then copies the external code into memory.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TTracingInst">
    /// A struct implementing <see cref="IFlag"/> that indicates whether tracing is active.
    /// </typeparam>
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">The EVM stack for operand retrieval and memory copy operations.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success, or an appropriate error code on failure.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeCopy<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        // Retrieve the target account address.
        Address address = stack.PopAddress();
        // Pop destination offset, source offset, and length from the stack.
        if (address is null ||
            !stack.PopUInt256(out UInt256 a, out UInt256 b, out UInt256 result))
            goto StackUnderflow;

        // Deduct gas cost: cost for external code access plus memory expansion cost.
        TGasPolicy.ConsumeDataCopyGas(ref gas, isExternalCode: true, spec.GasCosts.ExtCodeCost, GasCostOf.Memory * EvmCalculations.Div32Ceiling(in result, out bool outOfGas));
        if (outOfGas) goto OutOfGas;

        // Charge gas for account access (considering hot/cold storage costs).
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address))
            goto OutOfGas;

        if (!result.IsZero)
        {
            // Update memory cost if the destination region requires expansion.
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in a, result, vm.VmState))
                goto OutOfGas;

            vm.WorldState.AddAccountRead(address);

            CodeInfo codeInfo = vm.CodeInfoRepository
                .GetCachedCodeInfo(address, followDelegation: false, spec, out _);

            // Get the external code from the repository.
            ReadOnlySpan<byte> externalCode = codeInfo.CodeSpan;

            // Slice the external code starting at the source offset with appropriate zero-padding.
            ZeroPaddedSpan slice = externalCode.SliceWithZeroPadding(in b, (int)result);
            // Save the slice into memory at the destination offset.
            if (!vm.VmState.Memory.TrySave(in a, in slice)) goto OutOfGas;

            // Report memory changes if tracing is enabled.
            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportMemoryChange(a, in slice);
            }
        }
        else
        {
            vm.WorldState.AddAccountRead(address);
        }

        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Retrieves the size of the external code of an account.
    /// Pops an account address from the stack, validates access, and pushes the code size onto the stack.
    /// Additionally, applies peephole optimizations for common contract checks.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TTracingInst">
    /// A struct implementing <see cref="IFlag"/> indicating if instruction tracing is active.
    /// </typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The EVM stack from which the account address is popped and where the code size is pushed.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter, which may be adjusted during optimization.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success, or an appropriate error code if an error occurs.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeSize<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        // Deduct the gas cost for external code access.
        TGasPolicy.Consume(ref gas, spec.GasCosts.ExtCodeCost);

        // Pop the account address from the stack.
        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;

        // Charge gas for accessing the account's state.
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address))
            goto OutOfGas;

        vm.WorldState.AddAccountRead(address);

        // Attempt a peephole optimization when tracing is not active and code is available.
        ReadOnlySpan<byte> codeSection = vm.VmState.Env.CodeInfo.CodeSpan;
        if (!TTracingInst.IsActive && programCounter < codeSection.Length)
        {
            bool optimizeAccess = false;
            // Peek at the next instruction to detect patterns.
            Instruction nextInstruction = (Instruction)codeSection[programCounter];
            // If the next instruction is ISZERO, optimize for a simple contract check.
            if (nextInstruction == Instruction.ISZERO)
            {
                optimizeAccess = true;
            }
            // If the next instruction is GT or EQ and the top stack element is zero,
            // then this pattern likely corresponds to a contract existence check.
            else if ((nextInstruction == Instruction.GT || nextInstruction == Instruction.EQ) &&
                     stack.PeekUInt256IsZero())
            {
                optimizeAccess = true;
                // Remove the zero from the stack since we will have consumed it.
                if (!stack.PopLimbo()) goto StackUnderflow;
            }

            if (optimizeAccess)
            {
                // Peephole optimization for EXTCODESIZE when checking for contract existence.
                // This reduces storage access by using the preloaded CodeHash.
                vm.OpCodeCount++;
                programCounter++;
                // Deduct very-low gas cost for the next operation (ISZERO, GT, or EQ).
                TGasPolicy.Consume(ref gas, GasCostOf.VeryLow);

                // Determine if the account is a contract by checking the loaded CodeHash.
                bool isCodeLengthNotZero = vm.WorldState.IsContract(address);
                // If the original instruction was GT, invert the check to match the semantics.
                if (nextInstruction == Instruction.GT)
                {
                    isCodeLengthNotZero = !isCodeLengthNotZero;
                }

                // Push 1 if the condition is met (indicating contract presence or absence), else push 0.
                return !isCodeLengthNotZero
                    ? stack.PushOne<TTracingInst>()
                    : stack.PushZero<TTracingInst>();
            }
        }

        // No optimization applied: load the account's code from storage.
        ReadOnlySpan<byte> accountCode = vm.CodeInfoRepository
            .GetCachedCodeInfo(address, followDelegation: false, spec, out _)
            .CodeSpan;
        return stack.PushUInt32<TTracingInst>((uint)accountCode.Length);
        // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
