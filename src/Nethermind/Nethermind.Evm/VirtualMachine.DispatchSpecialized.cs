// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.Tracing;
#if DEBUG
using Nethermind.Evm.Tracing.Debugger;
#endif

namespace Nethermind.Evm;

/// <summary>
/// The interpreter dispatch loop. A direct <c>switch</c> over the hot opcodes (so they dispatch
/// without the function-pointer <c>calli</c> and the JIT can inline the handler bodies); every
/// other opcode falls through to the per-fork function-pointer table. The two opcode-availability
/// flags the loop gates on are lifted to compile-time <see cref="IFlag"/> type args (<c>TShift</c>
/// for EIP-145 SHL/SHR, <c>TPush0</c> for EIP-3855 PUSH0) so the JIT folds the gates and drops the
/// untaken cases. Every other hot opcode is fork-invariant, so one loop body serves all forks.
/// </summary>
public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <summary>
    /// Executes the EVM bytecode by iterating over the instruction set and invoking corresponding opcode methods
    /// via function pointers. The method leverages compile-time evaluation of tracing and cancellation flags to optimize
    /// conditional branches. It also updates the VM state as instructions are executed, handles exceptions,
    /// and returns an appropriate <see cref="CallResult"/>.
    /// </summary>
    /// <typeparam name="TTracingInst">
    /// A struct implementing <see cref="IFlag"/> that indicates at compile time whether tracing-specific logic should be enabled.
    /// </typeparam>
    /// <typeparam name="TCancelable">
    /// A struct implementing <see cref="IFlag"/> that indicates at compile time whether cancellation support is enabled.
    /// </typeparam>
    /// <param name="stack">
    ///     A reference to the current EVM stack used for execution.
    /// </param>
    /// <param name="gas">
    ///     The gas state to update
    /// </param>
    /// <returns>
    /// A <see cref="CallResult"/> that encapsulates the outcome of the execution, which can be a successful result,
    /// an empty result, a revert, or a failure due to an exception (such as out-of-gas).
    /// </returns>
    /// <remarks>
    /// The method uses an unsafe context and function pointers to invoke opcode implementations directly,
    /// which minimizes overhead and allows aggressive inlining and compile-time optimizations.
    /// </remarks>
    // Poll cancellation every 1024 opcodes (mask of low bits of the per-frame op counter).
    private const int CancellationCheckMask = 1023;


    [SkipLocalsInit]
    private CallResult RunByteCodeCore<TTracingInst, TCancelable, TShift, TPush0>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TShift : struct, IFlag
        where TPush0 : struct, IFlag
    {
        // Reset return data before executing the current frame.
        ReturnData = null;

        // Initialize the exception type to "None".
        EvmExceptionType exceptionType = EvmExceptionType.None;
#if DEBUG
        // In debug mode, retrieve a tracer for interactive debugging.
        DebugTracer<TGasPolicy>? debugger = _txTracer.GetTracer<DebugTracer<TGasPolicy>>();
#endif

        // Set the program counter from the current VM state; it may not be zero if resuming after a call.
        int programCounter = VmState.ProgramCounter;
        // Pin the opcode methods array to obtain a fixed pointer, avoiding repeated bounds checks.
        // If we don't use a pointer we have bounds checks (however only 256 opcodes and opcode is a byte so know always in bounds).
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] opcodeArray = _opcodeMethods;
        fixed (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>* opcodeMethods = &opcodeArray[0])
        {
            int opCodeCount = 0;
            // Iterate over the instructions using a while loop because opcodes may modify the program counter.
            ref Instruction code = ref Unsafe.As<byte, Instruction>(ref stack.Code);
            uint codeLength = (uint)stack.CodeLength;
            // Call depth does not change during dispatch; hoist outside the loop so a no-op
            // OnBeforeInstructionTrace doesn't force a per-instruction VmState.Env chase.
            int callDepth = VmState.Env.CallDepth;
            while ((uint)programCounter < codeLength)
            {
#if DEBUG
                // Allow the debugger to inspect and possibly pause execution for debugging purposes.
                debugger?.TryWait(ref _currentState, ref programCounter, ref gas, ref stack.Head);
#endif
                // Fetch the current instruction from the code section.
                Instruction instruction = Unsafe.Add(ref code, programCounter);

                // IsCancelled is an interface call; polling it per opcode is measurable on the
                // cancelable (eth_call) path. Every 1024 opcodes still aborts within microseconds.
                if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0 && _txTracer.IsCancelled)
                    ThrowOperationCanceledException();

                // Call gas policy hook before instruction execution.
                TGasPolicy.OnBeforeInstructionTrace(in gas, programCounter, instruction, callDepth);

                // If tracing is enabled, start an instruction trace.
                if (TTracingInst.IsActive)
                    StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), programCounter, in stack);

                // Advance the program counter to point to the next instruction.
                programCounter++;
                opCodeCount++;

                // Dispatch through a stack temp by ref so programCounter stays register-resident
                // for the loop's fetch/bounds/++; passing ref programCounter directly to the
                // handlers (incl. the calli) address-takes it, forcing a frame reload every opcode.
                int pc = programCounter;
                // Direct dispatch for the measured-hot opcodes; the rest take the table.
                // MUST stay inline in the loop: extracted, the JIT stops inlining the
                // handlers and direct dispatch loses to the table's calli. The fork-gated
                // cases check IFlag constants the JIT folds per instantiation.
                switch (instruction)
                {
                        case Instruction.ADD:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpAdd, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SUB:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpSub, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.MUL:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpMul, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.LT:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpLt, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.GT:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpGt, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.EQ:
                            exceptionType = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseEq>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.ISZERO:
                            exceptionType = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpIsZero>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.AND:
                            exceptionType = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseAnd>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.OR:
                            exceptionType = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseOr>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.NOT:
                            exceptionType = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpNot>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SHL:
                            if (!TShift.IsActive) goto default;
                            exceptionType = EvmInstructions.InstructionShift<TGasPolicy, EvmInstructions.OpShl, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SHR:
                            if (!TShift.IsActive) goto default;
                            exceptionType = EvmInstructions.InstructionShift<TGasPolicy, EvmInstructions.OpShr, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.CALLDATALOAD:
                            exceptionType = EvmInstructions.InstructionCallDataLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.MLOAD:
                            exceptionType = EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.MSTORE:
                            exceptionType = EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SLOAD:
                            exceptionType = EvmInstructions.InstructionSLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.JUMP:
                            exceptionType = EvmInstructions.InstructionJump(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.JUMPI:
                            exceptionType = EvmInstructions.InstructionJumpIf(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.JUMPDEST:
                            exceptionType = EvmInstructions.InstructionJumpDest(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.POP:
                            exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.PUSH0:
                            if (!TPush0.IsActive) goto default;
                            exceptionType = EvmInstructions.InstructionPush0<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.PUSH1:
                            exceptionType = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.PUSH2:
                            exceptionType = EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.PUSH3:
                            exceptionType = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.PUSH4:
                            exceptionType = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP1:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP2:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP3:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP4:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP5:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SWAP1:
                            exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SWAP2:
                            exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SWAP3:
                            exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        default:
                            exceptionType = opcodeMethods[(int)instruction](this, ref stack, ref gas, ref pc);
                            break;
                    }
                    programCounter = pc;

                // If gas is exhausted, jump to the out-of-gas handler.
                if (TGasPolicy.GetRemainingGas(in gas) < 0)
                {
                    OpCodeCount += opCodeCount;
                    goto OutOfGas;
                }

                // Call gas policy hook after instruction execution.
                TGasPolicy.OnAfterInstructionTrace(in gas);

                // If an exception occurred, exit the loop.
                if (exceptionType != EvmExceptionType.None)
                    break;

                // If tracing is enabled, complete the trace for the current instruction.
                if (TTracingInst.IsActive)
                    EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

                // If return data has been set, exit the loop to process the returned value.
                // Only the 0xF0+ family sets it (RETURN returns None and signals completion
                // solely through it), so the field load is skipped for the cheap majority below CREATE.
                if (instruction >= Instruction.CREATE && ReturnData is not null)
                {
                    break;
                }
            }

            OpCodeCount += opCodeCount;
        }

        // Update the current VM state if no fatal exception occurred, or if the exception is of type Stop or Revert.
        if (exceptionType is EvmExceptionType.None or EvmExceptionType.Stop or EvmExceptionType.Revert)
        {
            // If tracing is enabled, complete the trace for the current instruction.
            if (TTracingInst.IsActive)
                EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));
            UpdateCurrentState(programCounter, in gas, stack.Head);
        }
        else
        {
            // For any other exception, jump to the failure handling routine.
            goto ReturnFailure;
        }

        // If the exception indicates a revert, handle it specifically.
        if (exceptionType == EvmExceptionType.Revert)
            goto Revert;
        // If return data was produced, jump to the return data processing block.
        if (ReturnData is not null)
            goto DataReturn;

        // If no return data is produced, return an empty call result.
