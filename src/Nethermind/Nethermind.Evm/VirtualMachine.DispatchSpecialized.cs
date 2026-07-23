// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
#if ZK_EVM
using Nethermind.Evm.GasPolicy;
#endif
using Nethermind.Evm.Tracing;
#if DEBUG
using Nethermind.Evm.Tracing.Debugger;
#endif

namespace Nethermind.Evm;

/// <summary>
/// The interpreter dispatch loop. A direct <c>switch</c> over the hot opcodes (so they dispatch without the
/// function-pointer <c>calli</c> and the JIT can inline the handler bodies); every other opcode falls through
/// to the per-fork function-pointer table. Opcode-availability gates are lifted to compile-time <see cref="IFlag"/>
/// type args (<c>TShift</c> for EIP-145 SHL/SHR, <c>TPush0</c> for EIP-3855 PUSH0) so the JIT folds them and
/// drops the untaken cases. Every other hot opcode is fork-invariant, so one loop body serves all forks.
/// </summary>
public unsafe partial class VirtualMachine<TGasPolicy>
{
    // Poll cancellation every 1024 opcodes (low bits of the per-frame op counter).
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
        ReturnData = null;
        EvmExceptionType exceptionType = EvmExceptionType.None;
#if DEBUG
        DebugTracer<TGasPolicy>? debugger = _txTracer.GetTracer<DebugTracer<TGasPolicy>>();
#endif

        // May not be zero when resuming after a call.
        int programCounter = VmState.ProgramCounter;
        // Pinned pointer drops the per-dispatch bounds check (opcode is a byte, always in range).
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] opcodeArray = _opcodeMethods;
        fixed (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>* opcodeMethods = &opcodeArray[0])
        {
            int opCodeCount = 0;
            ref Instruction code = ref Unsafe.As<byte, Instruction>(ref stack.Code);
            uint codeLength = (uint)stack.CodeLength;
            // Hoisted: a no-op OnBeforeInstructionTrace would otherwise chase VmState.Env per instruction.
            int callDepth = VmState.Env.CallDepth;
            while ((uint)programCounter < codeLength)
            {
#if DEBUG
                debugger?.TryWait(ref _currentState, ref programCounter, ref gas, ref stack.Head);
#endif
                Instruction instruction = Unsafe.Add(ref code, programCounter);

                // IsCancelled is an interface call; polling it per opcode is measurable on the
                // cancelable (eth_call) path. Every 1024 opcodes still aborts within microseconds.
                if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0 && _txTracer.IsCancelled)
                    ThrowOperationCanceledException();

                TGasPolicy.OnBeforeInstructionTrace(in gas, programCounter, instruction, callDepth);

                if (TTracingInst.IsActive)
                    StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), programCounter, in stack);

                programCounter++;
                opCodeCount++;

                // Stack temp by ref keeps programCounter register-resident; passing ref programCounter to the
                // handlers (incl. the calli) address-takes it, forcing a frame reload every opcode.
                int pc = programCounter;
                bool directDispatch =
#if ZK_EVM
                // The guest has no I-cache and counts executed instructions, so inlining the hot handlers
                // is a win on every path; it always direct-dispatches, regardless of cancelability.
                true;
#else
                // The direct-dispatch switch inlines the hot handlers and pays off only for the cancelable
                // (eth_call/simulation) path, where a few hot contracts run repeatedly and stay in I-cache.
                // Block processing runs a diverse opcode mix across many contracts, where the switch's
                // jump-table indirection and code-size pressure measurably regress throughput versus the
                // plain function-pointer table; that path takes the table below.
                TCancelable.IsActive;
#endif
                // directDispatch folds at compile time, so this specializes into two loop bodies with no runtime branch.
                if (directDispatch)
                {
#if ZK_EVM
                    // Narrow, frequency-ordered switch (measured on real Glamsterdam blocks; these
                    // cover ~78% of executed opcodes). The wide CPU switch below exhausts the
                    // compiler's inline budget, so every case degrades into an out-of-line handler
                    // call; keeping the method small lets the hot handlers actually inline.
                    switch (instruction)
                    {
                        case Instruction.PUSH1:
                            exceptionType = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.PUSH2:
                            exceptionType = EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.ADD:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpAdd, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SWAP1:
                            exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP2:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.MSTORE:
                            exceptionType = EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.MLOAD:
                            exceptionType = EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP1:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.POP:
                            exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP3:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.SWAP2:
                            exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.ISZERO:
                            exceptionType = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpIsZero>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.DUP5:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.JUMPDEST:
                            exceptionType = EvmInstructions.InstructionJumpDest(this, ref stack, ref gas, ref pc);
                            break;
                        default:
                            exceptionType = opcodeMethods[(int)instruction](this, ref stack, ref gas, ref pc);
                            break;
                    }
#else
                    // Direct dispatch for the measured-hot opcodes; the rest take the table. MUST stay inline:
                    // extracted, the JIT stops inlining the handlers and direct dispatch loses to the table's calli.
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
                            exceptionType = TTracingInst.IsActive
                                ? EvmInstructions.InstructionJump(this, ref stack, ref gas, ref pc)
                                : EvmInstructions.InstructionJumpAndSkipJumpDest(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.JUMPI:
                            exceptionType = TTracingInst.IsActive
                                ? EvmInstructions.InstructionJumpIf(this, ref stack, ref gas, ref pc)
                                : EvmInstructions.InstructionJumpIfAndSkipJumpDest(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.JUMPDEST:
                            exceptionType = EvmInstructions.InstructionJumpDest(this, ref stack, ref gas, ref pc);
                            break;
                        case Instruction.POP:
                            exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gas, ref pc);
                            break;
#if ZK_EVM
                        // GAS is hot on the guest but absent from mainline's curated eth_call set.
                        case Instruction.GAS:
                            exceptionType = EvmInstructions.InstructionGas<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref pc);
                            break;
#endif
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
#endif
                }
                else
                {
                    // Block-processing path: plain function-pointer table (master-parity dispatch), with POP
                    // kept inline as the single measurably-hot, fork-invariant special case.
                    if (Instruction.POP == instruction)
                        exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gas, ref pc);
                    else
                        exceptionType = opcodeMethods[(int)instruction](this, ref stack, ref gas, ref pc);
                }
                programCounter = pc;

