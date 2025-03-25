// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

using System.Reflection;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;
using Sigil;
using Nethermind.Int256;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State;
using Nethermind.Evm.Tracing;
using Nethermind.Core.Specs;

using static Nethermind.Evm.CodeAnalysis.IL.WordEmit;
using static Nethermind.Evm.CodeAnalysis.IL.UnsafeEmit;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using Newtonsoft.Json.Linq;

namespace Nethermind.Evm.CodeAnalysis.IL.CompilerModes;

internal static class AotOpcodeEmitter<TDelegateType>
{
    internal static OpcodeILEmitterDelegate<TDelegateType> emptyEmitter = (codeInfo, ilCompilerConfig, contractMetadata, currentSubSegment, opcodeIndex, instruction, opcodeMetadata, method, localVariables, envStateLoader, exceptions, exitLabels) => { };
    public static OpcodeILEmitterDelegate<TDelegateType> GetOpcodeILEmitter(Instruction instruction)
    {
        switch (instruction)
        {
            case Instruction.JUMPDEST:
                return emptyEmitter;
                
            case Instruction.JUMP:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                    method.StoreLocal(locals.wordRef256A);

                    if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                    {
                        UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, pc, currentSubSegment);
                    }
                    else
                    {
                        UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, opcodeMetadata);
                        EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                    }
                    method.FakeBranch(escapeLabels.jumpTable);
                };
                
            case Instruction.JUMPI:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    Label noJump = method.DefineLabel();
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                    method.EmitCheck(nameof(Word.IsZero));
                    // if the jump condition is false, we do not jump
                    method.BranchIfTrue(noJump);

                    // we jump into the jump table

                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                    method.StoreLocal(locals.wordRef256A);

                    if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                    {
                        UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, pc, currentSubSegment);
                    }
                    else
                    {
                        UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, opcodeMetadata);
                        EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                    }
                    method.Branch(escapeLabels.jumpTable);

                    method.MarkLabel(noJump);
                };
                
            case Instruction.POP:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    // do nothing
                };
                
            case Instruction.STOP:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        envLoader.LoadResult(method, locals, true);
                        method.LoadConstant((int)ContractState.Finished);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                        method.FakeBranch(escapeLabels.returnLabel);
                    };
                }
                
            case Instruction.CHAINID:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadChainId(method, locals, false);
                        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.NOT:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
                        MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
                        MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
                        MethodInfo notVector256Method = typeof(Vector256)
                            .GetMethod(nameof(Vector256.OnesComplement), BindingFlags.Public | BindingFlags.Static)!
                            .MakeGenericMethod(typeof(byte));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(refWordToRefByteMethod);
                        method.Duplicate();
                        method.Call(readVector256Method);
                        method.Call(notVector256Method);
                        method.Call(writeVector256Method);
                    };
                }
                
            case Instruction.PUSH0:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                    };
                }
                
            case Instruction.PUSH1:
            case Instruction.PUSH2:
            case Instruction.PUSH3:
            case Instruction.PUSH4:
            case Instruction.PUSH5:
            case Instruction.PUSH6:
            case Instruction.PUSH7:
            case Instruction.PUSH8:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels) =>
                    {
                        int length = Math.Min(codeInfo.MachineCode.Length - pc - 1, opcode - Instruction.PUSH0);
                        var immediateBytes = codeInfo.MachineCode.Slice(pc + 1, length).Span;
                        if (immediateBytes.IsZero())
                            method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        else
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                            method.SpecialPushOpcode(instruction, immediateBytes);
                        }
                    };
                }
                
            case Instruction.PUSH9:
            case Instruction.PUSH10:
            case Instruction.PUSH11:
            case Instruction.PUSH12:
            case Instruction.PUSH13:
            case Instruction.PUSH14:
            case Instruction.PUSH15:
            case Instruction.PUSH16:
            case Instruction.PUSH17:
            case Instruction.PUSH18:
            case Instruction.PUSH19:
            case Instruction.PUSH20:
            case Instruction.PUSH21:
            case Instruction.PUSH22:
            case Instruction.PUSH23:
            case Instruction.PUSH24:
            case Instruction.PUSH25:
            case Instruction.PUSH26:
            case Instruction.PUSH27:
            case Instruction.PUSH28:
            case Instruction.PUSH29:
            case Instruction.PUSH30:
            case Instruction.PUSH31:
            case Instruction.PUSH32:
                {// we load the locals.stackHeadRef
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        int length = Math.Min(codeInfo.MachineCode.Length - pc - 1, opcode - Instruction.PUSH0);
                        var immediateBytes = codeInfo.MachineCode.Slice(pc + 1, length).Span;
                        if (immediateBytes.IsZero())
                            method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        else
                        {
                            if(length != 32)
                            {
                                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                                method.Call(GetAsMethodInfo<Word, byte>());
                                method.LoadConstant(32 - length);
                                method.Convert<nint>();
                                method.Call(GetAddBytesOffsetRef<byte>());
                            } else
                            {
                                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                                method.Call(GetAsMethodInfo<Word, byte>());
                            }
                            envLoader.LoadMachineCode(method, locals, true);
                            method.LoadItemFromSpan<TDelegateType, byte>(pc + 1, true);
                            method.LoadConstant(length);
                            method.CopyBlock();
                        }
                    };
                }
                
            case Instruction.ADD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                    };
                }
                
            case Instruction.SUB:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label pushNegItemB = method.DefineLabel();
                        Label pushItemA = method.DefineLabel();
                        // b - a a::b
                        Label fallbackToUInt256Call = method.DefineLabel();
                        Label endofOpcode = method.DefineLabel();
                        // we the two uint256 from the locals.stackHeadRef
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.StoreLocal(locals.wordRef256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.StoreLocal(locals.wordRef256B);

                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
                        method.BranchIfTrue(pushItemA);

                        EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        method.Branch(endofOpcode);

                        method.MarkLabel(pushItemA);
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.LoadLocal(locals.wordRef256A);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));

                        method.MarkLabel(endofOpcode);
                    };
                }
                
            case Instruction.MUL:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label push0Zero = method.DefineLabel();
                        Label pushItemA = method.DefineLabel();
                        Label pushItemB = method.DefineLabel();
                        Label endofOpcode = method.DefineLabel();
                        // we the two uint256 from the locals.stackHeadRef
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.StoreLocal(locals.wordRef256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.StoreLocal(locals.wordRef256B);

                        method.LoadLocal(locals.wordRef256A);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.LoadLocal(locals.wordRef256B);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);

                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256A);
                        method.BranchIfTrue(push0Zero);

                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
                        method.BranchIfTrue(endofOpcode);

                        method.EmitCheck(nameof(Word.IsOne), locals.wordRef256A);
                        method.BranchIfTrue(endofOpcode);

                        method.EmitCheck(nameof(Word.IsOne), locals.wordRef256B);
                        method.BranchIfTrue(pushItemA);

                        EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        method.Branch(endofOpcode);

                        method.MarkLabel(push0Zero);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Branch(endofOpcode);

                        method.MarkLabel(pushItemA);
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.LoadLocal(locals.wordRef256A);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));

                        method.MarkLabel(endofOpcode);
                    };
                }
                
            case Instruction.MOD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label pushZeroLabel = method.DefineLabel();
                        Label fallBackToOldBehavior = method.DefineLabel();
                        Label endofOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.StoreLocal(locals.wordRef256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.StoreLocal(locals.wordRef256B);

                        method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256B);
                        method.BranchIfTrue(pushZeroLabel);

                        EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        method.Branch(endofOpcode);

                        method.MarkLabel(pushZeroLabel);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.MarkLabel(endofOpcode);
                    };

                }
                
            case Instruction.SMOD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label bIsOneOrZero = method.DefineLabel();
                        Label endofOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.StoreLocal(locals.wordRef256B);

                        // if b is 1 or 0 result is always 0
                        method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256B);
                        method.BranchIfTrue(bIsOneOrZero);

                        EmitBinaryInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Mod), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        method.Branch(endofOpcode);

                        method.MarkLabel(bIsOneOrZero);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.MarkLabel(endofOpcode);
                    };

                }
                
            case Instruction.DIV:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label fallBackToOldBehavior = method.DefineLabel();
                        Label pushZeroLabel = method.DefineLabel();
                        Label pushALabel = method.DefineLabel();
                        Label endofOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.StoreLocal(locals.wordRef256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.StoreLocal(locals.wordRef256B);

                        // if a or b are 0 result is directly 0
                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
                        method.BranchIfTrue(pushZeroLabel);
                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256A);
                        method.BranchIfTrue(pushZeroLabel);

                        // if b is 1 result is by default a
                        method.EmitCheck(nameof(Word.IsOne), locals.wordRef256B);
                        method.BranchIfTrue(pushALabel);

                        EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        method.Branch(endofOpcode);

                        method.MarkLabel(pushZeroLabel);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Branch(endofOpcode);

                        method.MarkLabel(pushALabel);
                        method.LoadLocal(locals.wordRef256B);
                        method.LoadLocal(locals.wordRef256A);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));
                        method.Branch(endofOpcode);

                        method.MarkLabel(endofOpcode);
                    };

                }
                
            case Instruction.SDIV:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label fallBackToOldBehavior = method.DefineLabel();
                        Label pushZeroLabel = method.DefineLabel();
                        Label pushALabel = method.DefineLabel();
                        Label endofOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.StoreLocal(locals.wordRef256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.StoreLocal(locals.wordRef256B);


                        // if b is 0 or a is 0 then the result is 0
                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
                        method.BranchIfTrue(pushZeroLabel);
                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256A);
                        method.BranchIfTrue(pushZeroLabel);

                        // if b is 1 in all cases the result is a
                        method.EmitCheck(nameof(Word.IsOne), locals.wordRef256B);
                        method.BranchIfTrue(pushALabel);

                        // if b is -1 and a is 2^255 then the result is 2^255
                        method.EmitCheck(nameof(Word.IsMinusOne), locals.wordRef256B);
                        method.BranchIfFalse(fallBackToOldBehavior);

                        method.EmitCheck(nameof(Word.IsP255), locals.wordRef256A);
                        method.BranchIfTrue(pushALabel);

                        method.MarkLabel(fallBackToOldBehavior);
                        EmitBinaryInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Divide), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                        method.Branch(endofOpcode);

                        method.MarkLabel(pushZeroLabel);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Branch(endofOpcode);

                        method.MarkLabel(pushALabel);
                        method.LoadLocal(locals.wordRef256B);
                        method.LoadLocal(locals.wordRef256A);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));
                        method.Branch(endofOpcode);

                        method.MarkLabel(endofOpcode);

                    };

                }
                
            case Instruction.ADDMOD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label push0Zero = method.DefineLabel();
                        Label fallbackToUInt256Call = method.DefineLabel();
                        Label endofOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.StoreLocal(locals.wordRef256C);

                        // if c is 1 or 0 result is 0
                        method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256C);
                        method.BranchIfFalse(fallbackToUInt256Call);

                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.Branch(endofOpcode);

                        method.MarkLabel(fallbackToUInt256Call);
                        EmitTrinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.AddMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.uint256C);
                        method.MarkLabel(endofOpcode);
                    };

                }
                
            case Instruction.MULMOD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label push0Zero = method.DefineLabel();
                        Label fallbackToUInt256Call = method.DefineLabel();
                        Label endofOpcode = method.DefineLabel();
                        // we the two uint256 from the locals.stackHeadRef
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.StoreLocal(locals.wordRef256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.StoreLocal(locals.wordRef256B);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.StoreLocal(locals.wordRef256C);

                        // since (a * b) % c 
                        // if a or b are 0 then the result is 0
                        // if c is 0 or 1 then the result is 0
                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256A);
                        method.BranchIfTrue(push0Zero);
                        method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
                        method.BranchIfTrue(push0Zero);
                        method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256C);
                        method.BranchIfTrue(push0Zero);

                        // since (a * b) % c == (a % c * b % c) % c
                        // if a or b are equal to c, then the result is 0
                        method.LoadLocal(locals.wordRef256A);
                        method.LoadLocal(locals.wordRef256C);
                        method.Call(Word.AreEqual);
                        method.BranchIfTrue(push0Zero);
                        method.LoadLocal(locals.wordRef256B);
                        method.LoadLocal(locals.wordRef256C);
                        method.Call(Word.AreEqual);
                        method.BranchIfTrue(push0Zero);

                        method.MarkLabel(fallbackToUInt256Call);
                        EmitTrinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.MultiplyMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.uint256C);
                        method.Branch(endofOpcode);

                        method.MarkLabel(push0Zero);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.Branch(endofOpcode);

                        method.MarkLabel(endofOpcode);
                    };

                }
                
            case Instruction.SHL:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    EmitShiftUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), isLeft: true, evmExceptionLabels, locals.uint256A, locals.uint256B);
                };

                
            case Instruction.SHR:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    EmitShiftUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), isLeft: false, evmExceptionLabels, locals.uint256A, locals.uint256B);
                };

                
            case Instruction.SAR:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    EmitShiftInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), evmExceptionLabels, locals.uint256A, locals.uint256B);
                };

                
            case Instruction.AND:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseAnd), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                };

                
            case Instruction.OR:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseOr), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                };

                
            case Instruction.XOR:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.Xor), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                };

                
            case Instruction.EXP:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label powerIsZero = method.DefineLabel();
                        Label baseIsOneOrZero = method.DefineLabel();
                        Label endOfExpImpl = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Duplicate();
                        method.Call(Word.LeadingZeroProp);
                        method.StoreLocal(locals.uint64A);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);

                        method.LoadLocalAddress(locals.uint256B);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(powerIsZero);

                        // load spec
                        method.LoadLocal(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExpByteCost)));
                        method.LoadConstant((long)32);
                        method.LoadLocal(locals.uint64A);
                        method.Subtract();
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.LoadLocalAddress(locals.uint256A);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZeroOrOne)).GetMethod!);
                        method.BranchIfTrue(baseIsOneOrZero);

                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256B);
                        method.LoadLocalAddress(locals.uint256R);
                        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.LoadLocal(locals.uint256R);
                        method.Call(Word.SetUInt256);

                        method.Branch(endOfExpImpl);

                        method.MarkLabel(powerIsZero);
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.LoadConstant(1);
                        method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                        method.Branch(endOfExpImpl);

                        method.MarkLabel(baseIsOneOrZero);
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.LoadLocal(locals.uint256A);
                        method.Call(Word.SetUInt256);
                        method.Branch(endOfExpImpl);

                        method.MarkLabel(endOfExpImpl);
                    };
                }
                
            case Instruction.LT:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        EmitComparaisonUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, locals.uint256A, locals.uint256B);
                    };
                }

                
            case Instruction.GT:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        EmitComparaisonUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, locals.uint256A, locals.uint256B);
                    };
                }
                
            case Instruction.SLT:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    EmitComparaisonInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), false, evmExceptionLabels, locals.uint256A, locals.uint256B);
                };

                
            case Instruction.SGT:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    EmitComparaisonInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), true, evmExceptionLabels, locals.uint256A, locals.uint256B);
                };

                
            case Instruction.EQ:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
                        MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
                        MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
                        MethodInfo operationUnegenerified = typeof(Vector256).GetMethod(nameof(Vector256.EqualsAll), BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(byte));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(refWordToRefByteMethod);
                        method.Call(readVector256Method);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(refWordToRefByteMethod);
                        method.Call(readVector256Method);

                        method.Call(operationUnegenerified);
                        method.StoreLocal(locals.lbool);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.LoadLocal(locals.lbool);
                        method.Convert<uint>();
                        method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);

                    };
                }
                
            case Instruction.ISZERO:
                {// we load the locals.stackHeadRef
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Duplicate();
                        method.Duplicate();
                        method.EmitCheck(nameof(Word.IsZero));
                        method.StoreLocal(locals.lbool);
                        method.Call(Word.SetToZero);
                        method.LoadLocal(locals.lbool);
                        method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.DUP1:
            case Instruction.DUP2:
            case Instruction.DUP3:
            case Instruction.DUP4:
            case Instruction.DUP5:
            case Instruction.DUP6:
            case Instruction.DUP7:
            case Instruction.DUP8:
            case Instruction.DUP9:
            case Instruction.DUP10:
            case Instruction.DUP11:
            case Instruction.DUP12:
            case Instruction.DUP13:
            case Instruction.DUP14:
            case Instruction.DUP15:
            case Instruction.DUP16:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        var count = (int)opcode - (int)Instruction.DUP1 + 1;
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), count);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));
                    };
                }
                
            case Instruction.SWAP1:
            case Instruction.SWAP2:
            case Instruction.SWAP3:
            case Instruction.SWAP4:
            case Instruction.SWAP5:
            case Instruction.SWAP6:
            case Instruction.SWAP7:
            case Instruction.SWAP8:
            case Instruction.SWAP9:
            case Instruction.SWAP10:
            case Instruction.SWAP11:
            case Instruction.SWAP12:
            case Instruction.SWAP13:
            case Instruction.SWAP14:
            case Instruction.SWAP15:
            case Instruction.SWAP16:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        var count = (int)opcode - (int)Instruction.SWAP1 + 1;

                        method.LoadLocalAddress(locals.uint256R);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), count + 1);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), count + 1);
                        method.LoadLocalAddress(locals.uint256R);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));
                    };
                }
                
            case Instruction.CODESIZE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        method.LoadConstant(codeInfo.MachineCode.Length);
                        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.PC:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        method.LoadConstant(pc);
                        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.COINBASE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadBlockContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
                        method.Call(Word.SetAddress);
                    };
                }
                
            case Instruction.TIMESTAMP:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadBlockContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
                        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.NUMBER:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadBlockContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
                        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.GASLIMIT:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadBlockContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasLimit), false, out _));
                        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.CALLER:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadEnv(method, locals, false);

                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
                        method.Call(Word.SetAddress);
                    };
                }
                
            case Instruction.ADDRESS:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadEnv(method, locals, false);

                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.Call(Word.SetAddress);
                    };
                }
                
            case Instruction.ORIGIN:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadTxContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin), false, out _));
                        method.Call(Word.SetAddress);
                    };
                }
                
            case Instruction.CALLVALUE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadEnv(method, locals, false);

                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                        method.Call(Word.SetUInt256);
                    };
                }
                
            case Instruction.GASPRICE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadTxContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice), false, out _));
                        method.Call(Word.SetUInt256);
                    };
                }
                
            case Instruction.CALLDATACOPY:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label endOfOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256C);

                        method.LoadLocal(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256C);
                        method.LoadLocalAddress(locals.lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Memory);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(endOfOpcode);

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadCalldata(method, locals, false);
                        method.LoadLocalAddress(locals.uint256B);
                        method.LoadLocal(locals.uint256C);
                        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                        method.Convert<int>();
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.StoreLocal(locals.localZeroPaddedSpan);

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.localZeroPaddedSpan);
                        method.CallVirtual(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.CALLDATALOAD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

                        envLoader.LoadCalldata(method, locals, false);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadConstant(Word.Size);
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.Call(Word.SetZeroPaddedSpan);
                    };
                }
                
            case Instruction.CALLDATASIZE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadCalldata(method, locals, true);
                        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.MSIZE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);

                        envLoader.LoadMemory(method, locals, true);
                        method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
                        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.MSTORE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.StoreLocal(locals.wordRef256B);

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadConstant(Word.Size);
                        method.Call(ConvertionExplicit<UInt256, int>());
                        method.StoreLocal(locals.uint256C);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocal(locals.wordRef256B);
                        method.Call(Word.GetMutableSpan);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
                    };
                }
                
            case Instruction.MSTORE8:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.CallGetter(Word.GetByte0, BitConverter.IsLittleEndian);
                        method.StoreLocal(locals.byte8A);

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadConstant(1);
                        method.Call(ConvertionExplicit<UInt256, int>());
                        method.StoreLocal(locals.uint256C);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocal(locals.byte8A);

                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
                    };
                }
                
            case Instruction.MLOAD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);

                        using Local bigInt32 = method.DeclareLocal(typeof(UInt256));
                        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BigInt32)));
                        method.StoreLocal(bigInt32);

                        method.LoadLocalAddress(bigInt32);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
                        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                        method.StoreLocal(locals.localReadonOnlySpan);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.LoadLocal(locals.localReadonOnlySpan);
                        method.Call(Word.SetReadOnlySpan);
                    };
                }
                
            case Instruction.MCOPY:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256C);

                        method.LoadLocal(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256C);
                        method.LoadLocalAddress(locals.lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.VeryLow);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256B);
                        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Max)));
                        method.StoreLocal(locals.uint256R);
                        method.LoadLocalAddress(locals.uint256R);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256B);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
                    };
                }
                
            case Instruction.KECCAK256:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        MethodInfo refWordToRefValueHashMethod = UnsafeEmit.GetAsMethodInfo<Word, ValueHash256>();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);

                        method.LoadLocal(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256B);
                        method.LoadLocalAddress(locals.lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Sha3Word);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256B);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256B);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(refWordToRefValueHashMethod);
                        method.Call(typeof(KeccakCache).GetMethod(nameof(KeccakCache.ComputeTo), [typeof(ReadOnlySpan<byte>), typeof(ValueHash256).MakeByRefType()]));
                    };
                }
                
            case Instruction.BYTE:
                {// load a
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Duplicate();
                        method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                        method.StoreLocal(locals.uint32A);
                        method.StoreLocal(locals.wordRef256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetReadOnlySpan);
                        method.StoreLocal(locals.localReadonOnlySpan);


                        Label pushZeroLabel = method.DefineLabel();
                        Label endOfInstructionImpl = method.DefineLabel();
                        method.EmitCheck(nameof(Word.IsShort), locals.wordRef256A);
                        method.BranchIfFalse(pushZeroLabel);
                        method.LoadLocal(locals.wordRef256A);
                        method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
                        method.LoadConstant(Word.Size);
                        method.BranchIfGreaterOrEqual(pushZeroLabel);
                        method.LoadLocal(locals.wordRef256A);
                        method.CallGetter(Word.GetInt0, BitConverter.IsLittleEndian);
                        method.LoadConstant(0);
                        method.BranchIfLess(pushZeroLabel);

                        method.LoadLocalAddress(locals.localReadonOnlySpan);
                        method.LoadLocal(locals.uint32A);
                        method.Call(typeof(ReadOnlySpan<byte>).GetMethod("get_Item"));
                        method.LoadIndirect<byte>();
                        method.Convert<uint>();
                        method.StoreLocal(locals.uint32A);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.LoadLocal(locals.uint32A);
                        method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                        method.Branch(endOfInstructionImpl);

                        method.MarkLabel(pushZeroLabel);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.MarkLabel(endOfInstructionImpl);
                    };
                }
                
            case Instruction.CODECOPY:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label endOfOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256C);

                        method.LoadLocal(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256C);
                        method.LoadLocalAddress(locals.lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Memory);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);

                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(endOfOpcode);

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadMachineCode(method, locals, false);
                        method.LoadLocalAddress(locals.uint256B);
                        method.LoadLocalAddress(locals.uint256C);
                        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                        method.Convert<int>();
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlySpan<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.StoreLocal(locals.localZeroPaddedSpan);

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.localZeroPaddedSpan);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.GAS:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        method.LoadLocal(locals.gasAvailable);
                        method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.RETURNDATASIZE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadReturnDataBuffer(method, locals, true);
                        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.RETURNDATACOPY:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label endOfOpcode = method.DefineLabel();
                        using Local tempResult = method.DeclareLocal(typeof(UInt256));


                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256C);

                        method.LoadLocalAddress(locals.uint256B);
                        method.LoadLocalAddress(locals.uint256C);
                        method.LoadLocalAddress(tempResult);
                        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.AddOverflow)));
                        method.LoadLocalAddress(tempResult);

                        envLoader.LoadReturnDataBuffer(method, locals, true);
                        method.Call(typeof(ReadOnlyMemory<byte>).GetProperty(nameof(ReadOnlyMemory<byte>.Length)).GetMethod!);
                        method.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                        method.Or();
                        method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.AccessViolation));

                        method.LoadLocal(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256C);
                        method.LoadLocalAddress(locals.lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Memory);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        // Note : check if c + b > returnData.Size

                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(endOfOpcode);

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadReturnDataBuffer(method, locals, false);
                        method.LoadLocalAddress(locals.uint256B);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(MethodInfo<UInt256>("op_Explicit", typeof(int), new[] { typeof(UInt256).MakeByRefType() }));
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.StoreLocal(locals.localZeroPaddedSpan);

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.localZeroPaddedSpan);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.RETURN or Instruction.REVERT:
                {
                    return (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256B);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadReturnDataBuffer(method, locals, true);
                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256B);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Load), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                        method.StoreObject<ReadOnlyMemory<byte>>();

                        envLoader.LoadResult(method, locals, true);
                        switch (instruction)
                        {
                            case Instruction.REVERT:
                                method.LoadConstant((int)ContractState.Revert);
                                break;
                            case Instruction.RETURN:
                                method.LoadConstant((int)ContractState.Return);
                                break;
                        }
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                        method.FakeBranch(escapeLabels.returnLabel);
                    };
                }
                
            case Instruction.BASEFEE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadBlockContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                        method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.BaseFeePerGas), false, out _));
                        method.Call(Word.SetUInt256);
                    };
                }
                
            case Instruction.BLOBBASEFEE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        using Local uint256Nullable = method.DeclareLocal(typeof(UInt256?));
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadBlockContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.BlobBaseFee), false, out _));
                        method.StoreLocal(uint256Nullable);
                        method.LoadLocalAddress(uint256Nullable);
                        method.Call(GetPropertyInfo(typeof(UInt256?), nameof(Nullable<UInt256>.Value), false, out _));
                        method.Call(Word.SetUInt256);
                    };
                }
                
            case Instruction.PREVRANDAO:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label isPostMergeBranch = method.DefineLabel();
                        Label endOfOpcode = method.DefineLabel();
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);

                        envLoader.LoadBlockContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                        method.Duplicate();
                        method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.IsPostMerge), false, out _));
                        method.BranchIfFalse(isPostMergeBranch);
                        method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.Random), false, out _));
                        method.LoadField(typeof(Hash256).GetField("_hash256", BindingFlags.Instance | BindingFlags.NonPublic));
                        method.Call(Word.SetKeccak);
                        method.Branch(endOfOpcode);

                        method.MarkLabel(isPostMergeBranch);
                        method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.Difficulty), false, out _));
                        method.Call(Word.SetUInt256);

                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.BLOBHASH:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label blobVersionedHashNotFound = method.DefineLabel();
                        Label indexTooLarge = method.DefineLabel();
                        Label endOfOpcode = method.DefineLabel();
                        using Local byteMatrix = method.DeclareLocal(typeof(byte[][]));
                        envLoader.LoadTxContext(method, locals, true);
                        method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                        method.StoreLocal(byteMatrix);

                        method.LoadLocal(byteMatrix);
                        method.LoadNull();
                        method.BranchIfEqual(blobVersionedHashNotFound);

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocal(byteMatrix);
                        method.Call(GetPropertyInfo(typeof(byte[][]), nameof(Array.Length), false, out _));
                        method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                        method.BranchIfFalse(indexTooLarge);

                        method.LoadLocal(byteMatrix);
                        method.LoadLocal(locals.uint256A);
                        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                        method.Convert<int>();
                        method.LoadElement<byte[]>();
                        method.StoreLocal(locals.localArray);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.LoadLocal(locals.localArray);
                        method.Call(Word.SetArray);
                        method.Branch(endOfOpcode);

                        method.MarkLabel(blobVersionedHashNotFound);
                        method.MarkLabel(indexTooLarge);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.BLOCKHASH:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label blockHashReturnedNull = method.DefineLabel();
                        Label endOfOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        method.LoadLocalAddress(locals.uint256A);
                        method.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
                        method.StoreLocal(locals.int64A);

                        envLoader.LoadBlockhashProvider(method, locals, false);
                        envLoader.LoadHeader(method, locals, false);

                        method.LoadLocalAddress(locals.int64A);
                        method.CallVirtual(typeof(IBlockhashProvider).GetMethod(nameof(IBlockhashProvider.GetBlockhash), [typeof(BlockHeader), typeof(long).MakeByRefType()]));
                        method.Duplicate();
                        method.StoreLocal(locals.hash256);
                        method.LoadNull();
                        method.BranchIfEqual(blockHashReturnedNull);

                        // not equal
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.LoadLocal(locals.hash256);
                        method.Call(GetPropertyInfo(typeof(Hash256), nameof(Hash256.Bytes), false, out _));
                        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                        method.Call(Word.SetReadOnlySpan);
                        method.Branch(endOfOpcode);
                        // equal to null

                        method.MarkLabel(blockHashReturnedNull);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.SIGNEXTEND:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label signIsNegative = method.DefineLabel();
                        Label endOfOpcodeHandling = method.DefineLabel();
                        Label argumentGt32 = method.DefineLabel();
                        using Local wordSpan = method.DeclareLocal(typeof(Span<byte>));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Duplicate();
                        method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                        method.StoreLocal(locals.uint32A);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadConstant(32);
                        method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                        method.BranchIfFalse(argumentGt32);

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetMutableSpan);
                        method.StoreLocal(wordSpan);

                        method.LoadConstant((uint)31);
                        method.LoadLocal(locals.uint32A);
                        method.Subtract();
                        method.StoreLocal(locals.uint32A);

                        method.LoadItemFromSpan<TDelegateType, byte>(locals.uint32A, false, wordSpan);
                        method.LoadIndirect<byte>();
                        method.Convert<sbyte>();
                        method.LoadConstant(0);
                        method.BranchIfLess(signIsNegative);

                        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesZero32), BindingFlags.Static | BindingFlags.Public));
                        method.Branch(endOfOpcodeHandling);

                        method.MarkLabel(signIsNegative);
                        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesMax32), BindingFlags.Static | BindingFlags.Public));

                        method.MarkLabel(endOfOpcodeHandling);
                        method.LoadConstant(0);
                        method.LoadLocal(locals.uint32A);
                        method.EmitAsSpan();
                        method.StoreLocal(locals.localSpan);

                        method.LoadLocalAddress(locals.localSpan);
                        method.LoadLocalAddress(wordSpan);
                        method.LoadConstant(0);
                        method.LoadLocal(locals.uint32A);
                        method.Call(typeof(Span<byte>).GetMethod(nameof(Span<byte>.Slice), [typeof(int), typeof(int)]));
                        method.Call(typeof(Span<byte>).GetMethod(nameof(Span<byte>.CopyTo), [typeof(Span<byte>)]));

                        method.MarkLabel(argumentGt32);
                    };
                }
                
            case Instruction.LOG0:
            case Instruction.LOG1:
            case Instruction.LOG2:
            case Instruction.LOG3:
            case Instruction.LOG4:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        var topicsCount = (sbyte)(opcode - Instruction.LOG0);
                        using Local logEntry = method.DeclareLocal<LogEntry>();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A); // position
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B); // length
                                                            // UpdateMemoryCost
                        envLoader.LoadVmState(method, locals, false);


                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A); // position
                        method.LoadLocalAddress(locals.uint256B); // length
                        method.Call(
                            typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(
                                nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)
                            )
                        );
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        // update locals.gasAvailable
                        method.LoadLocal(locals.gasAvailable);
                        method.LoadConstant(topicsCount * GasCostOf.LogTopic);
                        method.Convert<ulong>();
                        method.LoadLocalAddress(locals.uint256B); // length
                        method.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
                        method.Convert<ulong>();
                        method.LoadConstant(GasCostOf.LogData);
                        method.Multiply();
                        method.Add();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable); // locals.gasAvailable -= gasCost
                        method.LoadConstant((ulong)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadEnv(method, locals, true);
                        method.LoadField(
                            GetFieldInfo(
                                typeof(ExecutionEnvironment),
                                nameof(ExecutionEnvironment.ExecutingAccount)
                            )
                        );

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A); // position
                        method.LoadLocalAddress(locals.uint256B); // length
                        method.Call(
                            typeof(EvmPooledMemory).GetMethod(
                                nameof(EvmPooledMemory.Load),
                                [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]
                            )
                        );
                        method.StoreLocal(locals.localReadOnlyMemory);
                        method.LoadLocalAddress(locals.localReadOnlyMemory);
                        method.Call(typeof(ReadOnlyMemory<byte>).GetMethod(nameof(ReadOnlyMemory<byte>.ToArray)));

                        method.LoadConstant(topicsCount);
                        method.NewArray<Hash256>();
                        for (var k = 0; k < topicsCount; k++)
                        {
                            method.Duplicate();
                            method.LoadConstant(k);
                            using (Local keccak = method.DeclareLocal(typeof(ValueHash256)))
                            {
                                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0) - 2, k + 1);
                                method.Call(Word.GetKeccak);
                                method.StoreLocal(keccak);
                                method.LoadLocalAddress(keccak);
                                method.NewObject(typeof(Hash256), typeof(ValueHash256).MakeByRefType());
                            }
                            method.StoreElement<Hash256>();
                        }
                        // Creat an LogEntry Object from Items on the Stack
                        method.NewObject(typeof(LogEntry), typeof(Address), typeof(byte[]), typeof(Hash256[]));
                        method.StoreLocal(logEntry);

                        envLoader.LoadVmState(method, locals, false);

                        using Local accessTrackerLocal = method.DeclareLocal<StackAccessTracker>();
                        method.Call(typeof(EvmState).GetProperty(nameof(EvmState.AccessTracker), BindingFlags.Instance | BindingFlags.Public).GetGetMethod());
                        method.LoadObject<StackAccessTracker>();
                        method.StoreLocal(accessTrackerLocal);

                        method.LoadLocalAddress(accessTrackerLocal);
                        method.CallVirtual(GetPropertyInfo(typeof(StackAccessTracker), nameof(StackAccessTracker.Logs), getSetter: false, out _));
                        method.LoadLocal(logEntry);
                        method.CallVirtual(
                            typeof(ICollection<LogEntry>).GetMethod(nameof(ICollection<LogEntry>.Add))
                        );
                    };
                }
                
            case Instruction.TSTORE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetArray);
                        method.StoreLocal(locals.localArray);

                        envLoader.LoadEnv(method, locals, false);

                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.LoadLocalAddress(locals.uint256A);
                        method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                        method.StoreLocal(locals.storageCell);

                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocalAddress(locals.storageCell);
                        method.LoadLocal(locals.localArray);
                        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.SetTransientState), [typeof(StorageCell).MakeByRefType(), typeof(byte[])]));
                    };
                }
                
            case Instruction.TLOAD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        envLoader.LoadEnv(method, locals, false);

                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.LoadLocalAddress(locals.uint256A);
                        method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                        method.StoreLocal(locals.storageCell);

                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocalAddress(locals.storageCell);
                        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.GetTransientState), [typeof(StorageCell).MakeByRefType()]));
                        method.StoreLocal(locals.localReadonOnlySpan);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.LoadLocal(locals.localReadonOnlySpan);
                        method.Call(Word.SetReadOnlySpan);
                    };
                }
                
            case Instruction.SSTORE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetReadOnlySpan);
                        method.StoreLocal(locals.localReadonOnlySpan);

                        envLoader.LoadVmState(method, locals, false);

                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.localReadonOnlySpan);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);

                        MethodInfo nonTracingSStoreMethod = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>)
                                    .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.InstructionSStore), BindingFlags.Static | BindingFlags.Public)
                                    .MakeGenericMethod(typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing));

                        MethodInfo tracingSStoreMethod = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>)
                                    .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.InstructionSStore), BindingFlags.Static | BindingFlags.Public)
                                    .MakeGenericMethod(typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing));

                        if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                        {
                            method.Call(nonTracingSStoreMethod);
                        }
                        else
                        {
                            Label callNonTracingMode = method.DefineLabel();
                            Label skipBeyondCalls = method.DefineLabel();
                            envLoader.LoadTxTracer(method, locals, false);
                            method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
                            method.BranchIfFalse(callNonTracingMode);
                            method.Call(tracingSStoreMethod);
                            method.Branch(skipBeyondCalls);
                            method.MarkLabel(callNonTracingMode);
                            method.Call(nonTracingSStoreMethod);
                            method.MarkLabel(skipBeyondCalls);
                        }

                        Label endOfOpcode = method.DefineLabel();
                        method.Duplicate();
                        method.StoreLocal(locals.uint32A);
                        method.LoadConstant((int)EvmExceptionType.None);
                        method.BranchIfEqual(endOfOpcode);

                        envLoader.LoadResult(method, locals, true);
                        method.Duplicate();
                        method.LoadLocal(locals.uint32A);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                        method.LoadConstant((int)ContractState.Failed);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

                        envLoader.LoadGasAvailable(method, locals, true);
                        method.LoadLocal(locals.gasAvailable);
                        method.StoreIndirect<long>();
                        method.FakeBranch(escapeLabels.exitLabel); ;

                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.SLOAD:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.LoadLocal(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetSLoadCost)));
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);

                        envLoader.LoadEnv(method, locals, false);

                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.LoadLocalAddress(locals.uint256A);
                        method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                        method.StoreLocal(locals.storageCell);

                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.storageCell);
                        method.LoadConstant((int)VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.StorageAccessType.SLOAD);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeStorageAccessGas), BindingFlags.Static | BindingFlags.Public));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocalAddress(locals.storageCell);
                        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.Get), [typeof(StorageCell).MakeByRefType()]));
                        method.StoreLocal(locals.localReadonOnlySpan);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.LoadLocal(locals.localReadonOnlySpan);
                        method.Call(Word.SetReadOnlySpan);
                    };
                }
                
            case Instruction.EXTCODESIZE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.LoadLocal(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(locals.address);

                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadVmState(method, locals, false);
                        method.LoadLocal(locals.address);
                        method.LoadConstant(false);
                        envLoader.LoadWorldState(method, locals, false);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        method.LoadConstant(true);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeAccountAccessGas)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

                        envLoader.LoadCodeInfoRepository(method, locals, false);
                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocal(locals.address);
                        envLoader.LoadSpec(method, locals, false);
                        method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                        method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));
                        method.StoreLocal(locals.localReadOnlyMemory);
                        method.LoadLocalAddress(locals.localReadOnlyMemory);
                        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));

                        method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                    };
                }
                
            case Instruction.EXTCODECOPY:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label endOfOpcode = method.DefineLabel();

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 4);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256C);

                        method.LoadLocal(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                        method.LoadLocalAddress(locals.uint256C);
                        method.LoadLocalAddress(locals.lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Memory);
                        method.Multiply();
                        method.Add();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(locals.address);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256A);
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(locals.uint256B);

                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadVmState(method, locals, false);
                        method.LoadLocal(locals.address);
                        method.LoadConstant(false);
                        envLoader.LoadWorldState(method, locals, false);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        method.LoadConstant(true);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeAccountAccessGas)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(endOfOpcode);

                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocalAddress(locals.gasAvailable);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        envLoader.LoadCodeInfoRepository(method, locals, false);
                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocal(locals.address);
                        envLoader.LoadSpec(method, locals, false);
                        method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                        method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));

                        method.LoadLocalAddress(locals.uint256B);
                        method.LoadLocal(locals.uint256C);
                        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                        method.Convert<int>();
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.StoreLocal(locals.localZeroPaddedSpan);

                        envLoader.LoadMemory(method, locals, true);
                        method.LoadLocalAddress(locals.uint256A);
                        method.LoadLocalAddress(locals.localZeroPaddedSpan);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.EXTCODEHASH:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        Label endOfOpcode = method.DefineLabel();

                        method.LoadLocal(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeHashCost)));
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(locals.address);

                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadVmState(method, locals, false);
                        method.LoadLocal(locals.address);
                        method.LoadConstant(false);
                        envLoader.LoadWorldState(method, locals, false);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        method.LoadConstant(true);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeAccountAccessGas)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        Label pushZeroLabel = method.DefineLabel();
                        Label pushhashcodeLabel = method.DefineLabel();

                        // account exists
                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocal(locals.address);
                        method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.AccountExists)));
                        method.BranchIfFalse(pushZeroLabel);

                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocal(locals.address);
                        method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.IsDeadAccount)));
                        method.BranchIfTrue(pushZeroLabel);

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        envLoader.LoadCodeInfoRepository(method, locals, false);
                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocal(locals.address);
                        method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.GetExecutableCodeHash), [typeof(IWorldState), typeof(Address)]));
                        method.Call(Word.SetKeccak);
                        method.Branch(endOfOpcode);

                        // Push 0
                        method.MarkLabel(pushZeroLabel);
                        method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

                        method.MarkLabel(endOfOpcode);
                    };
                }
                
            case Instruction.SELFBALANCE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        envLoader.LoadWorldState(method, locals, false);
                        envLoader.LoadEnv(method, locals, false);

                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                        method.Call(Word.SetUInt256);
                    };
                }
                
            case Instruction.BALANCE:
                {
                    return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                    {
                        method.LoadLocal(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetBalanceCost)));
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(locals.gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(locals.address);

                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadVmState(method, locals, false);

                        method.LoadLocal(locals.address);
                        method.LoadConstant(false);
                        envLoader.LoadWorldState(method, locals, false);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        method.LoadConstant(true);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeAccountAccessGas)));
                        method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocal(locals.address);
                        method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                        method.Call(Word.SetUInt256);
                    };
                }
                
            case Instruction.SELFDESTRUCT:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    MethodInfo selfDestructNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>)
                        .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.InstructionSelfDestruct), BindingFlags.Static | BindingFlags.Public);

                    MethodInfo selfDestructTracing = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>)
                        .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.InstructionSelfDestruct), BindingFlags.Static | BindingFlags.Public);

                    Label skipGasDeduction = method.DefineLabel();
                    Label happyPath = method.DefineLabel();

                    envLoader.LoadSpec(method, locals, false);
                    method.CallVirtual(typeof(IReleaseSpec).GetProperty(nameof(IReleaseSpec.UseShanghaiDDosProtection)).GetGetMethod());
                    method.BranchIfFalse(skipGasDeduction);

                    method.LoadLocal(locals.gasAvailable);
                    method.LoadConstant(GasCostOf.SelfDestructEip150);
                    method.Subtract();
                    method.Duplicate();
                    method.StoreLocal(locals.gasAvailable);
                    method.LoadConstant((long)0);
                    method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                    method.MarkLabel(skipGasDeduction);

                    envLoader.LoadVmState(method, locals, false);
                    envLoader.LoadWorldState(method, locals, false);
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                    method.Call(Word.GetAddress);
                    method.LoadLocalAddress(locals.gasAvailable);
                    envLoader.LoadSpec(method, locals, false);
                    envLoader.LoadTxTracer(method, locals, false);
                    if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                    {
                        method.Call(selfDestructNotTracing);
                    }
                    else
                    {
                        Label skipNonTracingCall = method.DefineLabel();
                        Label skipTracingCall = method.DefineLabel();
                        envLoader.LoadTxTracer(method, locals, false);
                        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
                        method.BranchIfFalse(skipTracingCall);
                        method.Call(selfDestructTracing);
                        method.Branch(skipNonTracingCall);
                        method.MarkLabel(skipTracingCall);
                        method.Call(selfDestructNotTracing);
                        method.MarkLabel(skipNonTracingCall);
                    }
                    method.StoreLocal(locals.uint32A);
                    method.LoadLocal(locals.uint32A);
                    method.LoadConstant((int)EvmExceptionType.None);
                    method.BranchIfEqual(happyPath);

                    envLoader.LoadResult(method, locals, true);
                    method.Duplicate();
                    method.LoadLocal(locals.uint32A);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                    method.LoadConstant((int)ContractState.Failed);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

                    method.FakeBranch(escapeLabels.exitLabel);

                    method.MarkLabel(happyPath);
                    envLoader.LoadResult(method, locals, true);
                    method.LoadConstant((int)ContractState.Finished);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                    method.FakeBranch(escapeLabels.returnLabel);
                };
                
            case Instruction.CALL:
            case Instruction.CALLCODE:
            case Instruction.DELEGATECALL:
            case Instruction.STATICCALL:
                return  (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    MethodInfo callMethodTracign = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>)
                        .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.InstructionCall), BindingFlags.Static | BindingFlags.Public)
                        .MakeGenericMethod(typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing));

                    MethodInfo callMethodNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>)
                        .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.InstructionCall), BindingFlags.Static | BindingFlags.Public)
                        .MakeGenericMethod(typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing));

                    using Local toPushToStack = method.DeclareLocal(typeof(UInt256?));
                    using Local newStateToExe = method.DeclareLocal<object>();
                    Label happyPath = method.DefineLabel();

                    envLoader.LoadVmState(method, locals, false);
                    envLoader.LoadWorldState(method, locals, false);
                    method.LoadLocalAddress(locals.gasAvailable);
                    envLoader.LoadSpec(method, locals, false);
                    envLoader.LoadTxTracer(method, locals, false);
                    envLoader.LoadLogger(method, locals, false);

                    method.LoadConstant((int)instruction);

                    var index = 1;
                    // load gasLimit
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                    method.Call(Word.GetUInt256);

                    // load codeSource
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                    method.Call(Word.GetAddress);

                    if(instruction is Instruction.DELEGATECALL)
                    {
                        envLoader.LoadEnv(method, locals, false);
                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                    }
                    else if (instruction is Instruction.STATICCALL)
                    {
                        method.LoadField(typeof(UInt256).GetField(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
                    }
                    else
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                        method.Call(Word.GetUInt256);
                    }
                    // load dataoffset
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                    method.Call(Word.GetUInt256);

                    // load datalength
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                    method.Call(Word.GetUInt256);

                    // load outputOffset
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                    method.Call(Word.GetUInt256);

                    // load outputLength
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
                    method.Call(Word.GetUInt256);

                    method.LoadLocalAddress(toPushToStack);

                    envLoader.LoadReturnDataBuffer(method, locals, true);

                    method.LoadLocalAddress(newStateToExe);

                    if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                    {
                        method.Call(callMethodNotTracing);
                    }
                    else
                    {
                        Label skipNonTracingCall = method.DefineLabel();
                        Label skipTracingCall = method.DefineLabel();
                        envLoader.LoadTxTracer(method, locals, false);
                        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
                        method.BranchIfFalse(skipTracingCall);
                        method.Call(callMethodTracign);
                        method.Branch(skipNonTracingCall);
                        method.MarkLabel(skipTracingCall);
                        method.Call(callMethodNotTracing);
                        method.MarkLabel(skipNonTracingCall);
                    }
                    method.StoreLocal(locals.uint32A);
                    method.LoadLocal(locals.uint32A);
                    method.LoadConstant((int)EvmExceptionType.None);
                    method.BranchIfEqual(happyPath);

                    envLoader.LoadResult(method, locals, true);
                    method.Duplicate();
                    method.LoadLocal(locals.uint32A);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                    method.LoadConstant((int)ContractState.Failed);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

                    method.FakeBranch(escapeLabels.exitLabel); ;

                    method.MarkLabel(happyPath);

                    Label skipStateMachineScheduling = method.DefineLabel();

                    method.LoadLocal(newStateToExe);
                    method.LoadNull();
                    method.BranchIfEqual(skipStateMachineScheduling);

                    method.LoadLocal(newStateToExe);
                    method.Call(GetPropertyInfo(typeof(VirtualMachine.CallResult), nameof(VirtualMachine.CallResult.BoxedEmpty), false, out _));
                    method.Call(typeof(object).GetMethod(nameof(ReferenceEquals), BindingFlags.Static | BindingFlags.Public));
                    method.BranchIfTrue(skipStateMachineScheduling);

                    if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                    {
                        UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, pc, currentSubSegment);
                    }
                    else
                    {
                        UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, opcodeMetadata);
                        EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                    }

                    // cast object to CallResult and store it in 
                    envLoader.LoadResult(method, locals, true);
                    method.Duplicate();
                    method.LoadLocal(newStateToExe);
                    method.CastClass(typeof(EvmState));
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));
                    method.LoadConstant((int)ContractState.Halted);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                    method.FakeBranch(escapeLabels.returnLabel);

                    method.MarkLabel(skipStateMachineScheduling);
                    Label hasNoItemsToPush = method.DefineLabel();

                    method.LoadLocalAddress(toPushToStack);
                    method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.HasValue)).GetGetMethod());
                    method.BranchIfFalse(hasNoItemsToPush);

                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
                    method.LoadLocalAddress(toPushToStack);
                    method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.Value)).GetGetMethod());
                    method.Call(Word.SetUInt256);

                    method.MarkLabel(hasNoItemsToPush);
                };
                
            case Instruction.CREATE:
            case Instruction.CREATE2:
                return (codeInfo, ilCompilerConfig, contractMetadata,  currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels)  =>
                {
                    MethodInfo callMethodTracign = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>)
                        .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.InstructionCreate), BindingFlags.Static | BindingFlags.Public)
                        .MakeGenericMethod(typeof(VirtualMachine.IsTracing));

                    MethodInfo callMethodNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>)
                        .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.InstructionCreate), BindingFlags.Static | BindingFlags.Public)
                        .MakeGenericMethod(typeof(VirtualMachine.NotTracing));

                    using Local toPushToStack = method.DeclareLocal(typeof(UInt256?));
                    using Local newStateToExe = method.DeclareLocal<object>();
                    Label happyPath = method.DefineLabel();

                    envLoader.LoadVmState(method, locals, false);

                    envLoader.LoadWorldState(method, locals, false);
                    method.LoadLocalAddress(locals.gasAvailable);
                    envLoader.LoadSpec(method, locals, false);
                    envLoader.LoadTxTracer(method, locals, false);
                    envLoader.LoadLogger(method, locals, false);

                    method.LoadConstant((int)instruction);

                    var index = 1;

                    // load value
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                    method.Call(Word.GetUInt256);

                    // load memory offset
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                    method.Call(Word.GetUInt256);

                    // load initcode len
                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                    method.Call(Word.GetUInt256);

                    // load callvalue
                    if (instruction is Instruction.CREATE2)
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
                        method.Call(Word.GetMutableSpan);
                    }
                    else
                    {
                        // load empty span
                        index--;
                        method.Call(typeof(Span<byte>).GetProperty(nameof(Span<byte>.Empty), BindingFlags.Static | BindingFlags.Public).GetGetMethod());
                    }

                    method.LoadLocalAddress(toPushToStack);

                    envLoader.LoadReturnDataBuffer(method, locals, true);

                    method.LoadLocalAddress(newStateToExe);

                    if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                    {
                        method.Call(callMethodNotTracing);
                    }
                    else
                    {
                        Label skipNonTracingCall = method.DefineLabel();
                        Label skipTracingCall = method.DefineLabel();
                        envLoader.LoadTxTracer(method, locals, false);
                        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
                        method.BranchIfFalse(skipTracingCall);
                        method.Call(callMethodTracign);
                        method.Branch(skipNonTracingCall);
                        method.MarkLabel(skipTracingCall);
                        method.Call(callMethodNotTracing);
                        method.MarkLabel(skipNonTracingCall);
                    }
                    method.StoreLocal(locals.uint32A);

                    method.LoadLocal(locals.uint32A);
                    method.LoadConstant((int)EvmExceptionType.None);
                    method.BranchIfEqual(happyPath);

                    envLoader.LoadResult(method, locals, true);
                    method.Duplicate();
                    method.LoadLocal(locals.uint32A);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                    method.LoadConstant((int)ContractState.Failed);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                    method.FakeBranch(escapeLabels.exitLabel);

                    method.MarkLabel(happyPath);
                    Label hasNoItemsToPush = method.DefineLabel();

                    method.LoadLocalAddress(toPushToStack);
                    method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.HasValue)).GetGetMethod());
                    method.BranchIfFalse(hasNoItemsToPush);

                    method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
                    method.LoadLocalAddress(toPushToStack);
                    method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.Value)).GetGetMethod());
                    method.Call(Word.SetUInt256);

                    method.MarkLabel(hasNoItemsToPush);

                    Label skipStateMachineScheduling = method.DefineLabel();

                    method.LoadLocal(newStateToExe);
                    method.LoadNull();
                    method.BranchIfEqual(skipStateMachineScheduling);

                    if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                    {
                        UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, pc, currentSubSegment);
                    }
                    else
                    {
                        UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, opcodeMetadata);
                        EmitCallToEndInstructionTrace(method, locals.gasAvailable, envLoader, locals);
                    }

                    envLoader.LoadResult(method, locals, true);
                    method.Duplicate();
                    method.LoadLocal(newStateToExe);
                    method.CastClass(typeof(EvmState));
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));
                    method.LoadConstant((int)ContractState.Halted);
                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                    method.Branch(escapeLabels.returnLabel);

                    method.MarkLabel(skipStateMachineScheduling);
                };
                
            default:
                {
                    return (codeInfo, ilCompilerConfig, contractMetadata, currentSubSegment, pc, opcode, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, escapeLabels) =>
                    {
                        method.FakeBranch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                    };
                }
        }
    }
}