#if DEBUG
        debugger?.TryWait(ref _currentState, ref programCounter, ref gas, ref stack.Head);
#endif
        return CallResult.Empty();

    DataReturn:
#if DEBUG
        // Allow debugging before processing the return data.
        debugger?.TryWait(ref _currentState, ref programCounter, ref gas, ref stack.Head);
#endif
        // Process the return data based on its runtime type.
        if (ReturnData is byte[] data)
        {
            // Fall back to returning a CallResult with a byte array as the return data.
            return new CallResult(data, null);
        }
        else if (ReturnData is VmState<TGasPolicy> state)
        {
            return new CallResult(state);
        }
        return new CallResult(ReturnDataBuffer, null);

    Revert:
        // Return a CallResult indicating a revert.
        return new CallResult((byte[])ReturnData, null, shouldRevert: true, exceptionType);

    OutOfGas:
        TGasPolicy.SetOutOfGas(ref gas);
        // Set the exception type to OutOfGas if gas has been exhausted.
        exceptionType = EvmExceptionType.OutOfGas;
    ReturnFailure:
        // EIP-8037: write gas back to state on failure so RestoreChildStateGasOnHalt
        // can read accumulated StateGasUsed/StateGasSpill from the child frame.
        _currentState.Gas = gas;
        // Return a failure CallResult based on the remaining gas and the exception type.
        return GetFailureReturn(TGasPolicy.GetRemainingGas(in gas), exceptionType);

        [DoesNotReturn]
        static void ThrowOperationCanceledException() => throw new OperationCanceledException("Cancellation Requested");
    }
}
