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
using Nethermind.Evm.Config;

namespace Nethermind.Evm.CodeAnalysis.IL;

internal static class OpcodeEmitter
{
    public static void GetOpcodeILEmitter<TDelegateType>(
        this Emit<TDelegateType> method,
        Instruction op, CodeInfo codeInfo,
        IVMConfig ilCompilerConfig,
        ContractCompilerMetadata contractMetadata,
        SubSegmentMetadata currentSubSegment,
        int pc, OpcodeMetadata opcodeMetadata,
        Locals<TDelegateType> locals, 
        Dictionary<EvmExceptionType, Label> evmExceptionLabels, (Label returnLabel, Label jumpTable, Label exitLabel) escapeLabels)
    {
        switch (op)
        {
            case Instruction.JUMPDEST:
                return;

            case Instruction.JUMP:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.StoreLocal(locals.wordRef256A);

                if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                {
                    UpdateStackHeadAndPushRerSegmentMode(method, locals.stackHeadRef, locals.stackHeadIdx, pc, currentSubSegment);
                }
                else
                {
                    UpdateStackHeadIdxAndPushRefOpcodeMode(method, locals.stackHeadRef, locals.stackHeadIdx, opcodeMetadata);
                    EmitCallToEndInstructionTrace(method, locals.gasAvailable, locals);
                }
                method.FakeBranch(escapeLabels.jumpTable);
                return;
            case Instruction.JUMPI:
                Label noJump = method.DefineLabel(locals.GetLabelName());
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
                    EmitCallToEndInstructionTrace(method, locals.gasAvailable, locals);
                }
                method.Branch(escapeLabels.jumpTable);

                method.MarkLabel(noJump);
                return;

            case Instruction.POP:
                return;

            case Instruction.STOP:
                method.LoadResult(locals, true);
                method.LoadConstant((int)ContractState.Finished);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                method.FakeBranch(escapeLabels.returnLabel);
                return;
            case Instruction.CHAINID:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadChainId(locals, false);
                method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                return;
            case Instruction.NOT:
                MethodInfo refWordToRefByteMethod = GetAsMethodInfo<Word, byte>();
                MethodInfo readVector256Method = GetReadUnalignedMethodInfo<Vector256<byte>>();
                MethodInfo writeVector256Method = GetWriteUnalignedMethodInfo<Vector256<byte>>();
                MethodInfo notVector256Method = typeof(Vector256)
                    .GetMethod(nameof(Vector256.OnesComplement), BindingFlags.Public | BindingFlags.Static)!
                    .MakeGenericMethod(typeof(byte));

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(refWordToRefByteMethod);
                method.Duplicate();
                method.Call(readVector256Method);
                method.Call(notVector256Method);
                method.Call(writeVector256Method);
                return;
            case Instruction.PUSH0:
                method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                return;
            case Instruction.PUSH1:
            case Instruction.PUSH2:
            case Instruction.PUSH3:
            case Instruction.PUSH4:
            case Instruction.PUSH5:
            case Instruction.PUSH6:
            case Instruction.PUSH7:
            case Instruction.PUSH8:
                int length = Math.Min(codeInfo.MachineCode.Length - pc - 1, op - Instruction.PUSH0);
                var immediateBytes = codeInfo.MachineCode.Slice(pc + 1, length).Span;
                if (immediateBytes.IsZero())
                    method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                else
                {
                    method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                    method.SpecialPushOpcode(op, immediateBytes);
                }
                return;
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
                length = Math.Min(codeInfo.MachineCode.Length - pc - 1, op - Instruction.PUSH0);
                immediateBytes = codeInfo.MachineCode.Slice(pc + 1, length).Span;
                if (immediateBytes.IsZero())
                    method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                else
                {
                    if (length != 32)
                    {
                        method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        method.Call(GetAsMethodInfo<Word, byte>());
                        method.LoadConstant(32 - length);
                        method.Convert<nint>();
                        method.Call(GetAddBytesOffsetRef<byte>());
                    }
                    else
                    {
                        method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                        method.Call(GetAsMethodInfo<Word, byte>());
                    }
                    method.LoadMachineCode(locals, true);
                    method.LoadItemFromSpan<TDelegateType, byte>(pc + 1, true);
                    method.LoadConstant(length);
                    method.CopyBlock();
                }
                return;
            case Instruction.ADD:
                EmitBinaryUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                return;
            case Instruction.SUB:
                Label pushNegItemB = method.DefineLabel(locals.GetLabelName());
                Label pushItemA = method.DefineLabel(locals.GetLabelName());
                // b - a a::b
                Label fallbackToUInt256Call = method.DefineLabel(locals.GetLabelName());
                Label endofOpcode = method.DefineLabel(locals.GetLabelName());
                // we the two uint256 from the locals.stackHeadRef
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.StoreLocal(locals.wordRef256A);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.StoreLocal(locals.wordRef256B);

                method.EmitCheck(nameof(Word.IsZero), locals.wordRef256B);
                method.BranchIfTrue(pushItemA);

                EmitBinaryUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                method.Branch(endofOpcode);

                method.MarkLabel(pushItemA);
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.LoadLocal(locals.wordRef256A);
                method.LoadObject(typeof(Word));
                method.StoreObject(typeof(Word));

                method.MarkLabel(endofOpcode);
                return;
            case Instruction.MUL:
                Label push0Zero = method.DefineLabel(locals.GetLabelName());
                pushItemA = method.DefineLabel(locals.GetLabelName());
                Label pushItemB = method.DefineLabel(locals.GetLabelName());
                endofOpcode = method.DefineLabel(locals.GetLabelName());
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

                EmitBinaryUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
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
                return;

            case Instruction.MOD:
                Label pushZeroLabel = method.DefineLabel(locals.GetLabelName());
                Label fallBackToOldBehavior = method.DefineLabel(locals.GetLabelName());
                endofOpcode = method.DefineLabel(locals.GetLabelName());

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.StoreLocal(locals.wordRef256A);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.StoreLocal(locals.wordRef256B);

                method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256B);
                method.BranchIfTrue(pushZeroLabel);

                EmitBinaryUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                method.Branch(endofOpcode);

                method.MarkLabel(pushZeroLabel);
                method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.MarkLabel(endofOpcode);
                return;
            case Instruction.SMOD:
                Label bIsOneOrZero = method.DefineLabel(locals.GetLabelName());
                endofOpcode = method.DefineLabel(locals.GetLabelName());

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.StoreLocal(locals.wordRef256B);

                // if b is 1 or 0 result is always 0
                method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256B);
                method.BranchIfTrue(bIsOneOrZero);

                EmitBinaryInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Mod), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
                method.Branch(endofOpcode);

                method.MarkLabel(bIsOneOrZero);
                method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.MarkLabel(endofOpcode);
                return;
            case Instruction.DIV:
                fallBackToOldBehavior = method.DefineLabel(locals.GetLabelName());
                pushZeroLabel = method.DefineLabel(locals.GetLabelName());
                Label pushALabel = method.DefineLabel(locals.GetLabelName());
                endofOpcode = method.DefineLabel(locals.GetLabelName());

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

                EmitBinaryUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
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
                return;
            case Instruction.SDIV:
                fallBackToOldBehavior = method.DefineLabel(locals.GetLabelName());
                pushZeroLabel = method.DefineLabel(locals.GetLabelName());
                pushALabel = method.DefineLabel(locals.GetLabelName());
                endofOpcode = method.DefineLabel(locals.GetLabelName());

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
                EmitBinaryInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Divide), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!, null, evmExceptionLabels, locals.uint256A, locals.uint256B);
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

                return;

            case Instruction.ADDMOD:
                push0Zero = method.DefineLabel(locals.GetLabelName());
                fallbackToUInt256Call = method.DefineLabel(locals.GetLabelName());
                endofOpcode = method.DefineLabel(locals.GetLabelName());

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                method.StoreLocal(locals.wordRef256C);

                // if c is 1 or 0 result is 0
                method.EmitCheck(nameof(Word.IsOneOrZero), locals.wordRef256C);
                method.BranchIfFalse(fallbackToUInt256Call);

                method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                method.Branch(endofOpcode);

                method.MarkLabel(fallbackToUInt256Call);
                EmitTrinaryUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.AddMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.uint256C);
                method.MarkLabel(endofOpcode);
                return;
            case Instruction.MULMOD:
                push0Zero = method.DefineLabel(locals.GetLabelName());
                fallbackToUInt256Call = method.DefineLabel(locals.GetLabelName());
                endofOpcode = method.DefineLabel(locals.GetLabelName());
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
                EmitTrinaryUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod(nameof(UInt256.MultiplyMod), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, locals.uint256A, locals.uint256B, locals.uint256C);
                method.Branch(endofOpcode);

                method.MarkLabel(push0Zero);
                method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 3);
                method.Branch(endofOpcode);

                method.MarkLabel(endofOpcode);
                return;
            case Instruction.SHL:
                EmitShiftUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), isLeft: true, evmExceptionLabels, locals.uint256A, locals.uint256B);
                return;
            case Instruction.SHR:
                EmitShiftUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), isLeft: false, evmExceptionLabels, locals.uint256A, locals.uint256B);
                return;
            case Instruction.SAR:
                EmitShiftInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), evmExceptionLabels, locals.uint256A, locals.uint256B);
                return;
            case Instruction.AND:
                EmitBitwiseUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseAnd), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                return;
            case Instruction.OR:
                EmitBitwiseUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseOr), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                return;
            case Instruction.XOR:
                EmitBitwiseUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Vector256).GetMethod(nameof(Vector256.Xor), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                return;
            case Instruction.EXP:
                Label powerIsZero = method.DefineLabel(locals.GetLabelName());
                Label baseIsOneOrZero = method.DefineLabel(locals.GetLabelName());
                Label endOfExpImpl = method.DefineLabel(locals.GetLabelName());

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
                method.LoadSpec(locals, false);
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
                return;
            case Instruction.LT:
                EmitComparaisonUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, locals.uint256A, locals.uint256B);
                return;
            case Instruction.GT:
                EmitComparaisonUInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, locals.uint256A, locals.uint256B);
                return;
            case Instruction.SLT:
                EmitComparaisonInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), false, evmExceptionLabels, locals.uint256A, locals.uint256B);
                return;
            case Instruction.SGT:
                EmitComparaisonInt256Method(method, locals.uint256R, locals, (locals.stackHeadRef, locals.stackHeadIdx, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0)), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), true, evmExceptionLabels, locals.uint256A, locals.uint256B);
                return;
            case Instruction.EQ:
                refWordToRefByteMethod = GetAsMethodInfo<Word, byte>();
                readVector256Method = GetReadUnalignedMethodInfo<Vector256<byte>>();
                writeVector256Method = GetWriteUnalignedMethodInfo<Vector256<byte>>();
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

                return;
            case Instruction.ISZERO:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Duplicate();
                method.Duplicate();
                method.EmitCheck(nameof(Word.IsZero));
                method.StoreLocal(locals.lbool);
                method.Call(Word.SetToZero);
                method.LoadLocal(locals.lbool);
                method.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                return;
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
                var count = (int)op - (int)Instruction.DUP1 + 1;
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), count);
                method.LoadObject(typeof(Word));
                method.StoreObject(typeof(Word));
                return;
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
                count = (int)op - (int)Instruction.SWAP1 + 1;

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
                return;
            case Instruction.CODESIZE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadConstant(codeInfo.MachineCode.Length);
                method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                return;
            case Instruction.PC:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadConstant(pc);
                method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                return;
            case Instruction.COINBASE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadBlockContext(locals, true);
                method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
                method.Call(Word.SetAddress);
                return;
            case Instruction.TIMESTAMP:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadBlockContext(locals, true);
                method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
                method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                return;
            case Instruction.NUMBER:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadBlockContext(locals, true);
                method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
                method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                return;
            case Instruction.GASLIMIT:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadBlockContext(locals, true);
                method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasLimit), false, out _));
                method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                return;
            case Instruction.CALLER:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadEnv(locals, false);

                method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
                method.Call(Word.SetAddress);
                return;
            case Instruction.ADDRESS:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadEnv(locals, false);

                method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                method.Call(Word.SetAddress);
                return;
            case Instruction.ORIGIN:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadTxContext(locals, true);
                method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin), false, out _));
                method.Call(Word.SetAddress);
                return;
            case Instruction.CALLVALUE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadEnv(locals, false);

                method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                method.Call(Word.SetUInt256);
                return;
            case Instruction.GASPRICE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadTxContext(locals, true);
                method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice), false, out _));
                method.Call(Word.SetUInt256);
                return;
            case Instruction.CALLDATACOPY:
                Label endOfOpcode = method.DefineLabel(locals.GetLabelName());

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

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadCalldata(locals, false);
                method.LoadLocalAddress(locals.uint256B);
                method.LoadLocal(locals.uint256C);
                method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                method.Convert<int>();
                method.LoadConstant((int)PadDirection.Right);
                method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                method.StoreLocal(locals.localZeroPaddedSpan);

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.localZeroPaddedSpan);
                method.CallVirtual(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                method.MarkLabel(endOfOpcode);
                return;
            case Instruction.CALLDATALOAD:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);

                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

                method.LoadCalldata(locals, false);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadConstant(Word.Size);
                method.LoadConstant((int)PadDirection.Right);
                method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                method.Call(Word.SetZeroPaddedSpan);
                return;
            case Instruction.CALLDATASIZE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadCalldata(locals, true);
                method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                return;
            case Instruction.MSIZE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);

                method.LoadMemory(locals, true);
                method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
                method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                return;
            case Instruction.MSTORE:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.StoreLocal(locals.wordRef256B);

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadConstant(Word.Size);
                method.Call(ConvertionExplicit<UInt256, int>());
                method.StoreLocal(locals.uint256C);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocal(locals.wordRef256B);
                method.Call(Word.GetMutableSpan);
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
                return;
            case Instruction.MSTORE8:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.CallGetter(Word.GetByte0, BitConverter.IsLittleEndian);
                method.StoreLocal(locals.byte8A);

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadConstant(1);
                method.Call(ConvertionExplicit<UInt256, int>());
                method.StoreLocal(locals.uint256C);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocal(locals.byte8A);

                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
                return;
            case Instruction.MLOAD:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);

                method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BigInt32)));
                method.StoreLocal(locals.uint256B);

                method.LoadLocalAddress(locals.uint256B);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
                method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                method.StoreLocal(locals.localReadonOnlySpan);

                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.LoadLocal(locals.localReadonOnlySpan);
                method.Call(Word.SetReadOnlySpan);
                return;
            case Instruction.MCOPY:
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

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256B);
                method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Max)));
                method.StoreLocal(locals.uint256R);
                method.LoadLocalAddress(locals.uint256R);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256B);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
                return;
            case Instruction.KECCAK256:
                MethodInfo refWordToRefValueHashMethod = GetAsMethodInfo<Word, ValueHash256>();

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

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256B);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256B);
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.Call(refWordToRefValueHashMethod);
                method.Call(typeof(KeccakCache).GetMethod(nameof(KeccakCache.ComputeTo), [typeof(ReadOnlySpan<byte>), typeof(ValueHash256).MakeByRefType()]));
                return;
            case Instruction.BYTE:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Duplicate();
                method.CallGetter(Word.GetUInt0, BitConverter.IsLittleEndian);
                method.StoreLocal(locals.uint32A);
                method.StoreLocal(locals.wordRef256A);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.Call(Word.GetReadOnlySpan);
                method.StoreLocal(locals.localReadonOnlySpan);


                pushZeroLabel = method.DefineLabel(locals.GetLabelName());
                Label endOfInstructionImpl = method.DefineLabel(locals.GetLabelName());
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
                return;
            case Instruction.CODECOPY:
                endOfOpcode = method.DefineLabel(locals.GetLabelName());

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

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadMachineCode(locals, false);
                method.LoadLocalAddress(locals.uint256B);
                method.LoadLocalAddress(locals.uint256C);
                method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                method.Convert<int>();
                method.LoadConstant((int)PadDirection.Right);
                method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlySpan<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                method.StoreLocal(locals.localZeroPaddedSpan);

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.localZeroPaddedSpan);
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                method.MarkLabel(endOfOpcode);
                return;
            case Instruction.GAS:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadLocal(locals.gasAvailable);
                method.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                return;
            case Instruction.RETURNDATASIZE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadReturnDataBuffer(locals, true);
                method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                return;
            case Instruction.RETURNDATACOPY:
                endOfOpcode = method.DefineLabel(locals.GetLabelName());


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
                method.LoadLocalAddress(locals.uint256R);
                method.Call(typeof(UInt256).GetMethod(nameof(UInt256.AddOverflow)));
                method.LoadLocalAddress(locals.uint256R);

                method.LoadReturnDataBuffer(locals, true);
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

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadReturnDataBuffer(locals, false);
                method.LoadLocalAddress(locals.uint256B);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(MethodInfo<UInt256>("op_Explicit", typeof(int), new[] { typeof(UInt256).MakeByRefType() }));
                method.LoadConstant((int)PadDirection.Right);
                method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                method.StoreLocal(locals.localZeroPaddedSpan);

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.localZeroPaddedSpan);
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                method.MarkLabel(endOfOpcode);
                return;
            case Instruction.RETURN or Instruction.REVERT:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256B);

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256B);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadReturnDataBuffer(locals, true);
                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256B);
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Load), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                method.StoreObject<ReadOnlyMemory<byte>>();

                method.LoadResult(locals, true);
                switch (op)
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
                return;

            case Instruction.BASEFEE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadBlockContext(locals, true);
                method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));

                method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.BaseFeePerGas), false, out _));
                method.Call(Word.SetUInt256);
                return;
            case Instruction.BLOBBASEFEE:
                Local uint256Nullable = method.DeclareLocal(typeof(UInt256?), locals.GetLocalName());
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadBlockContext(locals, true);
                method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.BlobBaseFee), false, out _));
                method.StoreLocal(uint256Nullable);
                method.LoadLocalAddress(uint256Nullable);
                method.Call(GetPropertyInfo(typeof(UInt256?), nameof(Nullable<UInt256>.Value), false, out _));
                method.Call(Word.SetUInt256);
                uint256Nullable.Dispose();
                return;
            case Instruction.PREVRANDAO:
                Label isPostMergeBranch = method.DefineLabel(locals.GetLabelName());
                endOfOpcode = method.DefineLabel(locals.GetLabelName());
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);

                method.LoadBlockContext(locals, true);
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
                return;
            case Instruction.BLOBHASH:
                Label blobVersionedHashNotFound = method.DefineLabel(locals.GetLabelName());
                Label indexTooLarge = method.DefineLabel(locals.GetLabelName());
                endOfOpcode = method.DefineLabel(locals.GetLabelName());

                Local byteMatrix = method.DeclareLocal(typeof(byte[][]), locals.GetLocalName());
                method.LoadTxContext(locals, true);
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
                byteMatrix.Dispose();
                return;
            case Instruction.BLOCKHASH:
                Label blockHashReturnedNull = method.DefineLabel(locals.GetLabelName());
                endOfOpcode = method.DefineLabel(locals.GetLabelName());

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);

                method.LoadLocalAddress(locals.uint256A);
                method.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
                method.StoreLocal(locals.int64A);

                method.LoadBlockhashProvider(locals, false);
                method.LoadHeader(locals, false);

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
                return;
            case Instruction.SIGNEXTEND:
                Label signIsNegative = method.DefineLabel(locals.GetLabelName());
                Label endOfOpcodeHandling = method.DefineLabel(locals.GetLabelName());
                Label argumentGt32 = method.DefineLabel(locals.GetLabelName());
                Local wordSpan = method.DeclareLocal(typeof(Span<byte>), locals.GetLocalName());

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

                wordSpan.Dispose();
                return;
            case Instruction.LOG0:
            case Instruction.LOG1:
            case Instruction.LOG2:
            case Instruction.LOG3:
            case Instruction.LOG4:
                var topicsCount = (sbyte)(op - Instruction.LOG0);
                Local logEntry = method.DeclareLocal<LogEntry>(locals.GetLocalName());

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A); // position
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256B); // length
                                                    // UpdateMemoryCost
                method.LoadVmState(locals, false);


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

                method.LoadEnv(locals, true);
                method.LoadField(
                    GetFieldInfo(
                        typeof(ExecutionEnvironment),
                        nameof(ExecutionEnvironment.ExecutingAccount)
                    )
                );

                method.LoadMemory(locals, true);
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
                    using (Local keccak = method.DeclareLocal(typeof(ValueHash256), locals.GetLocalName()))
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

                method.LoadVmState(locals, false);

                Local accessTrackerLocal = method.DeclareLocal<StackAccessTracker>(locals.GetLocalName());
                method.Call(typeof(EvmState).GetProperty(nameof(EvmState.AccessTracker), BindingFlags.Instance | BindingFlags.Public).GetGetMethod());
                method.LoadObject<StackAccessTracker>();
                method.StoreLocal(accessTrackerLocal);

                method.LoadLocalAddress(accessTrackerLocal);
                method.CallVirtual(GetPropertyInfo(typeof(StackAccessTracker), nameof(StackAccessTracker.Logs), getSetter: false, out _));
                method.LoadLocal(logEntry);
                method.CallVirtual(
                    typeof(ICollection<LogEntry>).GetMethod(nameof(ICollection<LogEntry>.Add))
                );

                logEntry.Dispose();
                accessTrackerLocal.Dispose();
                return;
            case Instruction.TSTORE:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.Call(Word.GetArray);
                method.StoreLocal(locals.localArray);

                method.LoadEnv(locals, false);

                method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                method.LoadLocalAddress(locals.uint256A);
                method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                method.StoreLocal(locals.storageCell);

                method.LoadWorldState(locals, false);
                method.LoadLocalAddress(locals.storageCell);
                method.LoadLocal(locals.localArray);
                method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.SetTransientState), [typeof(StorageCell).MakeByRefType(), typeof(byte[])]));
                return;
            case Instruction.TLOAD:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);

                method.LoadEnv(locals, false);

                method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                method.LoadLocalAddress(locals.uint256A);
                method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                method.StoreLocal(locals.storageCell);

                method.LoadWorldState(locals, false);
                method.LoadLocalAddress(locals.storageCell);
                method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.GetTransientState), [typeof(StorageCell).MakeByRefType()]));
                method.StoreLocal(locals.localReadonOnlySpan);

                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.LoadLocal(locals.localReadonOnlySpan);
                method.Call(Word.SetReadOnlySpan);
                return;
            case Instruction.SSTORE:
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 2);
                method.Call(Word.GetReadOnlySpan);
                method.StoreLocal(locals.localReadonOnlySpan);

                method.LoadVmState(locals, false);

                method.LoadWorldState(locals, false);
                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.localReadonOnlySpan);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);

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
                    Label callNonTracingMode = method.DefineLabel(locals.GetLabelName());
                    Label skipBeyondCalls = method.DefineLabel(locals.GetLabelName());
                    method.LoadTxTracer(locals, false);
                    method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
                    method.BranchIfFalse(callNonTracingMode);
                    method.Call(tracingSStoreMethod);
                    method.Branch(skipBeyondCalls);
                    method.MarkLabel(callNonTracingMode);
                    method.Call(nonTracingSStoreMethod);
                    method.MarkLabel(skipBeyondCalls);
                }

                endOfOpcode = method.DefineLabel(locals.GetLabelName());
                method.Duplicate();
                method.StoreLocal(locals.uint32A);
                method.LoadConstant((int)EvmExceptionType.None);
                method.BranchIfEqual(endOfOpcode);

                method.LoadResult(locals, true);
                method.Duplicate();
                method.LoadLocal(locals.uint32A);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                method.LoadConstant((int)ContractState.Failed);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

                method.LoadGasAvailable(locals, true);
                method.LoadLocal(locals.gasAvailable);
                method.StoreIndirect<long>();
                method.FakeBranch(escapeLabels.exitLabel); ;

                method.MarkLabel(endOfOpcode);
                return;
            case Instruction.SLOAD:
                method.LoadLocal(locals.gasAvailable);
                method.LoadSpec(locals, false);
                method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetSLoadCost)));
                method.Subtract();
                method.Duplicate();
                method.StoreLocal(locals.gasAvailable);
                method.LoadConstant((long)0);
                method.BranchIfLess(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256A);

                method.LoadEnv(locals, false);

                method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                method.LoadLocalAddress(locals.uint256A);
                method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                method.StoreLocal(locals.storageCell);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.storageCell);
                method.LoadConstant((int)VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.StorageAccessType.SLOAD);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeStorageAccessGas), BindingFlags.Static | BindingFlags.Public));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadWorldState(locals, false);
                method.LoadLocalAddress(locals.storageCell);
                method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.Get), [typeof(StorageCell).MakeByRefType()]));
                method.StoreLocal(locals.localReadonOnlySpan);

                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.LoadLocal(locals.localReadonOnlySpan);
                method.Call(Word.SetReadOnlySpan);
                return;
            case Instruction.EXTCODESIZE:
                method.LoadLocal(locals.gasAvailable);
                method.LoadSpec(locals, false);
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
                method.LoadVmState(locals, false);
                method.LoadLocal(locals.address);
                method.LoadConstant(false);
                method.LoadWorldState(locals, false);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);
                method.LoadConstant(true);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeAccountAccessGas)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

                method.LoadCodeInfoRepository(locals, false);
                method.LoadWorldState(locals, false);
                method.LoadLocal(locals.address);
                method.LoadSpec(locals, false);
                method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));
                method.StoreLocal(locals.localReadOnlyMemory);
                method.LoadLocalAddress(locals.localReadOnlyMemory);
                method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));

                method.CallSetter(Word.SetInt0, BitConverter.IsLittleEndian);
                return;
            case Instruction.EXTCODECOPY:
                endOfOpcode = method.DefineLabel(locals.GetLabelName());

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 4);
                method.Call(Word.GetUInt256);
                method.StoreLocal(locals.uint256C);

                method.LoadLocal(locals.gasAvailable);
                method.LoadSpec(locals, false);
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
                method.LoadVmState(locals, false);
                method.LoadLocal(locals.address);
                method.LoadConstant(false);
                method.LoadWorldState(locals, false);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);
                method.LoadConstant(true);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeAccountAccessGas)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                method.BranchIfTrue(endOfOpcode);

                method.LoadVmState(locals, false);

                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.uint256C);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.UpdateMemoryCost)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.LoadCodeInfoRepository(locals, false);
                method.LoadWorldState(locals, false);
                method.LoadLocal(locals.address);
                method.LoadSpec(locals, false);
                method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));

                method.LoadLocalAddress(locals.uint256B);
                method.LoadLocal(locals.uint256C);
                method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                method.Convert<int>();
                method.LoadConstant((int)PadDirection.Right);
                method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                method.StoreLocal(locals.localZeroPaddedSpan);

                method.LoadMemory(locals, true);
                method.LoadLocalAddress(locals.uint256A);
                method.LoadLocalAddress(locals.localZeroPaddedSpan);
                method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                method.MarkLabel(endOfOpcode);
                return;
            case Instruction.EXTCODEHASH:
                endOfOpcode = method.DefineLabel(locals.GetLabelName());

                method.LoadLocal(locals.gasAvailable);
                method.LoadSpec(locals, false);
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
                method.LoadVmState(locals, false);
                method.LoadLocal(locals.address);
                method.LoadConstant(false);
                method.LoadWorldState(locals, false);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);
                method.LoadConstant(true);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeAccountAccessGas)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                pushZeroLabel = method.DefineLabel(locals.GetLabelName());
                Label pushhashcodeLabel = method.DefineLabel(locals.GetLabelName());

                // account exists
                method.LoadWorldState(locals, false);
                method.LoadLocal(locals.address);
                method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.AccountExists)));
                method.BranchIfFalse(pushZeroLabel);

                method.LoadWorldState(locals, false);
                method.LoadLocal(locals.address);
                method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IReadOnlyStateProvider.IsDeadAccount)));
                method.BranchIfTrue(pushZeroLabel);

                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.LoadCodeInfoRepository(locals, false);
                method.LoadWorldState(locals, false);
                method.LoadLocal(locals.address);
                method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.GetExecutableCodeHash), [typeof(IWorldState), typeof(Address)]));
                method.Call(Word.SetKeccak);
                method.Branch(endOfOpcode);

                // Push 0
                method.MarkLabel(pushZeroLabel);
                method.CleanWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);

                method.MarkLabel(endOfOpcode);
                return;
            case Instruction.SELFBALANCE:
                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 0);
                method.LoadWorldState(locals, false);
                method.LoadEnv(locals, false);

                method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                method.Call(Word.SetUInt256);
                return;
            case Instruction.BALANCE:
                method.LoadLocal(locals.gasAvailable);
                method.LoadSpec(locals, false);
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
                method.LoadVmState(locals, false);

                method.LoadLocal(locals.address);
                method.LoadConstant(false);
                method.LoadWorldState(locals, false);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);
                method.LoadConstant(true);
                method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.ChargeAccountAccessGas)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));

                method.CleanAndLoadWord(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.LoadWorldState(locals, false);
                method.LoadLocal(locals.address);
                method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                method.Call(Word.SetUInt256);
                return;
            case Instruction.SELFDESTRUCT:
                MethodInfo selfDestructNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>)
                    .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.InstructionSelfDestruct), BindingFlags.Static | BindingFlags.Public);

                MethodInfo selfDestructTracing = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>)
                    .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.InstructionSelfDestruct), BindingFlags.Static | BindingFlags.Public);

                Label skipGasDeduction = method.DefineLabel(locals.GetLabelName());
                Label happyPath = method.DefineLabel(locals.GetLabelName());

                method.LoadSpec(locals, false);
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

                method.LoadVmState(locals, false);
                method.LoadWorldState(locals, false);
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), 1);
                method.Call(Word.GetAddress);
                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);
                if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                {
                    method.Call(selfDestructNotTracing);
                }
                else
                {
                    Label skipNonTracingCall = method.DefineLabel(locals.GetLabelName());
                    Label skipTracingCall = method.DefineLabel(locals.GetLabelName());
                    method.LoadTxTracer(locals, false);
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

                method.LoadResult(locals, true);
                method.Duplicate();
                method.LoadLocal(locals.uint32A);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                method.LoadConstant((int)ContractState.Failed);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

                method.FakeBranch(escapeLabels.exitLabel);

                method.MarkLabel(happyPath);
                method.LoadResult(locals, true);
                method.LoadConstant((int)ContractState.Finished);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                method.FakeBranch(escapeLabels.returnLabel);
                return;
            case Instruction.CALL:
            case Instruction.CALLCODE:
            case Instruction.DELEGATECALL:
            case Instruction.STATICCALL:
                MethodInfo callMethodTracign = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>)
                    .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.InstructionCall), BindingFlags.Static | BindingFlags.Public)
                    .MakeGenericMethod(typeof(VirtualMachine.IsTracing), typeof(VirtualMachine.IsTracing));

                MethodInfo callMethodNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>)
                    .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.InstructionCall), BindingFlags.Static | BindingFlags.Public)
                    .MakeGenericMethod(typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing));

                Local toPushToStack = method.DeclareLocal(typeof(UInt256?), locals.GetLocalName());
                Local newStateToExe = method.DeclareLocal<object>(locals.GetLocalName());
                happyPath = method.DefineLabel(locals.GetLabelName());

                method.LoadVmState(locals, false);
                method.LoadWorldState(locals, false);
                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);
                method.LoadLogger(locals, false);

                method.LoadConstant((int)op);

                var index = 1;
                // load gasLimit
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                method.Call(Word.GetUInt256);

                // load codeSource
                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index++);
                method.Call(Word.GetAddress);

                if (op is Instruction.DELEGATECALL)
                {
                    method.LoadEnv(locals, false);
                    method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                }
                else if (op is Instruction.STATICCALL)
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

                method.LoadReturnDataBuffer(locals, true);

                method.LoadLocalAddress(newStateToExe);

                if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                {
                    method.Call(callMethodNotTracing);
                }
                else
                {
                    Label skipNonTracingCall = method.DefineLabel(locals.GetLabelName());
                    Label skipTracingCall = method.DefineLabel(locals.GetLabelName());
                    method.LoadTxTracer(locals, false);
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

                method.LoadResult(locals, true);
                method.Duplicate();
                method.LoadLocal(locals.uint32A);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                method.LoadConstant((int)ContractState.Failed);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));

                method.FakeBranch(escapeLabels.exitLabel); ;

                method.MarkLabel(happyPath);

                Label skipStateMachineScheduling = method.DefineLabel(locals.GetLabelName());

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
                    EmitCallToEndInstructionTrace(method, locals.gasAvailable, locals);
                }

                // cast object to CallResult and store it in 
                method.LoadResult(locals, true);
                method.Duplicate();
                method.LoadLocal(newStateToExe);
                method.CastClass(typeof(EvmState));
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));
                method.LoadConstant((int)ContractState.Halted);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                method.FakeBranch(escapeLabels.returnLabel);

                method.MarkLabel(skipStateMachineScheduling);
                Label hasNoItemsToPush = method.DefineLabel(locals.GetLabelName());

                method.LoadLocalAddress(toPushToStack);
                method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.HasValue)).GetGetMethod());
                method.BranchIfFalse(hasNoItemsToPush);

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
                method.LoadLocalAddress(toPushToStack);
                method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.Value)).GetGetMethod());
                method.Call(Word.SetUInt256);

                method.MarkLabel(hasNoItemsToPush);

                toPushToStack.Dispose();
                newStateToExe.Dispose();
                return;
            case Instruction.CREATE:
            case Instruction.CREATE2:
                callMethodTracign = typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>)
                    .GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.InstructionCreate), BindingFlags.Static | BindingFlags.Public)
                    .MakeGenericMethod(typeof(VirtualMachine.IsTracing));

                callMethodNotTracing = typeof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>)
                    .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing, VirtualMachine.IsPrecompiling>.InstructionCreate), BindingFlags.Static | BindingFlags.Public)
                    .MakeGenericMethod(typeof(VirtualMachine.NotTracing));

                toPushToStack = method.DeclareLocal(typeof(UInt256?), locals.GetLocalName());
                newStateToExe = method.DeclareLocal<object>(locals.GetLocalName());
                happyPath = method.DefineLabel(locals.GetLabelName());

                method.LoadVmState(locals, false);

                method.LoadWorldState(locals, false);
                method.LoadLocalAddress(locals.gasAvailable);
                method.LoadSpec(locals, false);
                method.LoadTxTracer(locals, false);
                method.LoadLogger(locals, false);

                method.LoadConstant((int)op);

                index = 1;

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
                if (op is Instruction.CREATE2)
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

                method.LoadReturnDataBuffer(locals, true);

                method.LoadLocalAddress(newStateToExe);

                if (ilCompilerConfig.IsIlEvmAggressiveModeEnabled)
                {
                    method.Call(callMethodNotTracing);
                }
                else
                {
                    Label skipNonTracingCall = method.DefineLabel(locals.GetLabelName());
                    Label skipTracingCall = method.DefineLabel(locals.GetLabelName());
                    method.LoadTxTracer(locals, false);
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

                method.LoadResult(locals, true);
                method.Duplicate();
                method.LoadLocal(locals.uint32A);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ExceptionType)));
                method.LoadConstant((int)ContractState.Failed);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                method.FakeBranch(escapeLabels.exitLabel);

                method.MarkLabel(happyPath);
                hasNoItemsToPush = method.DefineLabel(locals.GetLabelName());

                method.LoadLocalAddress(toPushToStack);
                method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.HasValue)).GetGetMethod());
                method.BranchIfFalse(hasNoItemsToPush);

                method.StackLoadPrevious(locals.stackHeadRef, contractMetadata.StackOffsets.GetValueOrDefault(pc, (short)0), index);
                method.LoadLocalAddress(toPushToStack);
                method.Call(typeof(UInt256?).GetProperty(nameof(Nullable<UInt256>.Value)).GetGetMethod());
                method.Call(Word.SetUInt256);

                method.MarkLabel(hasNoItemsToPush);

                skipStateMachineScheduling = method.DefineLabel(locals.GetLabelName());

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
                    EmitCallToEndInstructionTrace(method, locals.gasAvailable, locals);
                }

                method.LoadResult(locals, true);
                method.Duplicate();
                method.LoadLocal(newStateToExe);
                method.CastClass(typeof(EvmState));
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.CallResult)));
                method.LoadConstant((int)ContractState.Halted);
                method.StoreField(GetFieldInfo(typeof(ILChunkExecutionState), nameof(ILChunkExecutionState.ContractState)));
                method.Branch(escapeLabels.returnLabel);

                method.MarkLabel(skipStateMachineScheduling);

                toPushToStack.Dispose();
                newStateToExe.Dispose();
                return;
            default:
                method.FakeBranch(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
                return;
        }
    }
}
