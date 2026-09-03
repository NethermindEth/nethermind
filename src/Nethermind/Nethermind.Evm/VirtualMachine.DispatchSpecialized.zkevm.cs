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
            nuint codeLength = (nuint)stack.CodeLength;
            // Hoisted: a no-op OnBeforeInstructionTrace would otherwise chase VmState.Env per instruction.
            int callDepth = VmState.Env.CallDepth;
            while ((nuint)pc < codeLength)
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
                    // Labeling every remaining opcode makes the switch dense, so it compiles to a single
                    // jump table instead of the compare tree Roslyn emits for sparse case values -- the
                    // tree costs several taken branches per dispatched opcode.
                    case (Instruction)0x00:
                    case (Instruction)0x02:
                    case (Instruction)0x03:
                    case (Instruction)0x04:
                    case (Instruction)0x05:
                    case (Instruction)0x06:
                    case (Instruction)0x07:
                    case (Instruction)0x08:
                    case (Instruction)0x09:
                    case (Instruction)0x0a:
                    case (Instruction)0x0b:
                    case (Instruction)0x0c:
                    case (Instruction)0x0d:
                    case (Instruction)0x0e:
                    case (Instruction)0x0f:
                    case (Instruction)0x10:
                    case (Instruction)0x11:
                    case (Instruction)0x12:
                    case (Instruction)0x13:
                    case (Instruction)0x14:
                    case (Instruction)0x16:
                    case (Instruction)0x17:
                    case (Instruction)0x18:
                    case (Instruction)0x19:
                    case (Instruction)0x1a:
                    case (Instruction)0x1b:
                    case (Instruction)0x1c:
                    case (Instruction)0x1d:
                    case (Instruction)0x1e:
                    case (Instruction)0x1f:
                    case (Instruction)0x20:
                    case (Instruction)0x21:
                    case (Instruction)0x22:
                    case (Instruction)0x23:
                    case (Instruction)0x24:
                    case (Instruction)0x25:
                    case (Instruction)0x26:
                    case (Instruction)0x27:
                    case (Instruction)0x28:
                    case (Instruction)0x29:
                    case (Instruction)0x2a:
                    case (Instruction)0x2b:
                    case (Instruction)0x2c:
                    case (Instruction)0x2d:
                    case (Instruction)0x2e:
                    case (Instruction)0x2f:
                    case (Instruction)0x30:
                    case (Instruction)0x31:
                    case (Instruction)0x32:
                    case (Instruction)0x33:
                    case (Instruction)0x34:
                    case (Instruction)0x35:
                    case (Instruction)0x36:
                    case (Instruction)0x37:
                    case (Instruction)0x38:
                    case (Instruction)0x39:
                    case (Instruction)0x3a:
                    case (Instruction)0x3b:
                    case (Instruction)0x3c:
                    case (Instruction)0x3d:
                    case (Instruction)0x3e:
                    case (Instruction)0x3f:
                    case (Instruction)0x40:
                    case (Instruction)0x41:
                    case (Instruction)0x42:
                    case (Instruction)0x43:
                    case (Instruction)0x44:
                    case (Instruction)0x45:
                    case (Instruction)0x46:
                    case (Instruction)0x47:
                    case (Instruction)0x48:
                    case (Instruction)0x49:
                    case (Instruction)0x4a:
                    case (Instruction)0x4b:
                    case (Instruction)0x4c:
                    case (Instruction)0x4d:
                    case (Instruction)0x4e:
                    case (Instruction)0x4f:
                    case (Instruction)0x53:
                    case (Instruction)0x54:
                    case (Instruction)0x55:
                    case (Instruction)0x56:
                    case (Instruction)0x57:
                    case (Instruction)0x58:
                    case (Instruction)0x59:
                    case (Instruction)0x5c:
                    case (Instruction)0x5d:
                    case (Instruction)0x5e:
                    case (Instruction)0x5f:
                    case (Instruction)0x62:
                    case (Instruction)0x63:
                    case (Instruction)0x64:
                    case (Instruction)0x65:
                    case (Instruction)0x66:
                    case (Instruction)0x67:
                    case (Instruction)0x68:
                    case (Instruction)0x69:
                    case (Instruction)0x6a:
                    case (Instruction)0x6b:
                    case (Instruction)0x6c:
                    case (Instruction)0x6d:
                    case (Instruction)0x6e:
                    case (Instruction)0x6f:
                    case (Instruction)0x70:
                    case (Instruction)0x71:
                    case (Instruction)0x72:
                    case (Instruction)0x73:
                    case (Instruction)0x74:
                    case (Instruction)0x75:
                    case (Instruction)0x76:
                    case (Instruction)0x77:
                    case (Instruction)0x78:
                    case (Instruction)0x79:
                    case (Instruction)0x7a:
                    case (Instruction)0x7b:
                    case (Instruction)0x7c:
                    case (Instruction)0x7d:
                    case (Instruction)0x7e:
                    case (Instruction)0x7f:
                    case (Instruction)0x83:
                    case (Instruction)0x85:
                    case (Instruction)0x86:
                    case (Instruction)0x87:
                    case (Instruction)0x88:
                    case (Instruction)0x89:
                    case (Instruction)0x8a:
                    case (Instruction)0x8b:
                    case (Instruction)0x8c:
                    case (Instruction)0x8d:
                    case (Instruction)0x8e:
                    case (Instruction)0x8f:
                    case (Instruction)0x92:
                    case (Instruction)0x93:
                    case (Instruction)0x94:
                    case (Instruction)0x95:
                    case (Instruction)0x96:
                    case (Instruction)0x97:
                    case (Instruction)0x98:
                    case (Instruction)0x99:
                    case (Instruction)0x9a:
                    case (Instruction)0x9b:
                    case (Instruction)0x9c:
                    case (Instruction)0x9d:
                    case (Instruction)0x9e:
                    case (Instruction)0x9f:
                    case (Instruction)0xa0:
                    case (Instruction)0xa1:
                    case (Instruction)0xa2:
                    case (Instruction)0xa3:
                    case (Instruction)0xa4:
                    case (Instruction)0xa5:
                    case (Instruction)0xa6:
                    case (Instruction)0xa7:
                    case (Instruction)0xa8:
                    case (Instruction)0xa9:
                    case (Instruction)0xaa:
                    case (Instruction)0xab:
                    case (Instruction)0xac:
                    case (Instruction)0xad:
                    case (Instruction)0xae:
                    case (Instruction)0xaf:
                    case (Instruction)0xb0:
                    case (Instruction)0xb1:
                    case (Instruction)0xb2:
                    case (Instruction)0xb3:
                    case (Instruction)0xb4:
                    case (Instruction)0xb5:
                    case (Instruction)0xb6:
                    case (Instruction)0xb7:
                    case (Instruction)0xb8:
                    case (Instruction)0xb9:
                    case (Instruction)0xba:
                    case (Instruction)0xbb:
                    case (Instruction)0xbc:
                    case (Instruction)0xbd:
                    case (Instruction)0xbe:
                    case (Instruction)0xbf:
                    case (Instruction)0xc0:
                    case (Instruction)0xc1:
                    case (Instruction)0xc2:
                    case (Instruction)0xc3:
                    case (Instruction)0xc4:
                    case (Instruction)0xc5:
                    case (Instruction)0xc6:
                    case (Instruction)0xc7:
                    case (Instruction)0xc8:
                    case (Instruction)0xc9:
                    case (Instruction)0xca:
                    case (Instruction)0xcb:
                    case (Instruction)0xcc:
                    case (Instruction)0xcd:
                    case (Instruction)0xce:
                    case (Instruction)0xcf:
                    case (Instruction)0xd0:
                    case (Instruction)0xd1:
                    case (Instruction)0xd2:
                    case (Instruction)0xd3:
                    case (Instruction)0xd4:
                    case (Instruction)0xd5:
                    case (Instruction)0xd6:
                    case (Instruction)0xd7:
                    case (Instruction)0xd8:
                    case (Instruction)0xd9:
                    case (Instruction)0xda:
                    case (Instruction)0xdb:
                    case (Instruction)0xdc:
                    case (Instruction)0xdd:
                    case (Instruction)0xde:
                    case (Instruction)0xdf:
                    case (Instruction)0xe0:
                    case (Instruction)0xe1:
                    case (Instruction)0xe2:
                    case (Instruction)0xe3:
                    case (Instruction)0xe4:
                    case (Instruction)0xe5:
                    case (Instruction)0xe6:
                    case (Instruction)0xe7:
                    case (Instruction)0xe8:
                    case (Instruction)0xe9:
                    case (Instruction)0xea:
                    case (Instruction)0xeb:
                    case (Instruction)0xec:
                    case (Instruction)0xed:
                    case (Instruction)0xee:
                    case (Instruction)0xef:
                    case (Instruction)0xf0:
                    case (Instruction)0xf1:
                    case (Instruction)0xf2:
                    case (Instruction)0xf3:
                    case (Instruction)0xf4:
                    case (Instruction)0xf5:
                    case (Instruction)0xf6:
                    case (Instruction)0xf7:
                    case (Instruction)0xf8:
                    case (Instruction)0xf9:
                    case (Instruction)0xfa:
                    case (Instruction)0xfb:
                    case (Instruction)0xfc:
                    case (Instruction)0xfd:
                    case (Instruction)0xfe:
                    case (Instruction)0xff:
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