#if ZK_EVM
                // Inside this huge method the compiler's inline budget is exhausted, so even the
                // trivial policy calls below compile to out-of-line calls executed once per opcode
                // (~5% of guest execution). The typeof checks fold at compile time per
                // instantiation, so the common EthereumGasPolicy loop reads the flag directly and
                // skips its empty OnAfterInstructionTrace without a call.
                bool outOfGas = typeof(TGasPolicy) == typeof(EthereumGasPolicy)
                    ? Unsafe.As<TGasPolicy, EthereumGasPolicy>(ref gas).OutOfGas
                    : TGasPolicy.IsOutOfGas(in gas);
                if (outOfGas)
                {
                    OpCodeCount += opCodeCount;
                    goto OutOfGas;
                }

                if (typeof(TGasPolicy) != typeof(EthereumGasPolicy))
                    TGasPolicy.OnAfterInstructionTrace(in gas);
#else
                if (TGasPolicy.IsOutOfGas(in gas))
                {
                    OpCodeCount += opCodeCount;
                    goto OutOfGas;
                }

                TGasPolicy.OnAfterInstructionTrace(in gas);
#endif

                if (exceptionType != EvmExceptionType.None)
                    break;

#if ZK_EVM
                // typeof folds at compile time even where the inliner gives up on the static
                // abstract IsActive getter (see the gas-policy note above), which otherwise
                // compiles to an out-of-line call returning false once per executed opcode.
                if (typeof(TTracingInst) != typeof(OffFlag))
                    EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));
#else
                if (TTracingInst.IsActive)
                    EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));
#endif

                // Only the 0xF0+ family sets ReturnData (RETURN returns None and signals completion solely
                // through it), so the field load is skipped for the cheap majority below CREATE.
                if (instruction >= Instruction.CREATE && ReturnData is not null)
                {
                    break;
                }
            }

            OpCodeCount += opCodeCount;
        }

        if (exceptionType is EvmExceptionType.None or EvmExceptionType.Stop or EvmExceptionType.Revert)
        {
            if (TTracingInst.IsActive)
                EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));
            UpdateCurrentState(programCounter, in gas, stack.Head);
        }
        else
        {
            goto ReturnFailure;
        }

        if (exceptionType == EvmExceptionType.Revert)
            goto Revert;
        if (ReturnData is not null)
            goto DataReturn;

#if DEBUG
        debugger?.TryWait(ref _currentState, ref programCounter, ref gas, ref stack.Head);
#endif
        return CallResult.Empty();

    DataReturn:
#if DEBUG
        debugger?.TryWait(ref _currentState, ref programCounter, ref gas, ref stack.Head);
#endif
        if (ReturnData is byte[] data)
        {
            return new CallResult(data, null);
        }
        else if (ReturnData is VmState<TGasPolicy> state)
        {
            return new CallResult(state);
        }
        return new CallResult(ReturnDataBuffer, null);

    Revert:
        return new CallResult((byte[])ReturnData, null, shouldRevert: true, exceptionType);

    OutOfGas:
        TGasPolicy.SetOutOfGas(ref gas);
        exceptionType = EvmExceptionType.OutOfGas;
    ReturnFailure:
        // EIP-8037: write gas back to state on failure so RestoreChildStateGasOnHalt can read
        // accumulated StateGasUsed/StateGasSpill from the child frame.
        _currentState.Gas = gas;
        return GetFailureReturn(TGasPolicy.GetRemainingGas(in gas), exceptionType);

        [DoesNotReturn]
        static void ThrowOperationCanceledException() => throw new OperationCanceledException("Cancellation Requested");
    }
}
