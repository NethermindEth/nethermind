// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Config;
using Nethermind.Int256;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading.Tasks;
using static Nethermind.Evm.CodeAnalysis.IL.WordEmit;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using static Nethermind.Evm.CodeAnalysis.IL.StackEmit;
using Nethermind.State;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Sigil.NonGeneric;
using Nethermind.Core.Crypto;
using Org.BouncyCastle.Ocsp;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal delegate void OpcodeILEmitterDelegate<T>(IVMConfig ilCompilerConfig, ContractMetadata contractMetadata, SegmentMetadata currentSegment, int opcodeIndex, OpcodeInfo opcodeMetadata, Sigil.Emit<T> method, Locals<T> localVariables, EnvLoader<T> envStateLoader, Dictionary<EvmExceptionType, Sigil.Label> exceptions, Label returnLabel);
internal abstract class OpcodeILEmitter<T>
{
    public Dictionary<Instruction, OpcodeILEmitterDelegate<T>> opcodeEmitters = new();
    public bool AddEmitter(Instruction instruction, OpcodeILEmitterDelegate<T> emitter)
    {
        if(opcodeEmitters.ContainsKey(instruction))
        {
            return false;
        }
        opcodeEmitters.Add(instruction, emitter);
        return true;
    }

    public void ReplaceEmitter(Instruction instruction, OpcodeILEmitterDelegate<T> emitter)
    {
        opcodeEmitters[instruction] = emitter;
    }

    public void RemoveEmitter(Instruction instruction)
    {
        opcodeEmitters.Remove(instruction);
    }

