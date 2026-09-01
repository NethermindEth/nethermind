// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Evm.GasPolicy;
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

                // The guest has no I-cache, so inlining the hot handlers always wins; it never takes the table
                // path. Narrow and frequency-ordered (~78% of executed opcodes): a wide switch exhausts the
                // inline budget and degrades every case to a call.
                switch (instruction)
                {
                    case Instruction.PUSH1:
                        result = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.PUSH2:
                        result = EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.ADD:
                        result = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpAdd, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.SWAP1:
                        result = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.DUP2:
                        result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.MSTORE:
                        result = EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.MLOAD:
                        result = EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.DUP1:
                        result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.POP:
                        result = EvmInstructions.InstructionPop(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.DUP3:
                        result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.SWAP2:
                        result = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.ISZERO:
                        result = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpIsZero>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.DUP5:
                        result = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    case Instruction.JUMPDEST:
                        result = EvmInstructions.InstructionJumpDest(this, ref stack, ref gas, nextPc);
                        break;
                    // GAS is hot on the guest but absent from mainline's curated eth_call set.
                    case Instruction.GAS:
                        result = EvmInstructions.InstructionGas<TGasPolicy, TTracingInst>(this, ref stack, ref gas, nextPc);
                        break;
                    default:
                        result = opcodeMethods[(int)instruction](this, ref stack, ref gas, nextPc);
                        break;
                }

                // The inline budget is exhausted here, so these trivial policy calls go out-of-line once per
                // opcode (~5% of guest execution); the typeof folds, letting EthereumGasPolicy read the flag.
                bool outOfGas = typeof(TGasPolicy) == typeof(EthereumGasPolicy)
                    ? Unsafe.As<TGasPolicy, EthereumGasPolicy>(ref gas).OutOfGas
                    : TGasPolicy.IsOutOfGas(in gas);
                // OnAfterInstructionTrace being empty is UNCHECKED - revisit the skip below if it gains a body.
                Debug.Assert(outOfGas == TGasPolicy.IsOutOfGas(in gas),
                    "Out-of-gas fast path diverged from TGasPolicy.IsOutOfGas");
                if (outOfGas)
                {
                    OpCodeCount += opCodeCount;
                    TGasPolicy.SetOutOfGas(ref gas);
                    programCounter = result.ProgramCounter;
                    return EvmExceptionType.OutOfGas;
                }

                if (typeof(TGasPolicy) != typeof(EthereumGasPolicy))
                    TGasPolicy.OnAfterInstructionTrace(in gas);

                // typeof folds where the inliner gives up on the IsActive getter. == OnFlag, not != OffFlag,
                // so a third IFlag would fail closed.
                if (typeof(TTracingInst) == typeof(OnFlag) && result.Exception == EvmExceptionType.None)
                    EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));
                Debug.Assert(typeof(TTracingInst) == typeof(OnFlag) == TTracingInst.IsActive,
                    "Tracing fast path assumes OnFlag/OffFlag are the only dispatch flags");

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
