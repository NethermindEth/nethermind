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
#if DEBUG
        DebugTracer<TGasPolicy>? debugger = _txTracer.GetTracer<DebugTracer<TGasPolicy>>();
#endif
        // Pinned pointer drops the per-dispatch bounds check (opcode is a byte, always in range).
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>[] opcodeArray = _opcodeMethods;
        fixed (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, int, OpcodeResult>* opcodeMethods = &opcodeArray[0])
        {
            int opCodeCount = 0;
            ref Instruction code = ref Unsafe.As<byte, Instruction>(ref stack.Code);
            // Held as ulong so the packed-compare loop guard does not re-widen it every iteration.
            ulong codeLength = (uint)stack.CodeLength;
            // Hoisted: a no-op OnBeforeInstructionTrace would otherwise chase VmState.Env per instruction.
            int callDepth = VmState.Env.CallDepth;
            // The packed value keeps the program counter in the low half and the exception in the high
            // half, so this single unsigned compare is both the bounds check and the exception exit.
            OpcodeResult result = new(programCounter);
            while (result.Value < codeLength)
            {
#if DEBUG
                if (debugger is not null)
                {
                    int debugPc = result.ProgramCounter;
                    debugger.TryWait(ref _currentState, ref debugPc, ref gas, ref stack.Head);
                    result = new(debugPc);
                }
#endif
                Instruction instruction = Unsafe.Add(ref code, (nuint)result.Value);

                // IsCancelled is an interface call; per-opcode polling is measurable, and 1024 still aborts in microseconds.
                if (TCancelable.IsActive && (opCodeCount & CancellationCheckMask) == 0 && _txTracer.IsCancelled)
                    ThrowOperationCanceledException();

                TGasPolicy.OnBeforeInstructionTrace(in gas, result.ProgramCounter, instruction, callDepth);

                if (TTracingInst.IsActive)
                    StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), result.ProgramCounter, in stack);

                opCodeCount++;
                int nextPc = (int)(result.Value + 1);
                // The switch pays off only on the cancelable (eth_call) path, where hot contracts stay in
                // I-cache; block processing's diverse mix regresses against the table. TCancelable folds.
                if (TCancelable.IsActive)
                {
                    switch (instruction)
                    {
                        case Instruction.ADD:
                            result = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpAdd, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.SUB:
                            result = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpSub, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.MUL:
                            result = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpMul, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.LT:
                            result = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpLt, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.GT:
                            result = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpGt, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.EQ:
                            result = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseEq>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.ISZERO:
                            result = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpIsZero>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.AND:
                            result = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseAnd>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.OR:
                            result = EvmInstructions.InstructionBitwise<TGasPolicy, EvmInstructions.OpBitwiseOr>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.NOT:
                            result = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpNot>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.SHL:
                            if (!TShift.IsActive) goto default;
                            result = EvmInstructions.InstructionShift<TGasPolicy, EvmInstructions.OpShl, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.SHR:
                            if (!TShift.IsActive) goto default;
                            result = EvmInstructions.InstructionShift<TGasPolicy, EvmInstructions.OpShr, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.CALLDATALOAD:
                            result = EvmInstructions.InstructionCallDataLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.MLOAD:
                            result = EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.MSTORE:
                            result = EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.SLOAD:
                            result = EvmInstructions.InstructionSLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.JUMP:
                            result = TTracingInst.IsActive
                                ? EvmInstructions.InstructionJump(this, ref stack, ref gas, nextPc)
                                : EvmInstructions.InstructionJumpAndSkipJumpDest(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.JUMPI:
                            result = TTracingInst.IsActive
                                ? EvmInstructions.InstructionJumpIf(this, ref stack, ref gas, nextPc)
                                : EvmInstructions.InstructionJumpIfAndSkipJumpDest(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.JUMPDEST:
                            result = EvmInstructions.InstructionJumpDest(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.POP:
                            result = EvmInstructions.InstructionPop(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.PUSH0:
                            if (!TPush0.IsActive) goto default;
                            result = EvmInstructions.InstructionPush0<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.PUSH1:
                            result = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.PUSH2:
                            result = EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.PUSH3:
                            result = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.PUSH4:
                            result = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.DUP1:
                            result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.DUP2:
                            result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.DUP3:
                            result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.DUP4:
                            result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op4, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.DUP5:
                            result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.SWAP1:
                            result = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.SWAP2:
                            result = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        case Instruction.SWAP3:
                            result = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, nextPc);
                            break;
                        default:
                            result = opcodeMethods[(int)instruction](this, ref stack, ref gas, nextPc);
                            break;
                    }
                }
                else
                {
                    // Master-parity table dispatch, with POP inline as the one measurably-hot special case.
                    if (Instruction.POP == instruction)
                        result = EvmInstructions.InstructionPop(this, ref stack, ref gas, nextPc);
                    else
                        result = opcodeMethods[(int)instruction](this, ref stack, ref gas, nextPc);
                }
                if (TGasPolicy.IsOutOfGas(in gas))
                {
                    OpCodeCount += opCodeCount;
                    TGasPolicy.SetOutOfGas(ref gas);
                    programCounter = result.ProgramCounter;
                    return EvmExceptionType.OutOfGas;
                }

                TGasPolicy.OnAfterInstructionTrace(in gas);

                if (TTracingInst.IsActive && result.Exception == EvmExceptionType.None)
                    EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

                // Only the 0xF0+ family sets ReturnData, so the cheap majority skips the field load.
                if (instruction >= Instruction.CREATE && ReturnData is not null)
                {
                    break;
                }
            }

            OpCodeCount += opCodeCount;
            programCounter = result.ProgramCounter;
            return result.Exception;
        }
    }
}