    public void Emit(IVMConfig ilCompilerConfig, ContractMetadata contractMetadata, SegmentMetadata currentSegment, int opcodeIndex, OpcodeInfo opcodeMetadata, Sigil.Emit<T> method,  Locals<T> localVariables, EnvLoader<T> envStateLoader, Dictionary<EvmExceptionType, Sigil.Label> exceptions, Label returnLabel)
    {
        if (opcodeEmitters.TryGetValue(opcodeMetadata.Operation, out var emitter))
        {
            emitter(ilCompilerConfig, contractMetadata, currentSegment, opcodeIndex, opcodeMetadata, method, localVariables, envStateLoader, exceptions, returnLabel);
        }
        else
        {
            if(opcodeEmitters.TryGetValue(Instruction.INVALID, out var emitInvalidOpcode))
            {
                emitInvalidOpcode(ilCompilerConfig, contractMetadata, currentSegment, opcodeIndex, opcodeMetadata, method, localVariables, envStateLoader, exceptions, returnLabel);
            }
            else
            {
                throw new InvalidOperationException($"Opcode {opcodeMetadata.Operation} is not supported");
            }
        }
    }
}
internal class PartialAotOpcodeEmitter<TDelegateType> : OpcodeILEmitter<TDelegateType>
{
    public PartialAotOpcodeEmitter()
    {
        Instruction[] instructions = (Instruction[])Enum.GetValues(typeof(Instruction));

        foreach (var instruction in instructions)
        {
            switch (instruction)
            {
                case Instruction.POP:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        // do nothing
                    });
                    break;
                case Instruction.STOP:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            envLoader.LoadResult(method, locals, true);
                            method.LoadConstant(true);
                            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldStop)));
                            method.FakeBranch(ret);
                        });
                    }
                    break;
                case Instruction.CHAINID:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadChainId(method, locals, false);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.NOT:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
                            MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo notVector256Method = typeof(Vector256)
                                .GetMethod(nameof(Vector256.OnesComplement), BindingFlags.Public | BindingFlags.Static)!
                                .MakeGenericMethod(typeof(byte));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(refWordToRefByteMethod);
                            method.Duplicate();
                            method.Call(readVector256Method);
                            method.Call(notVector256Method);
                            method.Call(writeVector256Method);
                        });
                    }
                    break;
                case Instruction.PUSH0:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                        });
                    }
                    break;
                case Instruction.PUSH1:
                case Instruction.PUSH2:
                case Instruction.PUSH3:
                case Instruction.PUSH4:
                case Instruction.PUSH5:
                case Instruction.PUSH6:
                case Instruction.PUSH7:
                case Instruction.PUSH8:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            if (contractMetadata.EmbeddedData[opcodeMetadata.Arguments.Value].IsZero())
                            {
                                method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            }
                            else
                            {
                                method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                                method.SpecialPushOpcode(opcodeMetadata, contractMetadata.EmbeddedData);
                            }
                        });
                    }
                    break;
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
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            if (contractMetadata.EmbeddedData[opcodeMetadata.Arguments.Value].IsZero())
                            {
                                method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            }
                            else
                            {
                                method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                                envLoader.LoadImmediatesData(method, locals, false);
                                method.LoadConstant(opcodeMetadata.Arguments.Value);
                                method.LoadElement<byte[]>();
                                method.Call(Word.SetArray);
                            }
                        });
                    }
                    break;
                case Instruction.ADD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Add();
                            method.StoreLocal(locals.uint64A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        });
                    }
                    break;
                case Instruction.SUB:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label pushNegItemB = method.DefineLabel();
                            Label pushItemA = method.DefineLabel();
                            // b - a a::b
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushItemA);

                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushNegItemB);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.BranchIfLess(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Subtract();
                            method.StoreLocal(locals.uint64A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushItemA);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushNegItemB);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.ToNegative);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        });
                    }
                    break;
                case Instruction.MUL:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label push0Zero = method.DefineLabel();
                            Label pushItemA = method.DefineLabel();
                            Label pushItemB = method.DefineLabel();
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(push0Zero);

                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(endofOpcode);

                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(endofOpcode);

                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushItemA);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Multiply();
                            method.StoreLocal(locals.uint64A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.Branch(endofOpcode);

                            method.MarkLabel(push0Zero);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushItemA);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));

                            method.MarkLabel(endofOpcode);
                        });
                    }
                    break;
                case Instruction.MOD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label pushZeroLabel = method.DefineLabel();
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallBackToOldBehavior);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint32);
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.Remainder();
                            method.StoreLocal(locals.uint64A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint64A);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        });

                    }
                    break;
                case Instruction.SMOD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            // if b is 1 or 0 result is always 0
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Mod), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        });

                    }
                    break;
                case Instruction.DIV:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label pushZeroLabel = method.DefineLabel();
                            Label pushALabel = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            // if a or b are 0 result is directly 0
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);
                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            // if b is 1 result is by default a
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushALabel);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushALabel);
                            method.LoadLocal(locals.wordRef256B);
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);

                            method.MarkLabel(endofOpcode);
                        });

                    }
                    break;
                case Instruction.SDIV:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label fallBackToOldBehavior = method.DefineLabel();
                            Label pushZeroLabel = method.DefineLabel();
                            Label pushALabel = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            // if b is 0 or a is 0 then the result is 0
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);
                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfTrue(pushZeroLabel);

                            // if b is 1 in all cases the result is a
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsOneCheck();
                            method.BranchIfTrue(pushALabel);

                            // if b is -1 and a is 2^255 then the result is 2^255
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsMinusOneCheck();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsP255Check();
                            method.BranchIfFalse(fallBackToOldBehavior);

                            method.Branch(endofOpcode);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Branch(endofOpcode);

                            method.MarkLabel(pushALabel);
                            method.LoadLocal(locals.wordRef256B);
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallBackToOldBehavior);
                            EmitBinaryInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Divide), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);

                            method.MarkLabel(endofOpcode);
                        });

                    }
                    break;
                case Instruction.ADDMOD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label push0Zero = method.DefineLabel();
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.StoreLocal(locals.wordRef256C);

                            // if c is 1 or 0 result is 0
                            method.LoadLocal(locals.wordRef256C);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitTrinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.AddMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.uint256C);
                            method.MarkLabel(endofOpcode);
                        });

                    }
                    break;
                case Instruction.MULMOD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label push0Zero = method.DefineLabel();
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.StoreLocal(locals.wordRef256C);

                            // since (a * b) % c 
                            // if a or b are 0 then the result is 0
                            // if c is 0 or 1 then the result is 0
                            method.LoadLocal(locals.wordRef256A);
                            method.EmitIsZeroCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.EmitIsZeroCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256C);
                            method.EmitIsZeroOrOneCheck();
                            method.BranchIfFalse(fallbackToUInt256Call);

                            // since (a * b) % c == (a % c * b % c) % c
                            // if a or b are equal to c, then the result is 0
                            method.LoadLocal(locals.wordRef256A);
                            method.LoadLocal(locals.wordRef256C);
                            method.Call(Word.AreEqual);
                            method.BranchIfTrue(push0Zero);
                            method.LoadLocal(locals.wordRef256B);
                            method.LoadLocal(locals.wordRef256C);
                            method.Call(Word.AreEqual);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.MarkLabel(push0Zero);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitTrinaryUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod(nameof(UInt256.MultiplyMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.uint256C);
                            method.MarkLabel(endofOpcode);
                        });

                    }
                    break;
                case Instruction.SHL:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        EmitShiftUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), isLeft: true, evmExceptionLabels, locals.uint256A, locals.uint256B);
                    });

                    break;
                case Instruction.SHR:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        EmitShiftUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), isLeft: false, evmExceptionLabels, locals.uint256A, locals.uint256B);
                    });

                    break;
                case Instruction.SAR:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        EmitShiftInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), evmExceptionLabels, locals.uint256A, locals.uint256B);
                    });

                    break;
                case Instruction.AND:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseAnd), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                    });

                    break;
                case Instruction.OR:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseOr), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                    });

                    break;
                case Instruction.XOR:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        EmitBitwiseUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Vector256).GetMethod(nameof(Vector256.Xor), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                    });

                    break;
                case Instruction.EXP:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label powerIsZero = method.DefineLabel();
                            Label baseIsOneOrZero = method.DefineLabel();
                            Label endOfExpImpl = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
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

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint256R);
                            method.Call(Word.SetUInt256);

                            method.Branch(endOfExpImpl);

                            method.MarkLabel(powerIsZero);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadConstant(1);
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                            method.Branch(endOfExpImpl);

                            method.MarkLabel(baseIsOneOrZero);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint256A);
                            method.Call(Word.SetUInt256);
                            method.Branch(endOfExpImpl);

                            method.MarkLabel(endOfExpImpl);
                        });
                    }
                    break;
                case Instruction.LT:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.CompareLessThan();
                            method.StoreLocal(locals.byte8B);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.byte8B);
                            method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitComparaisonUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        });
                    }

                    break;
                case Instruction.GT:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label fallbackToUInt256Call = method.DefineLabel();
                            Label endofOpcode = method.DefineLabel();
                            // we the two uint256 from the locals.stackHeadRef
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetIsUint64);
                            method.BranchIfFalse(fallbackToUInt256Call);

                            method.LoadLocal(locals.wordRef256A);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.LoadLocal(locals.wordRef256B);
                            method.CallGetter(Word.GetULong0, BitConverter.IsLittleEndian);
                            method.CompareGreaterThan();
                            method.StoreLocal(locals.byte8B);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.byte8B);
                            method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                            method.Branch(endofOpcode);

                            method.MarkLabel(fallbackToUInt256Call);
                            EmitComparaisonUInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, locals.uint256A, locals.uint256B);
                            method.MarkLabel(endofOpcode);
                        });
                    }
                    break;
                case Instruction.SLT:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        EmitComparaisonInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), false, evmExceptionLabels, locals.uint256A, locals.uint256B);
                    });

                    break;
                case Instruction.SGT:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        EmitComparaisonInt256Method(method, locals.uint256R, (locals.stackHeadRef, locals.stackHeadIdx, segmentMetadata.StackOffsets[i]), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), true, evmExceptionLabels, locals.uint256A, locals.uint256B);
                    });

                    break;
                case Instruction.EQ:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
                            MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
                            MethodInfo operationUnegenerified = typeof(Vector256).GetMethod(nameof(Vector256.EqualsAll), BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(byte));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(refWordToRefByteMethod);
                            method.Call(readVector256Method);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(refWordToRefByteMethod);
                            method.Call(readVector256Method);

                            method.Call(operationUnegenerified);
                            method.StoreLocal(locals.lbool);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.lbool);
                            method.Convert<uint>();
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);

                        });
                    }
                    break;
                case Instruction.ISZERO:
                    {// we load the locals.stackHeadRef
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.Duplicate();
                            method.EmitIsZeroCheck();
                            method.StoreLocal(locals.lbool);
                            method.Call(Word.SetToZero);
                            method.LoadLocal(locals.lbool);
                            method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
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
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            int count = (int)opcodeMetadata.Operation - (int)Instruction.DUP1 + 1;
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], count);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                        });
                    }
                    break;
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
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            int count = (int)opcodeMetadata.Operation - (int)Instruction.SWAP1 + 1;

                            method.LoadLocalAddress(locals.uint256R);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], count + 1);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], count + 1);
                            method.LoadLocalAddress(locals.uint256R);
                            method.LoadObject(typeof(Word));
                            method.StoreObject(typeof(Word));
                        });
                    }
                    break;
                case Instruction.CODESIZE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadConstant(contractMetadata.TargetCodeInfo.MachineCode.Length);
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.PC:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadConstant(opcodeMetadata.ProgramCounter);
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.COINBASE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
                            method.Call(Word.SetAddress);
                        });
                    }
                    break;
                case Instruction.TIMESTAMP:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.NUMBER:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.GASLIMIT:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasLimit), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.CALLER:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadEnv(method, locals, false);
                            
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
                            method.Call(Word.SetAddress);
                        });
                    }
                    break;
                case Instruction.ADDRESS:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadEnv(method, locals, false);
                            
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.Call(Word.SetAddress);
                        });
                    }
                    break;
                case Instruction.ORIGIN:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadTxContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin), false, out _));
                            method.Call(Word.SetAddress);
                        });
                    }
                    break;
                case Instruction.CALLVALUE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadEnv(method, locals, false);
                            
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                            method.Call(Word.SetUInt256);
                        });
                    }
                    break;
                case Instruction.GASPRICE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadTxContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice), false, out _));
                            method.Call(Word.SetUInt256);
                        });
                    }
                    break;
                case Instruction.CALLDATACOPY:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
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
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
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
                        });
                    }
                    break;
                case Instruction.CALLDATALOAD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            envLoader.LoadCalldata(method, locals, false);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadConstant(Word.Size);
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.Call(Word.SetZeroPaddedSpan);
                        });
                    }
                    break;
                case Instruction.CALLDATASIZE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadCalldata(method, locals, true);
                            method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.MSIZE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);

                            envLoader.LoadMemory(method, locals, true);
                            method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.MSTORE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.StoreLocal(locals.wordRef256B);

                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadConstant(Word.Size);
                            method.Call(ConvertionExplicit<UInt256, int>());
                            method.StoreLocal(locals.uint256C);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals, true);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocal(locals.wordRef256B);
                            method.Call(Word.GetMutableSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
                        });
                    }
                    break;
                case Instruction.MSTORE8:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.CallGetter(Word.GetByte0, BitConverter.IsLittleEndian);
                            method.StoreLocal(locals.byte8A);

                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadConstant(1);
                            method.Call(ConvertionExplicit<UInt256, int>());
                            method.StoreLocal(locals.uint256C);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals, true);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocal(locals.byte8A);

                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
                        });
                    }
                    break;
                case Instruction.MLOAD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadFieldAddress(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BigInt32)));
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals, true);
                            method.LoadLocalAddress(locals.uint256A);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.StoreLocal(locals.localReadonOnlySpan);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        });
                    }
                    break;
                case Instruction.MCOPY:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
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
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals, true);
                            method.LoadLocalAddress(locals.uint256A);
                            envLoader.LoadMemory(method, locals, true);
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
                        });
                    }
                    break;
                case Instruction.KECCAK256:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            MethodInfo refWordToRefValueHashMethod = UnsafeEmit.GetAsMethodInfo<Word, ValueHash256>();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
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
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMemory(method, locals, true);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(refWordToRefValueHashMethod);
                            method.Call(typeof(KeccakCache).GetMethod(nameof(KeccakCache.ComputeTo), [typeof(ReadOnlySpan<byte>), typeof(ValueHash256).MakeByRefType()]));
                        });
                    }
                    break;
                case Instruction.BYTE:
                    {// load a
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.StoreLocal(locals.uint32A);
                            method.StoreLocal(locals.wordRef256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetReadOnlySpan);
                            method.StoreLocal(locals.localReadonOnlySpan);

                            Label pushZeroLabel = method.DefineLabel();
                            Label endOfInstructionImpl = method.DefineLabel();
                            method.LoadLocal(locals.wordRef256A);
                            method.Call(Word.GetIsUint16);
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

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.LoadLocal(locals.uint32A);
                            method.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                            method.Branch(endOfInstructionImpl);

                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.MarkLabel(endOfInstructionImpl);
                        });
                    }
                    break;
                case Instruction.CODECOPY:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
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

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadMachineCode(method, locals, false);
                            method.StoreLocal(locals.localReadOnlyMemory);

                            method.LoadLocal(locals.localReadOnlyMemory);
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256C);
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
                        });
                    }
                    break;
                case Instruction.GAS:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            method.LoadLocal(locals.gasAvailable);
                            method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.RETURNDATASIZE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadResult(method, locals, true);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                            method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                        });
                    }
                    break;
                case Instruction.RETURNDATACOPY:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label endOfOpcode = method.DefineLabel();
                            using Local tempResult = method.DeclareLocal(typeof(UInt256));


                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256C);

                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256C);
                            method.LoadLocalAddress(tempResult);
                            method.Call(typeof(UInt256).GetMethod(nameof(UInt256.AddOverflow)));
                            method.LoadLocalAddress(tempResult);
                            envLoader.LoadResult(method, locals, true);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
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
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadResult(method, locals, true);
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            method.LoadObject(typeof(ReadOnlyMemory<byte>));
                            method.LoadLocalAddress(locals.uint256B);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(MethodInfo<UInt256>("op_Explicit", typeof(Int32), new[] { typeof(UInt256).MakeByRefType() }));
                            method.LoadConstant((int)PadDirection.Right);
                            method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                            method.StoreLocal(locals.localZeroPaddedSpan);

                            envLoader.LoadMemory(method, locals, true);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.localZeroPaddedSpan);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                            method.MarkLabel(endOfOpcode);
                        });
                    }
                    break;
                case Instruction.RETURN or Instruction.REVERT:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadResult(method, locals, true);
                            method.LoadObject(typeof(ILChunkExecutionState));
                            method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));
                            envLoader.LoadMemory(method, locals, true);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256B);
                            method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Load), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                            method.StoreObject<ReadOnlyMemory<byte>>();

                            envLoader.LoadResult(method, locals, true);
                            method.LoadConstant(true);
                            switch (opcodeMetadata.Operation)
                            {
                                case Instruction.REVERT:
                                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldRevert)));
                                    break;
                                case Instruction.RETURN:
                                    method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldReturn)));
                                    break;
                            }
                            method.FakeBranch(ret);
                        });
                    }
                    break;
                case Instruction.BASEFEE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.BaseFeePerGas), false, out _));
                            method.Call(Word.SetUInt256);
                        });
                    }
                    break;
                case Instruction.BLOBBASEFEE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            using Local uint256Nullable = method.DeclareLocal(typeof(UInt256?));
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadBlockContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.BlobBaseFee), false, out _));
                            method.StoreLocal(uint256Nullable);
                            method.LoadLocalAddress(uint256Nullable);
                            method.Call(GetPropertyInfo(typeof(UInt256?), nameof(Nullable<UInt256>.Value), false, out _));
                            method.Call(Word.SetUInt256);
                        });
                    }
                    break;
                case Instruction.PREVRANDAO:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label isPostMergeBranch = method.DefineLabel();
                            Label endOfOpcode = method.DefineLabel();
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);

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
                        });
                    }
                    break;
                case Instruction.BLOBHASH:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
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

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
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
                            method.LoadElement<Byte[]>();
                            method.StoreLocal(locals.localArray);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.localArray);
                            method.Call(Word.SetArray);
                            method.Branch(endOfOpcode);

                            method.MarkLabel(blobVersionedHashNotFound);
                            method.MarkLabel(indexTooLarge);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.MarkLabel(endOfOpcode);
                        });
                    }
                    break;
                case Instruction.BLOCKHASH:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label blockHashReturnedNull = method.DefineLabel();
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.LoadLocalAddress(locals.uint256A);
                            method.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
                            method.StoreLocal(locals.int64A);

                            envLoader.LoadBlockhashProvider(method, locals, false);
                            envLoader.LoadBlockContext(method, locals, true);
                            method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                            method.LoadLocalAddress(locals.int64A);
                            method.CallVirtual(typeof(IBlockhashProvider).GetMethod(nameof(IBlockhashProvider.GetBlockhash), [typeof(BlockHeader), typeof(long).MakeByRefType()]));
                            method.Duplicate();
                            method.StoreLocal(locals.hash256);
                            method.LoadNull();
                            method.BranchIfEqual(blockHashReturnedNull);

                            // not equal
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.hash256);
                            method.Call(GetPropertyInfo(typeof(Hash256), nameof(Hash256.Bytes), false, out _));
                            method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                            method.Call(Word.SetReadOnlySpan);
                            method.Branch(endOfOpcode);
                            // equal to null

                            method.MarkLabel(blockHashReturnedNull);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            method.MarkLabel(endOfOpcode);
                        });
                    }
                    break;
                case Instruction.SIGNEXTEND:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label signIsNegative = method.DefineLabel();
                            Label endOfOpcodeHandling = method.DefineLabel();
                            Label argumentGt32 = method.DefineLabel();
                            using Local wordSpan = method.DeclareLocal(typeof(Span<byte>));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Duplicate();
                            method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                            method.StoreLocal(locals.uint32A);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadConstant(32);
                            method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                            method.BranchIfFalse(argumentGt32);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetMutableSpan);
                            method.StoreLocal(wordSpan);

                            method.LoadConstant((uint)31);
                            method.LoadLocal(locals.uint32A);
                            method.Subtract();
                            method.StoreLocal(locals.uint32A);

                            method.LoadItemFromSpan<TDelegateType, byte>(wordSpan, locals.uint32A);
                            method.LoadIndirect<byte>();
                            method.Convert<sbyte>();
                            method.LoadConstant((sbyte)0);
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
                        });
                    }
                    break;
                case Instruction.LOG0:
                case Instruction.LOG1:
                case Instruction.LOG2:
                case Instruction.LOG3:
                case Instruction.LOG4:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            sbyte topicsCount = (sbyte)(opcodeMetadata.Operation - Instruction.LOG0);

                            envLoader.LoadVmState(method, locals, false);
                            
                            method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                            method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StaticCallViolation));

                            using Local logEntry = method.DeclareLocal<LogEntry>();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A); // position
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B); // length
                                                                // UpdateMemoryCost
                            envLoader.LoadVmState(method, locals, false);
                            

                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A); // position
                            method.LoadLocalAddress(locals.uint256B); // length
                            method.Call(
                                typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(
                                    nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)
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
                            for (int k = 0; k < topicsCount; k++)
                            {
                                method.Duplicate();
                                method.LoadConstant(k);
                                using (Local keccak = method.DeclareLocal(typeof(ValueHash256)))
                                {
                                    method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i] - 2, k + 1);
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
                            

                            method.LoadFieldAddress(typeof(EvmState).GetField("_accessTracker", BindingFlags.Instance | BindingFlags.NonPublic));
                            method.CallVirtual(GetPropertyInfo(typeof(StackAccessTracker), nameof(StackAccessTracker.Logs), getSetter: false, out _));
                            method.LoadLocal(logEntry);
                            method.CallVirtual(
                                typeof(ICollection<LogEntry>).GetMethod(nameof(ICollection<LogEntry>.Add))
                            );
                        });
                    }
                    break;
                case Instruction.TSTORE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            envLoader.LoadVmState(method, locals, false);
                            
                            method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                            method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StaticCallViolation));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
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
                        });
                    }
                    break;
                case Instruction.TLOAD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
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

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        });
                    }
                    break;
                case Instruction.SSTORE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetReadOnlySpan);
                            method.StoreLocal(locals.localReadonOnlySpan);

                            envLoader.LoadVmState(method, locals, false);
                            
                            envLoader.LoadWorldState(method, locals, false);
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.localReadonOnlySpan);
                            envLoader.LoadSpec(method, locals, false);
                            envLoader.LoadTxTracer(method, locals, false);

                            MethodInfo nonTracingSStoreMethod = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>)
                                        .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.InstructionSStore), BindingFlags.Static | BindingFlags.NonPublic)
                                        .MakeGenericMethod(typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing));

                            MethodInfo tracingSStoreMethod = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>)
                                        .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.InstructionSStore), BindingFlags.Static | BindingFlags.NonPublic)
                                        .MakeGenericMethod(typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing));

                            if (!ilCompilerConfig.BakeInTracingInAotModes)
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
                            method.LoadLocal(locals.uint32A);
                            method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

                            envLoader.LoadGasAvailable(method, locals, true);
                            method.LoadLocal(locals.gasAvailable);
                            method.StoreIndirect<long>();
                            method.Return();

                            method.MarkLabel(endOfOpcode);
                        });
                    }
                    break;
                case Instruction.SLOAD:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals, false);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetSLoadCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
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
                            method.LoadConstant((int)VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.StorageAccessType.SLOAD);
                            envLoader.LoadSpec(method, locals, false);
                            envLoader.LoadTxTracer(method, locals, false);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeStorageAccessGas), BindingFlags.Static | BindingFlags.NonPublic));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            envLoader.LoadWorldState(method, locals, false);
                            method.LoadLocalAddress(locals.storageCell);
                            method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.Get), [typeof(StorageCell).MakeByRefType()]));
                            method.StoreLocal(locals.localReadonOnlySpan);

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.LoadLocal(locals.localReadonOnlySpan);
                            method.Call(Word.SetReadOnlySpan);
                        });
                    }
                    break;
                case Instruction.EXTCODESIZE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals, false);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(locals.address);

                            method.LoadLocalAddress(locals.gasAvailable);
                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocal(locals.address);
                            method.LoadConstant(true);
                            envLoader.LoadWorldState(method, locals, false);
                            envLoader.LoadSpec(method, locals, false);
                            envLoader.LoadTxTracer(method, locals, false);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);

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
                        });
                    }
                    break;
                case Instruction.EXTCODECOPY:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            Label endOfOpcode = method.DefineLabel();

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 4);
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

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(locals.address);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 2);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256A);
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 3);
                            method.Call(Word.GetUInt256);
                            method.StoreLocal(locals.uint256B);

                            method.LoadLocalAddress(locals.gasAvailable);
                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocal(locals.address);
                            method.LoadConstant(true);
                            envLoader.LoadWorldState(method, locals, false);
                            envLoader.LoadSpec(method, locals, false);
                            envLoader.LoadTxTracer(method, locals, false);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                            method.BranchIfTrue(endOfOpcode);

                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocalAddress(locals.gasAvailable);
                            method.LoadLocalAddress(locals.uint256A);
                            method.LoadLocalAddress(locals.uint256C);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.UpdateMemoryCost)));
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
                        });
                    }
                    break;
                case Instruction.EXTCODEHASH:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
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

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            method.Call(Word.GetAddress);
                            method.StoreLocal(locals.address);

                            method.LoadLocalAddress(locals.gasAvailable);
                            envLoader.LoadVmState(method, locals, false);
                            
                            method.LoadLocal(locals.address);
                            method.LoadConstant(true);
                            envLoader.LoadWorldState(method, locals, false);
                            envLoader.LoadSpec(method, locals, false);
                            envLoader.LoadTxTracer(method, locals, false);
                            method.LoadConstant(true);
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
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

                            using Local delegateAddress = method.DeclareLocal<Address>();
                            envLoader.LoadCodeInfoRepository(method, locals, false);
                            envLoader.LoadWorldState(method, locals, false);
                            method.LoadLocal(locals.address);
                            method.LoadLocalAddress(delegateAddress);
                            method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.TryGetDelegation), [typeof(IWorldState), typeof(Address), typeof(Address).MakeByRefType()]));
                            method.BranchIfFalse(pushhashcodeLabel);

                            envLoader.LoadWorldState(method, locals, false);
                            method.LoadLocal(delegateAddress);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.AccountExists)));
                            method.BranchIfFalse(pushZeroLabel);

                            envLoader.LoadWorldState(method, locals, false);
                            method.LoadLocal(delegateAddress);
                            method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.IsDeadAccount)));
                            method.BranchIfTrue(pushZeroLabel);

                            method.MarkLabel(pushhashcodeLabel);
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            envLoader.LoadCodeInfoRepository(method, locals, false);
                            envLoader.LoadWorldState(method, locals, false);
                            method.LoadLocal(locals.address);
                            method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.GetExecutableCodeHash), [typeof(IWorldState), typeof(Address)]));
                            method.Call(Word.SetKeccak);
                            method.Branch(endOfOpcode);

                            // Push 0
                            method.MarkLabel(pushZeroLabel);
                            method.CleanWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);

                            method.MarkLabel(endOfOpcode);
                        });
                    }
                    break;
                case Instruction.SELFBALANCE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 0);
                            envLoader.LoadWorldState(method, locals, false);
                            envLoader.LoadEnv(method, locals, false);
                            
                            method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                            method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                            method.Call(Word.SetUInt256);
                        });
                    }
                    break;
                case Instruction.BALANCE:
                    {
                        AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.LoadLocal(locals.gasAvailable);
                            envLoader.LoadSpec(method, locals, false);
                            method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetBalanceCost)));
                            method.Subtract();
                            method.Duplicate();
                            method.StoreLocal(locals.gasAvailable);
                            method.LoadConstant((long)0);
                            method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
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
                            method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.ChargeAccountAccessGas)));
                            method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                            method.CleanAndLoadWord(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                            envLoader.LoadWorldState(method, locals, false);
                            method.LoadLocal(locals.address);
                            method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                            method.Call(Word.SetUInt256);
                        });
                    }
                    break;
                case Instruction.SELFDESTRUCT:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        MethodInfo selfDestructNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.InstructionSelfDestruct), BindingFlags.Static | BindingFlags.Public);

                        MethodInfo selfDestructTracing = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.InstructionSelfDestruct), BindingFlags.Static | BindingFlags.Public);

                        Label skipGasDeduction = method.DefineLabel();
                        Label happyPath = method.DefineLabel();

                        envLoader.LoadVmState(method, locals, false);
                        
                        method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                        method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StaticCallViolation));

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

                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(locals.address);

                        envLoader.LoadVmState(method, locals, false);
                        
                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocal(locals.address);
                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        if(ilCompilerConfig.BakeInTracingInAotModes)
                        {
                            method.Call(selfDestructTracing);
                        } else
                        {
                            method.Call(selfDestructNotTracing);
                        }
                        method.StoreLocal(locals.uint32A);


                        method.LoadLocal(locals.uint32A);
                        method.LoadConstant((int)EvmExceptionType.None);
                        method.BranchIfEqual(happyPath);

                        envLoader.LoadResult(method, locals, true);
                        method.LoadLocal(locals.uint32A);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

                        envLoader.LoadGasAvailable(method, locals, true);
                        method.LoadLocal(locals.gasAvailable);
                        method.StoreIndirect<long>();
                        method.Return();

                        method.MarkLabel(happyPath);
                        envLoader.LoadResult(method, locals, true);
                        method.LoadConstant(true);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldStop)));
                        method.FakeBranch(ret);
                    });
                    break;
                default:
                    {
                        AddEmitter(Instruction.INVALID, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                        {
                            method.FakeBranch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                        });
                    }
                    break;
            }
        }
    }
}

