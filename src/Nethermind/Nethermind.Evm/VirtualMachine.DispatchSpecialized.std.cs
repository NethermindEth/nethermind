// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
#if DEBUG
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.Debugger;
#endif

using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    [SkipLocalsInit]
    private partial EvmExceptionType RunDispatchLoop<TTracingInst, TCancelable, TShift, TPush0>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref int programCounter)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TShift : struct, IFlag
        where TPush0 : struct, IFlag
    {
        EvmExceptionType exceptionType = EvmExceptionType.None;
#if DEBUG
        DebugTracer<TGasPolicy>? debugger = _txTracer.GetTracer<DebugTracer<TGasPolicy>>();
#endif
        // Hoisted: reading through the ref parameter would reload from the frame every opcode.
        int pc = programCounter;
        // Pinned pointer drops the per-dispatch bounds check (opcode is a byte, always in range).
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] opcodeArray = _opcodeMethods;
        fixed (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>* opcodeMethods = &opcodeArray[0])
        {
            int opCodeCount = 0;
            ref Instruction code = ref Unsafe.As<byte, Instruction>(ref stack.Code);
            uint codeLength = (uint)stack.CodeLength;
            // Hoisted: a no-op OnBeforeInstructionTrace would otherwise chase VmState.Env per instruction.
            int callDepth = VmState.Env.CallDepth;
            while ((uint)pc < codeLength)
            {
#if DEBUG
                debugger?.TryWait(ref _currentState, ref pc, ref gas, ref stack.Head);
#endif
                Instruction instruction = Unsafe.Add(ref code, pc);

                // IsCancelled is an interface call; per-opcode polling is measurable, and 1024 still aborts in microseconds.
                if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0 && _txTracer.IsCancelled)
                    ThrowOperationCanceledException();

                TGasPolicy.OnBeforeInstructionTrace(in gas, pc, instruction, callDepth);

                if (TTracingInst.IsActive)
                    StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), pc, in stack);

                pc++;
                opCodeCount++;

                // Temp by ref: ref pc handed to the handlers would address-take it and spill it every opcode.
                int opPc = pc;
                // The switch pays off only on the cancelable (eth_call) path, where hot contracts stay in
                // I-cache; block processing's diverse mix regresses against the table. TCancelable folds.
                if (TCancelable.IsActive)
                {
                    switch (instruction)
                    {
                        case Instruction.ADD:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpAdd, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.SUB:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpSub, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.MUL:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpMul, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.LT:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpLt, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.GT:
                            exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpGt, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.EQ:
                            exceptionType = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseEq>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.ISZERO:
                            exceptionType = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpIsZero>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.AND:
                            exceptionType = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseAnd>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.OR:
                            exceptionType = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseOr>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.NOT:
                            exceptionType = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpNot>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.SHL:
                            if (!TShift.IsActive) goto default;
                            exceptionType = EvmInstructions.InstructionShift<TGasPolicy, EvmInstructions.OpShl, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.SHR:
                            if (!TShift.IsActive) goto default;
                            exceptionType = EvmInstructions.InstructionShift<TGasPolicy, EvmInstructions.OpShr, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.CALLDATALOAD:
                            exceptionType = EvmInstructions.InstructionCallDataLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.MLOAD:
                            exceptionType = EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.MSTORE:
                            exceptionType = EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.SLOAD:
                            exceptionType = EvmInstructions.InstructionSLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.JUMP:
                            exceptionType = TTracingInst.IsActive
                                ? EvmInstructions.InstructionJump(this, ref stack, ref gas, ref opPc)
                                : EvmInstructions.InstructionJumpAndSkipJumpDest(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.JUMPI:
                            exceptionType = TTracingInst.IsActive
                                ? EvmInstructions.InstructionJumpIf(this, ref stack, ref gas, ref opPc)
                                : EvmInstructions.InstructionJumpIfAndSkipJumpDest(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.JUMPDEST:
                            exceptionType = EvmInstructions.InstructionJumpDest(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.POP:
                            exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.PUSH0:
                            if (!TPush0.IsActive) goto default;
                            exceptionType = EvmInstructions.InstructionPush0<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.PUSH1:
                            exceptionType = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.PUSH2:
                            exceptionType = EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.PUSH3:
                            exceptionType = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.PUSH4:
                            exceptionType = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.DUP1:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.DUP2:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.DUP3:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.DUP4:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.DUP5:
                            exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.SWAP1:
                            exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.SWAP2:
                            exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        case Instruction.SWAP3:
                            exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref opPc);
                            break;
                        default:
                            exceptionType = opcodeMethods[(int)instruction](this, ref stack, ref gas, ref opPc);
                            break;
                    }
                }
                else
                {
                    // Master-parity table dispatch, with POP inline as the one measurably-hot special case.
                    if (Instruction.POP == instruction)
                        exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gas, ref opPc);
                    else
                        exceptionType = opcodeMethods[(int)instruction](this, ref stack, ref gas, ref opPc);
                }
                pc = opPc;

                if (TGasPolicy.IsOutOfGas(in gas))
                {
                    OpCodeCount += opCodeCount;
                    TGasPolicy.SetOutOfGas(ref gas);
                    exceptionType = EvmExceptionType.OutOfGas;
                    goto Halted;
                }

                TGasPolicy.OnAfterInstructionTrace(in gas);

                if (exceptionType != EvmExceptionType.None)
                    break;

                if (TTracingInst.IsActive)
                    EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

                // Only the 0xF0+ family sets ReturnData, so the cheap majority skips the field load.
                if (instruction >= Instruction.CREATE && ReturnData is not null)
                {
                    break;
                }
            }

            OpCodeCount += opCodeCount;
        }

    Halted:
        programCounter = pc;
        return exceptionType;
    }
}
