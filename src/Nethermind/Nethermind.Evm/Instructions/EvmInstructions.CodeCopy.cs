// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    /// <summary>
    /// Provides a mechanism to retrieve a code segment for code copy operations.
    /// Implementers return a ReadOnlySpan of bytes representing the code to copy.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpCodeCopy<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        /// <summary>
        /// Gets the code to be copied.
        /// </summary>
        /// <param name="vm">The virtual machine instance providing execution context.</param>
        /// <returns>A read-only span of bytes containing the code.</returns>
        abstract static ReadOnlySpan<byte> GetCode(VirtualMachine<TGasPolicy> vm);
    }

    /// <summary>
    /// Copies a portion of code (or call data) into memory.
    /// Pops three parameters from the stack: destination memory offset, source offset, and length.
    /// It then deducts gas based on the memory expansion and performs the copy using the provided code source.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpCodeCopy">
    /// A struct implementing <see cref="IOpCodeCopy"/> that defines the code source to copy from.
    /// </typeparam>
    /// <typeparam name="TTracingInst">
    /// A struct implementing <see cref="IFlag"/> that indicates whether tracing is active.
    /// </typeparam>
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">The EVM stack used for operand retrieval and result storage.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the current program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success, or an appropriate error code if an error occurs.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCodeCopy<TGasPolicy, TOpCodeCopy, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCodeCopy : struct, IOpCodeCopy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Pop destination offset, source offset, and copy length.
        if (!stack.PopUInt256(out UInt256 a) ||
            !stack.PopUInt256(out UInt256 b) ||
            !stack.PopUInt256(out UInt256 result))
            goto StackUnderflow;

        // Deduct gas for the operation plus the cost for memory expansion.
        // Gas cost is calculated as a fixed "VeryLow" cost plus a per-32-bytes cost.
        TGasPolicy.ConsumeDataCopyGas(ref gas, isExternalCode: false, GasCostOf.VeryLow, GasCostOf.Memory * EvmCalculations.Div32Ceiling(in result, out bool outOfGas));
        if (outOfGas) goto OutOfGas;

        // Only perform the copy if length (result) is non-zero.
        if (!result.IsZero)
        {
            // Check and update memory expansion cost.
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in a, result, vm.VmState))
                goto OutOfGas;

            // Obtain the code slice with zero-padding if needed.
            ZeroPaddedSpan slice = TOpCodeCopy.GetCode(vm).SliceWithZeroPadding(in b, (int)result);
            // Save the slice into memory at the destination offset.
            if (!vm.VmState.Memory.TrySave(in a, in slice)) goto OutOfGas;

            // If tracing is enabled, report the memory change.
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
    /// Retrieves call data as the source for a code copy.
    /// </summary>
    public struct OpCallDataCopy<TGasPolicy> : IOpCodeCopy<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        public static ReadOnlySpan<byte> GetCode(VirtualMachine<TGasPolicy> vm)
            => vm.VmState.Env.InputData.Span;
    }

    /// <summary>
    /// Retrieves the executing code as the source for a code copy.
    /// </summary>
    public struct OpCodeCopy<TGasPolicy> : IOpCodeCopy<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        public static ReadOnlySpan<byte> GetCode(VirtualMachine<TGasPolicy> vm)
            => vm.VmState.Env.CodeInfo.CodeSpan;
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
            !stack.PopUInt256(out UInt256 a) ||
            !stack.PopUInt256(out UInt256 b) ||
            !stack.PopUInt256(out UInt256 result))
            goto StackUnderflow;

        // Deduct gas cost: cost for external code access plus memory expansion cost.
        TGasPolicy.ConsumeDataCopyGas(ref gas, isExternalCode: true, spec.GetExtCodeCost(), GasCostOf.Memory * EvmCalculations.Div32Ceiling(in result, out bool outOfGas));
        if (outOfGas) goto OutOfGas;

        // Charge gas for account access (considering hot/cold storage costs).
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address))
            goto OutOfGas;

        if (!result.IsZero)
        {
            // Update memory cost if the destination region requires expansion.
            if (!TGasPolicy.UpdateMemoryCost(ref gas, in a, result, vm.VmState))
                goto OutOfGas;

            ICodeInfo codeInfo = vm.CodeInfoRepository
                .GetCachedCodeInfo(address, followDelegation: false, spec, out _);

            // Get the external code from the repository.
            ReadOnlySpan<byte> externalCode = codeInfo.CodeSpan;
            // If contract is large, charge for access
            if (spec.IsEip7907Enabled)
            {
                uint excessContractSize = (uint)Math.Max(0, externalCode.Length - CodeSizeConstants.MaxCodeSizeEip170);
                if (excessContractSize > 0 && !ChargeForLargeContractAccess(excessContractSize, address, in vm.VmState.AccessTracker, ref gas))
                    goto OutOfGas;
            }

            // If EOF is enabled and the code is an EOF contract, use a predefined magic value.
            if (spec.IsEofEnabled && EofValidator.IsEof(externalCode, out _))
            {
                externalCode = EofValidator.MAGIC;
            }

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
        TGasPolicy.Consume(ref gas, spec.GetExtCodeCost());

        // Pop the account address from the stack.
        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;

        // Charge gas for accessing the account's state.
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address))
            goto OutOfGas;

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
                if (!isCodeLengthNotZero)
                {
                    stack.PushOne<TTracingInst>();
                }
                else
                {
                    stack.PushZero<TTracingInst>();
                }
                return EvmExceptionType.None;
            }
        }

        // No optimization applied: load the account's code from storage.
        ReadOnlySpan<byte> accountCode = vm.CodeInfoRepository
            .GetCachedCodeInfo(address, followDelegation: false, spec, out _)
            .CodeSpan;
        // If EOF is enabled and the code is an EOF contract, push a fixed size (2).
        if (spec.IsEofEnabled && EofValidator.IsEof(accountCode, out _))
        {
            stack.PushUInt32<TTracingInst>(2);
        }
        else
        {
            // Otherwise, push the actual code length.
            stack.PushUInt32<TTracingInst>((uint)accountCode.Length);
        }
        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