internal class FullAotOpcodeEmitter<T> : PartialAotOpcodeEmitter<T>
{
    public FullAotOpcodeEmitter() {
        // statefull opcode
        Instruction[] statefullOpcodes = [
            Instruction.CALL,
            Instruction.CALLCODE,
            Instruction.DELEGATECALL,
            Instruction.STATICCALL,
            Instruction.CREATE,
            Instruction.CREATE2,
            ];

        foreach (Instruction instruction in statefullOpcodes)
        {
            switch(instruction)
            {
                case Instruction.CALL:
                case Instruction.CALLCODE:
                case Instruction.DELEGATECALL:
                case Instruction.STATICCALL:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        MethodInfo callMethodTracign = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.InstructionCall), BindingFlags.Static | BindingFlags.NonPublic)
                            .MakeGenericMethod(typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing));

                        MethodInfo callMethodNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.InstructionCall), BindingFlags.Static | BindingFlags.NonPublic)
                            .MakeGenericMethod(typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing));

                        using Local toPushToStack = method.DeclareLocal(typeof(ReadOnlyMemory<byte>?));
                        using Local newStateToExe = method.DeclareLocal<Object>();
                        Label happyPath = method.DefineLabel();

                        envLoader.LoadVmState(method, locals, false);
                        

                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        envLoader.LoadLogger(method, locals, false);

                        method.LoadConstant((int)instruction);

                        int index = 1;
                        // load gasLimit
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load codeSource
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetAddress);

                        // load callvalue
                        if (instruction is Instruction.CALL)
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                            method.Call(Word.GetUInt256);
                        }
                        else
                        {
                            method.LoadField(typeof(UInt256).GetField(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
                        }

                        // load dataoffset
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load datalength
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load datasize
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load outputOffset
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load outputLength
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index);
                        method.Call(Word.GetUInt256);

                        method.LoadLocalAddress(toPushToStack);

                        method.LoadObject(typeof(ILChunkExecutionState));
                        method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));

                        method.LoadLocalAddress(newStateToExe);

                        if (ilCompilerConfig.BakeInTracingInAotModes)
                        {
                            method.Call(callMethodTracign);
                        }
                        else
                        {
                            method.Call(callMethodNotTracing);
                        }
                        method.StoreLocal(locals.uint32A);

                        method.LoadLocal(locals.uint32A);
                        method.LoadConstant((int)EvmExceptionType.None);
                        method.BranchIfEqual(happyPath);

                        envLoader.LoadResult(method, locals, true);
                        method.LoadLocal(locals.uint32A);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

                        envLoader.LoadGasAvailable(method, locals, true);
                        method.LoadLocal(locals.gasAvailable);
                        method.StoreIndirect<long>();
                        method.Return();

                        method.MarkLabel(happyPath);

                        Label skipStateMachineScheduling = method.DefineLabel();

                        method.LoadLocal(newStateToExe);
                        method.LoadNull();
                        method.BranchIfEqual(skipStateMachineScheduling);

                        method.LoadLocal(newStateToExe);
                        method.Call(GetPropertyInfo(typeof(VirtualMachine.CallResult), nameof(VirtualMachine.CallResult.BoxedEmpty), false, out _));
                        method.Call(typeof(Object).GetMethod(nameof(Object.ReferenceEquals), BindingFlags.Static | BindingFlags.Public));
                        method.BranchIfTrue(skipStateMachineScheduling);

                        // cast object to CallResult and store it in 
                        envLoader.LoadResult(method, locals, true);
                        method.Duplicate();
                        method.LoadLocal(newStateToExe);
                        method.CastClass(typeof(EvmState));
                        method.NewObject(typeof(VirtualMachine.CallResult), typeof(EvmState));
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));

                        method.LoadConstant(true);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldContinue)));
                        method.Branch(ret);

                        method.MarkLabel(skipStateMachineScheduling);
                        Label hasNoItemsToPush = method.DefineLabel();

                        method.LoadLocal(toPushToStack);
                        method.LoadNull();
                        method.BranchIfEqual(hasNoItemsToPush);

                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index);
                        method.LoadLocal(toPushToStack);
                        method.Call(Word.SetReadOnlyMemory);

                        method.MarkLabel(hasNoItemsToPush);
                    });
                    break;
                case Instruction.CREATE:
                case Instruction.CREATE2:
                    AddEmitter(instruction, (ilCompilerConfig, contractMetadata, segmentMetadata, i, opcodeMetadata, method, locals, envLoader, evmExceptionLabels, ret) =>
                    {
                        MethodInfo callMethodTracign = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.InstructionCreate), BindingFlags.Static | BindingFlags.NonPublic)
                            .MakeGenericMethod(typeof(VirtualMachine.IsTracing));

                        MethodInfo callMethodNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>)
                            .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsOptimizing>.InstructionCreate), BindingFlags.Static | BindingFlags.NonPublic)
                            .MakeGenericMethod(typeof(VirtualMachine.NotTracing));

                        using Local toPushToStack = method.DeclareLocal(typeof(ReadOnlyMemory<byte>?));
                        using Local newStateToExe = method.DeclareLocal<Object>();
                        Label happyPath = method.DefineLabel();

                        envLoader.LoadVmState(method, locals, false);
                        

                        envLoader.LoadWorldState(method, locals, false);
                        method.LoadLocalAddress(locals.gasAvailable);
                        envLoader.LoadSpec(method, locals, false);
                        envLoader.LoadTxTracer(method, locals, false);
                        envLoader.LoadLogger(method, locals, false);

                        method.LoadConstant((int)instruction);

                        int index = 1;

                        // load value
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load memory offset
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load initcode len
                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                        method.Call(Word.GetUInt256);

                        // load callvalue
                        if (instruction is Instruction.CREATE2)
                        {
                            method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index++);
                            method.Call(Word.GetUInt256);
                        }
                        else
                        {
                            method.LoadConstant(0);
                            method.InitializeObject(typeof(Span<byte>));
                        }

                        method.LoadLocalAddress(toPushToStack);

                        method.LoadObject(typeof(ILChunkExecutionState));
                        method.LoadField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ReturnData)));

                        method.LoadLocalAddress(newStateToExe);

                        if (ilCompilerConfig.BakeInTracingInAotModes)
                        {
                            method.Call(callMethodTracign);
                        }
                        else
                        {
                            method.Call(callMethodNotTracing);
                        }
                        method.StoreLocal(locals.uint32A);

                        method.LoadLocal(locals.uint32A);
                        method.LoadConstant((int)EvmExceptionType.None);
                        method.BranchIfEqual(happyPath);

                        envLoader.LoadResult(method, locals, true);
                        method.LoadLocal(locals.uint32A);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));

                        envLoader.LoadGasAvailable(method, locals, true);
                        method.LoadLocal(locals.gasAvailable);
                        method.StoreIndirect<long>();
                        method.Return();

                        method.MarkLabel(happyPath);

                        Label skipStateMachineScheduling = method.DefineLabel();

                        method.LoadLocal(newStateToExe);
                        method.LoadNull();
                        method.BranchIfEqual(skipStateMachineScheduling);

                        method.LoadLocal(newStateToExe);
                        method.Call(GetPropertyInfo(typeof(VirtualMachine.CallResult), nameof(VirtualMachine.CallResult.BoxedEmpty), false, out _));
                        method.Call(typeof(Object).GetMethod(nameof(Object.ReferenceEquals), BindingFlags.Static | BindingFlags.Public));
                        method.BranchIfTrue(skipStateMachineScheduling);

                        // cast object to CallResult and store it in 
                        envLoader.LoadResult(method, locals, true);
                        method.Duplicate();
                        method.LoadLocal(newStateToExe);
                        method.CastClass(typeof(EvmState));
                        method.NewObject(typeof(VirtualMachine.CallResult), typeof(EvmState));
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));

                        method.LoadConstant(true);
                        method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ShouldContinue)));
                        method.Branch(ret);

                        method.MarkLabel(skipStateMachineScheduling);
                        Label hasNoItemsToPush = method.DefineLabel();

                        method.LoadLocal(toPushToStack);
                        method.LoadNull();
                        method.BranchIfEqual(hasNoItemsToPush);

                        method.StackLoadPrevious(locals.stackHeadRef, segmentMetadata.StackOffsets[i], index);
                        method.LoadLocal(toPushToStack);
                        method.Call(Word.SetUInt256);

                        method.MarkLabel(hasNoItemsToPush);
                    });
                    break;
            }
        }
    }
}
