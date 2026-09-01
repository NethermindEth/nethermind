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
        ref nint programCounter)
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
        nint pc = programCounter;
        // Pinned pointer drops the per-dispatch bounds check (opcode is a byte, always in range).
        delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref nint, EvmExceptionType>[] opcodeArray = _opcodeMethods;
        fixed (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref nint, EvmExceptionType>* opcodeMethods = &opcodeArray[0])
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

                TGasPolicy.OnBeforeInstructionTrace(in gas, (int)pc, instruction, callDepth);

                if (TTracingInst.IsActive)
                    StartInstructionTrace(instruction, TGasPolicy.GetRemainingGas(in gas), (int)pc, in stack);

                pc++;
                opCodeCount++;

                // Temp by ref: ref pc handed to the handlers would address-take it and spill it every opcode.
                nint opPc = pc;
                // The guest has no I-cache, so inlining the hot handlers always wins; it never takes the table
                // path. Narrow and frequency-ordered (~78% of executed opcodes): a wide switch exhausts the
                // inline budget and degrades every case to a call.
                switch (instruction)
                {
                    case Instruction.PUSH1:
                        exceptionType = EvmInstructions.InstructionPush<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.PUSH2:
                        exceptionType = EvmInstructions.InstructionPush2<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.ADD:
                        exceptionType = EvmInstructions.InstructionMath2Param<TGasPolicy, EvmInstructions.OpAdd, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.SWAP1:
                        exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.DUP2:
                        exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.MSTORE:
                        exceptionType = EvmInstructions.InstructionMStore<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.MLOAD:
                        exceptionType = EvmInstructions.InstructionMLoad<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.DUP1:
                        exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op1, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.POP:
                        exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.DUP3:
                        exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op3, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.SWAP2:
                        exceptionType = EvmInstructions.InstructionSwap<TGasPolicy, EvmInstructions.Op2, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.ISZERO:
                        exceptionType = EvmInstructions.InstructionMath1Param<TGasPolicy, EvmInstructions.OpIsZero>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.DUP5:
                        exceptionType = EvmInstructions.InstructionDup<TGasPolicy, EvmInstructions.Op5, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    case Instruction.JUMPDEST:
                        exceptionType = EvmInstructions.InstructionJumpDest(this, ref stack, ref gas, ref opPc);
                        break;
                    // GAS is hot on the guest but absent from mainline's curated eth_call set.
                    case Instruction.GAS:
                        exceptionType = EvmInstructions.InstructionGas<TGasPolicy, TTracingInst>(this, ref stack, ref gas, ref opPc);
                        break;
                    default:
                        exceptionType = opcodeMethods[(int)instruction](this, ref stack, ref gas, ref opPc);
                        break;
                }
                pc = opPc;

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
                    exceptionType = EvmExceptionType.OutOfGas;
                    goto Halted;
                }

                if (typeof(TGasPolicy) != typeof(EthereumGasPolicy))
                    TGasPolicy.OnAfterInstructionTrace(in gas);

                if (exceptionType != EvmExceptionType.None)
                    break;

                // typeof folds where the inliner gives up on the IsActive getter. == OnFlag, not != OffFlag,
                // so a third IFlag would fail closed.
                if (typeof(TTracingInst) == typeof(OnFlag))
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
        }

    Halted:
        programCounter = pc;
        return exceptionType;
    }
}
