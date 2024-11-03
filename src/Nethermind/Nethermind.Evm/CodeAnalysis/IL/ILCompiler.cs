// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Config;
using Nethermind.Evm.IL;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.State;
using Sigil;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Intrinsics;
using static Nethermind.Evm.IL.EmitExtensions;
using Label = Sigil.Label;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class ILCompiler
{
    public delegate void ExecuteSegment(ref ILEvmState vmstate, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, ITxTracer trace, byte[][] immediatesData);

    private const int VMSTATE_INDEX = 0;
    private const int BLOCKHASH_PROVIDER_INDEX = 1;
    private const int WORLD_STATE_INDEX = 2;
    private const int CODE_INFO_REPOSITORY_INDEX = 3;
    private const int SPEC_INDEX = 4;
    private const int TXTRACER_INDEX = 5;
    private const int IMMEDIATES_DATA_INDEX = 6;

    public class SegmentExecutionCtx
    {
        public string Name => PrecompiledSegment.Method.Name;
        public ExecuteSegment PrecompiledSegment;
        public byte[][] Data;
        public ushort[] JumpDestinations;
    }
    public static SegmentExecutionCtx CompileSegment(string segmentName, OpcodeInfo[] code, byte[][] data, IVMConfig config)
    {
        // code is optimistic assumes stack underflow and stack overflow to not occure (WE NEED EOF FOR THIS)
        // Note(Ayman) : What stops us from adopting stack analysis from EOF in ILVM?
        // Note(Ayman) : verify all endianness arguments and bytes

        Emit<ExecuteSegment> method = Emit<ExecuteSegment>.NewDynamicMethod(segmentName, doVerify: true, strictBranchVerification: true);

        ushort[] jumpdests = EmitSegmentBody(method, code, config.BakeInTracingInJitMode);
        ExecuteSegment dynEmitedDelegate = method.CreateDelegate();
        return new SegmentExecutionCtx
        {
            PrecompiledSegment = dynEmitedDelegate,
            Data = data,
            JumpDestinations = jumpdests
        };
    }

    private static ushort[] EmitSegmentBody(Emit<ExecuteSegment> method, OpcodeInfo[] code, bool bakeInTracerCalls)
    {
        using Local jmpDestination = method.DeclareLocal(typeof(int));
        using Local consumeJumpCondition = method.DeclareLocal(typeof(int));

        using Local address = method.DeclareLocal(typeof(Address));

        using Local hash256 = method.DeclareLocal(typeof(Hash256));

        using Local uint256A = method.DeclareLocal(typeof(UInt256));
        using Local uint256B = method.DeclareLocal(typeof(UInt256));
        using Local uint256C = method.DeclareLocal(typeof(UInt256));
        using Local uint256R = method.DeclareLocal(typeof(UInt256));

        using Local localReadOnlyMemory = method.DeclareLocal(typeof(ReadOnlyMemory<byte>));
        using Local localReadonOnlySpan = method.DeclareLocal(typeof(ReadOnlySpan<byte>));
        using Local localZeroPaddedSpan = method.DeclareLocal(typeof(ZeroPaddedSpan));
        using Local localSpan = method.DeclareLocal(typeof(Span<byte>));
        using Local localMemory = method.DeclareLocal(typeof(Memory<byte>));
        using Local localArray = method.DeclareLocal(typeof(byte[]));
        using Local uint64A = method.DeclareLocal(typeof(ulong));

        using Local uint32A = method.DeclareLocal(typeof(uint));
        using Local uint32B = method.DeclareLocal(typeof(uint));
        using Local int64A = method.DeclareLocal(typeof(long));
        using Local int64B = method.DeclareLocal(typeof(long));
        using Local byte8A = method.DeclareLocal(typeof(byte));
        using Local lbool = method.DeclareLocal(typeof(bool));
        using Local byte8B = method.DeclareLocal(typeof(byte));
        using Local buffer = method.DeclareLocal(typeof(byte*));

        using Local storageCell = method.DeclareLocal(typeof(StorageCell));

        using Local gasAvailable = method.DeclareLocal(typeof(long));
        using Local programCounter = method.DeclareLocal(typeof(ushort));

        using Local stack = method.DeclareLocal(typeof(Span<Word>));
        using Local head = method.DeclareLocal(typeof(int));


        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();

        foreach (var exception in Enum.GetValues<EvmExceptionType>())
        {
            evmExceptionLabels.Add(exception, method.DefineLabel());
        }

        Label exit = method.DefineLabel(); // the label just before return
        Label jumpTable = method.DefineLabel(); // jump table
        Label isContinuation = method.DefineLabel(); // jump table
        Label ret = method.DefineLabel();

        // allocate stack
        method.LoadArgument(VMSTATE_INDEX);
        method.Duplicate();
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Stack)));
        method.Call(GetCastMethodInfo<byte, Word>());
        method.StoreLocal(stack);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.StackHead)));
        method.StoreLocal(head);

        // set gas to local
        method.LoadArgument(VMSTATE_INDEX);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.GasAvailable)));
        method.Convert<long>();
        method.StoreLocal(gasAvailable);

        // set pc to local
        method.LoadArgument(VMSTATE_INDEX);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ProgramCounter)));
        method.StoreLocal(programCounter);

        // if last ilvmstate was a jump
        method.LoadLocal(programCounter);
        method.LoadConstant(code[0].ProgramCounter);
        method.CompareEqual();
        method.BranchIfFalse(isContinuation);

        Dictionary<ushort, Label> jumpDestinations = new();

        var costs = BuildCostLookup(code);

        // Idea(Ayman) : implement every opcode as a method, and then inline the IL of the method in the main method
        for (int i = 0; i < code.Length; i++)
        {
            OpcodeInfo op = code[i];
            if (op.Operation is Instruction.JUMPDEST)
            {
                // mark the jump destination
                jumpDestinations[op.ProgramCounter] = method.DefineLabel();
                method.MarkLabel(jumpDestinations[op.ProgramCounter]);
                method.LoadConstant(op.ProgramCounter);
                method.StoreLocal(programCounter);
            }


            if (bakeInTracerCalls)
            {
                EmitCallToStartInstructionTrace(method, gasAvailable, head, op);
            }

            // check if opcode is activated in current spec
            method.LoadArgument(SPEC_INDEX);
            method.LoadConstant((byte)op.Operation);
            method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
            method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.BadInstruction]);

            if (!bakeInTracerCalls) {
                if (costs.ContainsKey(op.ProgramCounter) && costs[op.ProgramCounter] > 0)
                {
                    method.LoadLocal(gasAvailable);
                    method.LoadConstant(costs[op.ProgramCounter]);
                    method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadLocal(gasAvailable);
                    method.LoadConstant(costs[op.ProgramCounter]);
                    method.Subtract();
                    method.StoreLocal(gasAvailable);
                }
            } else
            {
                method.LoadLocal(gasAvailable);
                method.LoadConstant(op.Metadata.GasCost);
                method.Subtract();
                method.Duplicate();
                method.StoreLocal(gasAvailable);
                method.LoadConstant((long)0);
                method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);
            }

            if (i == code.Length - 1)
            {
                method.LoadConstant(op.ProgramCounter + op.Metadata.AdditionalBytes);
                method.StoreLocal(programCounter);
            }

            if (op.Metadata.StackBehaviorPop > 0)
            {
                method.LoadLocal(head);
                method.LoadConstant(op.Metadata.StackBehaviorPop);
                method.BranchIfLess(evmExceptionLabels[EvmExceptionType.StackUnderflow]);
            }

            if (op.Metadata.StackBehaviorPush > 0)
            {
                int delta = op.Metadata.StackBehaviorPush - op.Metadata.StackBehaviorPop;
                method.LoadLocal(head);
                method.LoadConstant(delta);
                method.Add();
                method.LoadConstant(EvmStack.MaxStackSize);
                method.BranchIfGreaterOrEqual(evmExceptionLabels[EvmExceptionType.StackOverflow]);
            }

            // else emit
            switch (op.Operation)
            {
                case Instruction.JUMPDEST:
                    // we do nothing
                    break;
                case Instruction.STOP:
                    {
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadConstant(true);
                        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ShouldStop)));
                        method.FakeBranch(ret);
                    }
                    break;
                case Instruction.CHAINID:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ChainId)));
                        method.Call(Word.SetULong0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.NOT:
                    {
                        var refWordToRefByteMethod = GetAsMethodInfo<Word, byte>();
                        var readVector256Method = GetReadUnalignedMethodInfo<Vector256<byte>>();
                        var writeVector256Method = GetWriteUnalignedMethodInfo<Vector256<byte>>();
                        var notVector256Method = typeof(Vector256)
                            .GetMethod(nameof(Vector256.OnesComplement), BindingFlags.Public | BindingFlags.Static)!
                            .MakeGenericMethod(typeof(byte));

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(refWordToRefByteMethod);
                        method.Duplicate();
                        method.Call(readVector256Method);
                        method.Call(notVector256Method);
                        method.Call(writeVector256Method);
                    }
                    break;
                case Instruction.JUMP:
                    {
                        // we jump into the jump table
                        if (bakeInTracerCalls)
                        {
                            EmitCallToEndInstructionTrace(method, gasAvailable);
                        }
                        method.FakeBranch(jumpTable);
                    }
                    break;
                case Instruction.JUMPI:
                    {// consume the jump condition
                        Label noJump = method.DefineLabel();
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetIsZero);
                        // if the jump condition is false, we do not jump
                        method.BranchIfTrue(noJump);

                        // load the jump address
                        method.LoadConstant(1);
                        method.StoreLocal(consumeJumpCondition);

                        // we jump into the jump table

                        if (bakeInTracerCalls)
                        {
                            EmitCallToEndInstructionTrace(method, gasAvailable);
                        }
                        method.Branch(jumpTable);

                        method.MarkLabel(noJump);
                        method.StackPop(head, 2);
                    }
                    break;
                case Instruction.PUSH0:
                    {
                        method.CleanWord(stack, head);
                        method.StackPush(head);
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
                    {// we load the stack
                        method.CleanAndLoadWord(stack, head);

                        // we load the span of bytes
                        method.LoadArgument(IMMEDIATES_DATA_INDEX);
                        method.LoadConstant(op.Arguments.Value);
                        method.LoadElement<byte[]>();
                        method.Call(Word.SetArray);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.ADD:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.SUB:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.MUL:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, null, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.MOD:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label = il.DefineLabel();

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZero), false, out _));
                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Call(ConvertionExplicit<UInt256, int>());
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.SMOD:
                    EmitBinaryInt256Method(method, uint256R, (stack, head), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Mod), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label bIsNotZeroOrOneLabel = il.DefineLabel();

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZeroOrOne), false, out _));
                            il.BranchIfFalse(bIsNotZeroOrOneLabel);

                            il.LoadField(GetFieldInfo<UInt256>(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(bIsNotZeroOrOneLabel);
                        }, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.DIV:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label = il.DefineLabel();

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZero), false, out _));
                            il.BranchIfFalse(label);

                            il.LoadField(GetFieldInfo<UInt256>(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.SDIV:
                    EmitBinaryInt256Method(method, uint256R, (stack, head), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.Divide), BindingFlags.Public | BindingFlags.Static, [typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType(), typeof(Int256.Int256).MakeByRefType()])!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label bIsNotZero = il.DefineLabel();
                            Label bIsNotMinusOneLabel = il.DefineLabel();

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZero), false, out _));
                            il.BranchIfFalse(bIsNotZero);

                            il.LoadField(typeof(UInt256).GetField(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(bIsNotZero);

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
                            il.LoadFieldAddress(typeof(Int256.Int256).GetField(nameof(Int256.Int256.MinusOne), BindingFlags.Static | BindingFlags.Public));
                            il.Call(typeof(Int256.Int256).GetMethod("op_Equality"));

                            il.LoadLocalAddress(locals[0]);
                            il.Call(typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.P255), BindingFlags.Static | BindingFlags.NonPublic).GetMethod);
                            il.Call(typeof(UInt256).GetMethod("op_Equality", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }));
                            il.And();
                            il.BranchIfFalse(bIsNotMinusOneLabel);

                            il.Call(typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.P255), BindingFlags.Static | BindingFlags.NonPublic).GetMethod);
                            il.LoadObject(typeof(UInt256));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(bIsNotMinusOneLabel);
                        }, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.ADDMOD:
                    EmitTrinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.AddMod), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label cIsNotZeroLabel = il.DefineLabel();

                            il.LoadLocalAddress(locals[2]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZeroOrOne), false, out _));
                            il.BranchIfFalse(cIsNotZeroLabel);

                            il.LoadField(GetFieldInfo<UInt256>(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(cIsNotZeroLabel);
                        }, evmExceptionLabels, uint256A, uint256B, uint256C);
                    break;
                case Instruction.MULMOD:
                    EmitTrinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.MultiplyMod), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label cIsNotZeroLabel = il.DefineLabel();

                            il.LoadLocalAddress(locals[2]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZeroOrOne), false, out _));
                            il.BranchIfFalse(cIsNotZeroLabel);

                            il.LoadField(GetFieldInfo<UInt256>(nameof(UInt256.Zero), BindingFlags.Static | BindingFlags.Public));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(cIsNotZeroLabel);
                        }, evmExceptionLabels, uint256A, uint256B, uint256C);
                    break;
                case Instruction.SHL:
                    EmitShiftUInt256Method(method, uint256R, (stack, head), isLeft: true, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.SHR:
                    EmitShiftUInt256Method(method, uint256R, (stack, head), isLeft: false, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.SAR:
                    EmitShiftInt256Method(method, uint256R, (stack, head), evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.AND:
                    EmitBitwiseUInt256Method(method, uint256R, (stack, head), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseAnd), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                    break;
                case Instruction.OR:
                    EmitBitwiseUInt256Method(method, uint256R, (stack, head), typeof(Vector256).GetMethod(nameof(Vector256.BitwiseOr), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                    break;
                case Instruction.XOR:
                    EmitBitwiseUInt256Method(method, uint256R, (stack, head), typeof(Vector256).GetMethod(nameof(Vector256.Xor), BindingFlags.Public | BindingFlags.Static)!, evmExceptionLabels);
                    break;
                case Instruction.EXP:
                    {
                        Label powerIsZero = method.DefineLabel();
                        Label baseIsOneOrZero = method.DefineLabel();
                        Label endOfExpImpl = method.DefineLabel();

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Duplicate();
                        method.Call(Word.LeadingZeroProp);
                        method.StoreLocal(uint64A);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);
                        method.StackPop(head, 2);

                        method.LoadLocalAddress(uint256B);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(powerIsZero);

                        // load spec
                        method.LoadLocal(gasAvailable);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExpByteCost)));
                        method.LoadConstant((long)32);
                        method.LoadLocal(uint64A);
                        method.Subtract();
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadLocalAddress(uint256A);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZeroOrOne)).GetMethod!);
                        method.BranchIfTrue(baseIsOneOrZero);

                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256B);
                        method.LoadLocalAddress(uint256R);
                        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocal(uint256R);
                        method.Call(Word.SetUInt256);

                        method.Branch(endOfExpImpl);

                        method.MarkLabel(powerIsZero);
                        method.CleanAndLoadWord(stack, head);
                        method.LoadConstant(1);
                        method.Call(Word.SetUInt0);
                        method.Branch(endOfExpImpl);

                        method.MarkLabel(baseIsOneOrZero);
                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocal(uint256A);
                        method.Call(Word.SetUInt256);
                        method.Branch(endOfExpImpl);

                        method.MarkLabel(endOfExpImpl);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.LT:
                    EmitComparaisonUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.GT:
                    EmitComparaisonUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.SLT:
                    EmitComparaisonInt256Method(method, uint256R, (stack, head), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), false, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.SGT:
                    EmitComparaisonInt256Method(method, uint256R, (stack, head), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), true, evmExceptionLabels, uint256A, uint256B);
                    break;
                case Instruction.EQ:
                    {
                        var refWordToRefByteMethod = GetAsMethodInfo<Word, byte>();
                        var readVector256Method = GetReadUnalignedMethodInfo<Vector256<byte>>();
                        var writeVector256Method = GetWriteUnalignedMethodInfo<Vector256<byte>>();
                        var operationUnegenerified = typeof(Vector256).GetMethod(nameof(Vector256.EqualsAll), BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(typeof(byte));

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(refWordToRefByteMethod);
                        method.Call(readVector256Method);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(refWordToRefByteMethod);
                        method.Call(readVector256Method);
                        method.StackPop(head, 2);

                        method.Call(operationUnegenerified);
                        method.StoreLocal(lbool);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocal(lbool);
                        method.Convert<uint>();
                        method.Call(Word.SetUInt0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.ISZERO:
                    {// we load the stack
                        method.StackLoadPrevious(stack, head, 1);
                        method.Duplicate();
                        method.Call(Word.GetIsZero);
                        method.Call(Word.SetByte0);
                    }
                    break;
                case Instruction.POP:
                    {
                        method.StackPop(head);
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
                        int count = (int)op.Operation - (int)Instruction.DUP1 + 1;
                        method.Load(stack, head);
                        method.StackLoadPrevious(stack, head, count);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));
                        method.StackPush(head);
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
                        var count = (int)op.Operation - (int)Instruction.SWAP1 + 1;

                        method.LoadLocalAddress(uint256R);
                        method.StackLoadPrevious(stack, head, 1);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));

                        method.StackLoadPrevious(stack, head, 1);
                        method.StackLoadPrevious(stack, head, count + 1);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));

                        method.StackLoadPrevious(stack, head, count + 1);
                        method.LoadLocalAddress(uint256R);
                        method.LoadObject(typeof(Word));
                        method.StoreObject(typeof(Word));
                    }
                    break;
                case Instruction.CODESIZE:
                    {
                        var lastOpcode = code[^1];
                        method.CleanAndLoadWord(stack, head);
                        method.LoadConstant(lastOpcode.ProgramCounter + lastOpcode.Metadata.AdditionalBytes + 1);
                        method.Call(Word.SetInt0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.PC:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadConstant((uint)op.ProgramCounter);
                        method.Call(Word.SetUInt0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.COINBASE:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
                        method.Call(Word.SetAddress);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.TIMESTAMP:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
                        method.Call(Word.SetULong0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.NUMBER:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
                        method.Call(Word.SetULong0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.GASLIMIT:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                        method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasLimit), false, out _));
                        method.Call(Word.SetULong0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.CALLER:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
                        method.Call(Word.SetAddress);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.ADDRESS:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.Call(Word.SetAddress);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.ORIGIN:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                        method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin), false, out _));
                        method.Call(Word.SetAddress);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.CALLVALUE:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                        method.Call(Word.SetUInt256);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.GASPRICE:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                        method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice), false, out _));
                        method.Call(Word.SetUInt256);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.CALLDATACOPY:
                    {
                        Label endOfOpcode = method.DefineLabel();

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);
                        method.StackLoadPrevious(stack, head, 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256C);
                        method.StackPop(head, 3);

                        method.LoadLocal(gasAvailable);
                        method.LoadLocalAddress(uint256C);
                        method.LoadLocalAddress(lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Memory);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(endOfOpcode);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);


                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.InputBuffer)));
                        method.LoadObject(typeof(ReadOnlyMemory<byte>));
                        method.LoadLocalAddress(uint256B);
                        method.LoadLocal(uint256C);
                        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                        method.Convert<int>();
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.StoreLocal(localZeroPaddedSpan);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(localZeroPaddedSpan);
                        method.CallVirtual(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                        method.MarkLabel(endOfOpcode);
                    }
                    break;
                case Instruction.CALLDATALOAD:
                    {
                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackPop(head, 1);

                        method.CleanAndLoadWord(stack, head);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.InputBuffer)));
                        method.LoadObject(typeof(ReadOnlyMemory<byte>));

                        method.LoadLocalAddress(uint256A);
                        method.LoadConstant(Word.Size);
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.LoadField(GetFieldInfo(typeof(ZeroPaddedSpan), nameof(ZeroPaddedSpan.Span)));
                        method.Call(Word.SetSpan);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.CALLDATASIZE:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.InputBuffer)));
                        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                        method.Call(Word.SetInt0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.MSIZE:
                    {
                        method.CleanAndLoadWord(stack, head);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
                        method.Call(Word.SetULong0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.MSTORE:
                    {
                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);
                        method.StackPop(head, 2);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadConstant(Word.Size);
                        method.Call(ConvertionExplicit<UInt256, int>());
                        method.StoreLocal(uint256C);
                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256B);
                        method.LoadConstant(Word.Size);
                        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.PaddedBytes)));
                        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(byte[])));
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
                    }
                    break;
                case Instruction.MSTORE8:
                    {
                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetByte0);
                        method.StoreLocal(byte8A);
                        method.StackPop(head, 2);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadConstant(1);
                        method.Call(ConvertionExplicit<UInt256, int>());
                        method.StoreLocal(uint256C);
                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocal(byte8A);

                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
                    }
                    break;
                case Instruction.MLOAD:
                    {
                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackPop(head, 1);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadFieldAddress(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BigInt32)));
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
                        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                        method.StoreLocal(localReadonOnlySpan);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocalAddress(localReadonOnlySpan);
                        method.LoadConstant(BitConverter.IsLittleEndian);
                        method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));
                        method.Call(Word.SetUInt256);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.MCOPY:
                    {
                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);

                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);

                        method.StackLoadPrevious(stack, head, 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256C);

                        method.StackPop(head, 3);

                        method.LoadLocal(gasAvailable);
                        method.LoadLocalAddress(uint256C);
                        method.LoadLocalAddress(lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.VeryLow);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256B);
                        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Max)));
                        method.StoreLocal(uint256R);
                        method.LoadLocalAddress(uint256R);
                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256B);
                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
                    }
                    break;
                case Instruction.KECCAK256:
                    {
                        var refWordToRefValueHashMethod = GetAsMethodInfo<Word, ValueHash256>();

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);

                        method.LoadLocal(gasAvailable);
                        method.LoadLocalAddress(uint256B);
                        method.LoadLocalAddress(lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Sha3Word);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256B);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);


                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256B);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(refWordToRefValueHashMethod);
                        method.Call(typeof(KeccakCache).GetMethod(nameof(KeccakCache.ComputeTo), [typeof(ReadOnlySpan<byte>), typeof(ValueHash256).MakeByRefType()]));
                        method.StackPop(head);
                    }
                    break;
                case Instruction.BYTE:
                    {// load a
                        method.StackLoadPrevious(stack, head, 1);
                        method.Duplicate();
                        method.Call(Word.GetUInt0);
                        method.StoreLocal(uint32A);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetSpan);
                        method.StoreLocal(localReadonOnlySpan);
                        method.StackPop(head, 2);


                        Label pushZeroLabel = method.DefineLabel();
                        Label endOfInstructionImpl = method.DefineLabel();
                        method.LoadLocalAddress(uint256A);
                        method.LoadConstant(Word.Size - 1);
                        method.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                        method.LoadLocalAddress(uint256A);
                        method.LoadConstant(0);
                        method.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                        method.Or();
                        method.BranchIfTrue(pushZeroLabel);

                        method.LoadLocalAddress(localReadonOnlySpan);
                        method.LoadLocal(uint32A);
                        method.Call(typeof(ReadOnlySpan<byte>).GetMethod("get_Item"));
                        method.LoadIndirect<byte>();
                        method.Convert<uint>();
                        method.StoreLocal(uint32A);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocal(uint32A);
                        method.Call(Word.SetUInt0);
                        method.StackPush(head);
                        method.Branch(endOfInstructionImpl);

                        method.MarkLabel(pushZeroLabel);
                        method.CleanWord(stack, head);
                        method.StackPush(head);

                        method.MarkLabel(endOfInstructionImpl);
                    }
                    break;
                case Instruction.CODECOPY:
                    {
                        var endOfOpcode = method.DefineLabel();

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);
                        method.StackLoadPrevious(stack, head, 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256C);
                        method.StackPop(head, 3);

                        method.LoadLocal(gasAvailable);
                        method.LoadLocalAddress(uint256C);
                        method.LoadLocalAddress(lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Memory);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(endOfOpcode);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.MachineCode)));
                        method.StoreLocal(localReadOnlyMemory);

                        method.LoadLocal(localReadOnlyMemory);
                        method.LoadLocalAddress(uint256B);
                        method.LoadLocalAddress(uint256C);
                        method.Call(MethodInfo<UInt256>("op_Explicit", typeof(Int32), new[] { typeof(UInt256).MakeByRefType() }));
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.StoreLocal(localZeroPaddedSpan);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(localZeroPaddedSpan);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                        method.MarkLabel(endOfOpcode);
                    }
                    break;
                case Instruction.GAS:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocal(gasAvailable);
                        method.Call(Word.SetULong0);

                        method.StackPush(head);
                    }
                    break;
                case Instruction.RETURNDATASIZE:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ReturnBuffer)));
                        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                        method.Call(Word.SetInt0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.RETURNDATACOPY:
                    {
                        var endOfOpcode = method.DefineLabel();
                        using Local tempResult = method.DeclareLocal(typeof(UInt256));


                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);
                        method.StackLoadPrevious(stack, head, 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256C);
                        method.StackPop(head, 3);

                        method.LoadLocalAddress(uint256B);
                        method.LoadLocalAddress(uint256C);
                        method.LoadLocalAddress(tempResult);
                        method.Call(typeof(UInt256).GetMethod(nameof(UInt256.AddOverflow)));
                        method.LoadLocalAddress(tempResult);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ReturnBuffer)));
                        method.Call(typeof(ReadOnlyMemory<byte>).GetProperty(nameof(ReadOnlyMemory<byte>.Length)).GetMethod!);
                        method.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
                        method.Or();
                        method.BranchIfTrue(evmExceptionLabels[EvmExceptionType.AccessViolation]);


                        method.LoadLocal(gasAvailable);
                        method.LoadLocalAddress(uint256C);
                        method.LoadLocalAddress(lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Memory);
                        method.Multiply();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        // Note : check if c + b > returnData.Size

                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(endOfOpcode);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ReturnBuffer)));
                        method.LoadObject(typeof(ReadOnlyMemory<byte>));
                        method.LoadLocalAddress(uint256B);
                        method.LoadLocalAddress(uint256C);
                        method.Call(MethodInfo<UInt256>("op_Explicit", typeof(Int32), new[] { typeof(UInt256).MakeByRefType() }));
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.StoreLocal(localZeroPaddedSpan);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(localZeroPaddedSpan);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                        method.MarkLabel(endOfOpcode);
                    }
                    break;
                case Instruction.RETURN or Instruction.REVERT:
                    {
                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);
                        method.StackPop(head, 2);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256B);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ReturnBuffer)));
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256B);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Load), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                        method.StoreObject<ReadOnlyMemory<byte>>();

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadConstant(true);
                        switch (op.Operation)
                        {
                            case Instruction.REVERT:
                                method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ShouldRevert)));
                                break;
                            case Instruction.RETURN:
                                method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ShouldReturn)));
                                break;
                        }
                        method.FakeBranch(ret);
                    }
                    break;
                case Instruction.BASEFEE:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                        method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.BaseFeePerGas), false, out _));
                        method.Call(Word.SetUInt256);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.BLOBBASEFEE:
                    {
                        using Local uint256Nullable = method.DeclareLocal(typeof(UInt256?));
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.BlobBaseFee), false, out _));
                        method.StoreLocal(uint256Nullable);
                        method.LoadLocalAddress(uint256Nullable);
                        method.Call(GetPropertyInfo(typeof(UInt256?), nameof(Nullable<UInt256>.Value), false, out _));
                        method.Call(Word.SetUInt256);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.PREVRANDAO:
                    {
                        Label isPostMergeBranch = method.DefineLabel();
                        Label endOfOpcode = method.DefineLabel();
                        method.CleanAndLoadWord(stack, head);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
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
                        method.StackPush(head);
                    }
                    break;
                case Instruction.BLOBHASH:
                    {
                        Label blobVersionedHashNotFound = method.DefineLabel();
                        Label endOfOpcode = method.DefineLabel();

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetInt0);
                        method.StoreLocal(uint32A);
                        method.StackPop(head, 1);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                        method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                        method.LoadNull();
                        method.BranchIfEqual(blobVersionedHashNotFound);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                        method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                        method.Call(GetPropertyInfo(typeof(byte[][]), nameof(Array.Length), false, out _));
                        method.LoadLocal(uint32A);
                        method.BranchIfLessOrEqual(blobVersionedHashNotFound);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                        method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                        method.LoadLocal(uint32A);
                        method.LoadElement<Byte[]>();
                        method.StoreLocal(localArray);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocal(localArray);
                        method.Call(Word.SetArray);
                        method.Branch(endOfOpcode);

                        method.MarkLabel(blobVersionedHashNotFound);
                        method.CleanWord(stack, head);

                        method.MarkLabel(endOfOpcode);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.BLOCKHASH:
                    {
                        Label blockHashReturnedNull = method.DefineLabel();
                        Label pushToStackRegion = method.DefineLabel();

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt0);
                        method.Convert<long>();
                        method.LoadConstant(long.MaxValue);
                        method.Call(typeof(Math).GetMethod(nameof(Math.Min), [typeof(long), typeof(long)]));
                        method.StoreLocal(int64A);
                        method.StackPop(head, 1);

                        method.LoadArgument(BLOCKHASH_PROVIDER_INDEX);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                        method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                        method.LoadLocalAddress(int64A);
                        method.CallVirtual(typeof(IBlockhashProvider).GetMethod(nameof(IBlockhashProvider.GetBlockhash), [typeof(BlockHeader), typeof(long).MakeByRefType()]));
                        method.Duplicate();
                        method.StoreLocal(hash256);
                        method.LoadNull();
                        method.BranchIfEqual(blockHashReturnedNull);

                        // not equal
                        method.LoadLocal(hash256);
                        method.Call(GetPropertyInfo(typeof(Hash256), nameof(Hash256.Bytes), false, out _));
                        method.Call(ConvertionImplicit(typeof(Span<byte>), typeof(Span<byte>)));
                        method.StoreLocal(localReadonOnlySpan);
                        method.Branch(pushToStackRegion);
                        // equal to null

                        method.MarkLabel(blockHashReturnedNull);

                        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesZero32)));
                        method.Call(ConvertionImplicit(typeof(ReadOnlySpan<byte>), typeof(byte[])));
                        method.StoreLocal(localReadonOnlySpan);

                        method.MarkLabel(pushToStackRegion);
                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocalAddress(localReadonOnlySpan);
                        method.LoadConstant(BitConverter.IsLittleEndian);
                        method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));
                        method.Call(Word.SetUInt256);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.SIGNEXTEND:
                    {
                        Label signIsNegative = method.DefineLabel();
                        Label endOfOpcodeHandling = method.DefineLabel();
                        Label argumentGt32 = method.DefineLabel();

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt0);
                        method.StoreLocal(uint32A);

                        method.LoadLocal(uint32A);
                        method.LoadConstant(32);
                        method.BranchIfGreaterOrEqual(argumentGt32);

                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetArray);
                        method.StoreLocal(localArray);

                        method.LoadConstant((uint)31);
                        method.LoadLocal(uint32A);
                        method.Subtract();
                        method.StoreLocal(uint32A);

                        method.LoadLocal(localArray);
                        method.LoadLocal(uint32A);
                        method.LoadElement<byte>();
                        method.Convert<sbyte>();
                        method.LoadConstant((sbyte)0);
                        method.BranchIfLess(signIsNegative);

                        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesZero32)));
                        method.Branch(endOfOpcodeHandling);

                        method.MarkLabel(signIsNegative);
                        method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesMax32)));

                        method.MarkLabel(endOfOpcodeHandling);
                        method.LoadConstant(0);
                        method.LoadLocal(uint32A);
                        method.EmitAsSpan();
                        method.StoreLocal(localSpan);

                        using Local tempLocalSpan = method.DeclareLocal(typeof(Span<byte>));
                        method.LoadLocal(localArray);
                        method.Duplicate();
                        method.Call(GetPropertyInfo(typeof(byte[]), nameof(Array.Length), false, out _));
                        method.StoreLocal(uint32B);
                        method.LoadConstant(0);
                        method.LoadLocal(uint32B);
                        method.EmitAsSpan();
                        method.StoreLocal(tempLocalSpan);

                        method.LoadLocalAddress(localSpan);
                        method.LoadLocal(tempLocalSpan);
                        method.Call(typeof(Span<byte>).GetMethod(nameof(Span<byte>.CopyTo), [typeof(Span<byte>)]));
                        method.MarkLabel(argumentGt32);
                        method.StackPop(head, 1);
                    }
                    break;
                case Instruction.LOG0:
                case Instruction.LOG1:
                case Instruction.LOG2:
                case Instruction.LOG3:
                case Instruction.LOG4:
                    {
                        sbyte topicsCount = (sbyte)(op.Operation - Instruction.LOG0);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                        method.BranchIfTrue(evmExceptionLabels[EvmExceptionType.StaticCallViolation]);

                        EmitLogMethod(method, (stack, head), topicsCount, evmExceptionLabels, uint256A, uint256B, int64A, gasAvailable, hash256, localReadOnlyMemory);
                    }
                    break;
                case Instruction.TSTORE:
                    {
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
                        method.BranchIfTrue(evmExceptionLabels[EvmExceptionType.StaticCallViolation]);

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);

                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetArray);
                        method.StoreLocal(localArray);

                        method.StackPop(head, 2);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.LoadLocalAddress(uint256A);
                        method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                        method.StoreLocal(storageCell);

                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocalAddress(storageCell);
                        method.LoadLocal(localArray);
                        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.SetTransientState), [typeof(StorageCell).MakeByRefType(), typeof(byte[])]));
                    }
                    break;
                case Instruction.TLOAD:
                    {
                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackPop(head, 1);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.LoadLocalAddress(uint256A);
                        method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                        method.StoreLocal(storageCell);

                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocalAddress(storageCell);
                        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.GetTransientState), [typeof(StorageCell).MakeByRefType()]));
                        method.StoreLocal(localReadonOnlySpan);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocal(localReadonOnlySpan);
                        method.Call(Word.SetSpan);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.SSTORE:
                    {
                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetSpan);
                        method.StoreLocal(localReadonOnlySpan);
                        method.StackPop(head, 2);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(localReadonOnlySpan);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));

                        MethodInfo sstoreMethod =
                            typeof(VirtualMachine<VirtualMachine.NotTracing>)
                                .GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.InstructionSStore), BindingFlags.Static | BindingFlags.NonPublic)
                                .MakeGenericMethod(typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing), typeof(VirtualMachine.NotTracing));
                        method.Call(sstoreMethod);

                        Label endOfOpcode = method.DefineLabel();
                        method.Duplicate();
                        method.StoreLocal(uint32A);
                        method.LoadConstant((int)EvmExceptionType.None);
                        method.BranchIfEqual(endOfOpcode);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadLocal(uint32A);
                        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmException)));
                        method.Branch(exit);

                        method.MarkLabel(endOfOpcode);
                    }
                    break;
                case Instruction.SLOAD:
                    {
                        method.LoadLocal(gasAvailable);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetSLoadCost)));
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackPop(head, 1);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.LoadLocalAddress(uint256A);
                        method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                        method.StoreLocal(storageCell);

                        method.LoadLocalAddress(gasAvailable);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(storageCell);
                        method.LoadConstant((int)VirtualMachine<VirtualMachine.NotTracing>.StorageAccessType.SLOAD);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeStorageAccessGas), BindingFlags.Static | BindingFlags.NonPublic));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocalAddress(storageCell);
                        method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.Get), [typeof(StorageCell).MakeByRefType()]));
                        method.StoreLocal(localReadonOnlySpan);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadLocal(localReadonOnlySpan);
                        method.Call(Word.SetSpan);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.EXTCODESIZE:
                    {
                        method.LoadLocal(gasAvailable);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(address);
                        method.StackPop(head, 1);

                        method.LoadLocalAddress(gasAvailable);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocal(address);
                        method.LoadConstant(true);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                        method.LoadConstant(true);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.CleanAndLoadWord(stack, head);

                        method.LoadArgument(CODE_INFO_REPOSITORY_INDEX);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocal(address);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                        method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));
                        method.StoreLocal(localReadOnlyMemory);
                        method.LoadLocalAddress(localReadOnlyMemory);
                        method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));

                        method.Call(Word.SetInt0);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.EXTCODECOPY:
                    {
                        Label endOfOpcode = method.DefineLabel();

                        method.StackLoadPrevious(stack, head, 4);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256C);

                        method.LoadLocal(gasAvailable);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                        method.LoadLocalAddress(uint256C);
                        method.LoadLocalAddress(lbool);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling), [typeof(UInt256).MakeByRefType(), typeof(bool).MakeByRefType()]));
                        method.LoadConstant(GasCostOf.Memory);
                        method.Multiply();
                        method.Add();
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(address);
                        method.StackLoadPrevious(stack, head, 2);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256A);
                        method.StackLoadPrevious(stack, head, 3);
                        method.Call(Word.GetUInt256);
                        method.StoreLocal(uint256B);
                        method.StackPop(head, 4);

                        method.LoadLocalAddress(gasAvailable);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocal(address);
                        method.LoadConstant(true);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                        method.LoadConstant(true);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                        method.BranchIfTrue(endOfOpcode);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocalAddress(gasAvailable);
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(uint256C);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(CODE_INFO_REPOSITORY_INDEX);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocal(address);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(typeof(CodeInfoRepositoryExtensions).GetMethod(nameof(CodeInfoRepositoryExtensions.GetCachedCodeInfo), [typeof(ICodeInfoRepository), typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                        method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));

                        method.LoadLocalAddress(uint256B);
                        method.LoadLocal(uint256C);
                        method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                        method.Convert<int>();
                        method.LoadConstant((int)PadDirection.Right);
                        method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                        method.StoreLocal(localZeroPaddedSpan);

                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                        method.LoadLocalAddress(uint256A);
                        method.LoadLocalAddress(localZeroPaddedSpan);
                        method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                        method.MarkLabel(endOfOpcode);
                    }
                    break;
                case Instruction.EXTCODEHASH:
                    {
                        Label endOfOpcode = method.DefineLabel();

                        method.LoadLocal(gasAvailable);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeHashCost)));
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(address);
                        method.StackPop(head, 1);

                        method.LoadLocalAddress(gasAvailable);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocal(address);
                        method.LoadConstant(true);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                        method.LoadConstant(true);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocal(address);
                        method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IWorldState.AccountExists)));
                        method.LoadConstant(false);
                        method.CompareEqual();
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocal(address);
                        method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IWorldState.IsDeadAccount)));
                        method.Or();
                        method.BranchIfTrue(endOfOpcode);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocal(address);
                        method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetCodeHash)));
                        method.Call(Word.SetKeccak);
                        method.MarkLabel(endOfOpcode);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.SELFBALANCE:
                    {
                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                        method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                        method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                        method.Call(Word.SetUInt256);
                        method.StackPush(head);
                    }
                    break;
                case Instruction.BALANCE:
                    {
                        method.LoadLocal(gasAvailable);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetBalanceCost)));
                        method.Subtract();
                        method.Duplicate();
                        method.StoreLocal(gasAvailable);
                        method.LoadConstant((long)0);
                        method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.StackLoadPrevious(stack, head, 1);
                        method.Call(Word.GetAddress);
                        method.StoreLocal(address);
                        method.StackPop(head, 1);

                        method.LoadLocalAddress(gasAvailable);
                        method.LoadArgument(VMSTATE_INDEX);
                        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                        method.LoadLocal(address);
                        method.LoadConstant(false);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadArgument(SPEC_INDEX);
                        method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                        method.LoadConstant(true);
                        method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas)));
                        method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                        method.CleanAndLoadWord(stack, head);
                        method.LoadArgument(WORLD_STATE_INDEX);
                        method.LoadLocal(address);
                        method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                        method.Call(Word.SetUInt256);
                        method.StackPush(head);
                    }
                    break;
                default:
                    {
                        method.FakeBranch(evmExceptionLabels[EvmExceptionType.BadInstruction]);
                    }
                    break;
            }

            if (bakeInTracerCalls)
            {
                EmitCallToEndInstructionTrace(method, gasAvailable);
            }
        }

        Label skipProgramCounterSetting = method.DefineLabel();
        Local isEphemeralJump = method.DeclareLocal<bool>();
        // prepare ILEvmState
        // check if returnState is null
        method.MarkLabel(ret);
        // we get stack size
        method.LoadArgument(VMSTATE_INDEX);
        method.LoadLocal(head);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.StackHead)));

        // set stack
        method.LoadArgument(VMSTATE_INDEX);
        method.LoadLocal(stack);
        method.Call(GetCastMethodInfo<Word, byte>());
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Stack)));

        // set gas available
        method.LoadArgument(VMSTATE_INDEX);
        method.LoadLocal(gasAvailable);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.GasAvailable)));

        // set program counter
        method.LoadLocal(isEphemeralJump);
        method.BranchIfTrue(skipProgramCounterSetting);

        method.LoadArgument(VMSTATE_INDEX);
        method.LoadLocal(programCounter);
        method.LoadConstant(1);
        method.Add();
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ProgramCounter)));

        method.MarkLabel(skipProgramCounterSetting);


        // set exception
        method.LoadArgument(VMSTATE_INDEX);
        method.LoadConstant((int)EvmExceptionType.None);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmException)));

        // go to return
        method.Branch(exit);

        Label jumpIsLocal = method.DefineLabel();
        Label jumpIsNotLocal = method.DefineLabel();

        // isContinuation
        method.MarkLabel(isContinuation);
        method.LoadLocal(programCounter);
        method.StoreLocal(jmpDestination);
        method.Branch(jumpIsLocal);

        // jump table
        method.MarkLabel(jumpTable);
        method.StackLoadPrevious(stack, head, 1);
        method.Call(Word.GetInt0);
        method.StoreLocal(jmpDestination);
        method.StackPop(head);

        method.StackPop(head, consumeJumpCondition);
        method.LoadConstant(0);
        method.StoreLocal(consumeJumpCondition);

        //check if jump crosses segment boundaies
        int maxJump = code[^1].ProgramCounter + code[^1].Metadata.AdditionalBytes;
        int minJump = code[0].ProgramCounter;

        // if (jumpDest <= maxJump)
        method.LoadLocal(jmpDestination);
        method.LoadConstant(maxJump);
        method.BranchIfGreater(jumpIsNotLocal);

        // if (jumpDest >= minJump)
        method.LoadLocal(jmpDestination);
        method.LoadConstant(minJump);
        method.BranchIfLess(jumpIsNotLocal);

        method.Branch(jumpIsLocal);

        method.MarkLabel(jumpIsNotLocal);
        method.LoadArgument(VMSTATE_INDEX);
        method.Duplicate();
        method.LoadConstant(true);
        method.StoreLocal(isEphemeralJump);
        method.LoadConstant(true);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ShouldJump)));
        method.LoadLocal(jmpDestination);
        method.Convert<ushort>();
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ProgramCounter)));
        method.Branch(ret);

        method.MarkLabel(jumpIsLocal);


        // if (jumpDest > uint.MaxValue)
        method.LoadConstant(uint.MaxValue);
        method.LoadLocal(jmpDestination);
        // goto invalid address
        method.BranchIfGreater(evmExceptionLabels[EvmExceptionType.InvalidJumpDestination]);
        // else

        const int length = 1 << 8;
        const int bitMask = length - 1; // 128
        Label[] jumps = new Label[length];
        for (int i = 0; i < length; i++)
        {
            jumps[i] = method.DefineLabel();
        }

        // we get first Word.Size bits of the jump destination since it is less than int.MaxValue

        method.LoadLocal(jmpDestination);
        method.LoadConstant(bitMask);
        method.And();


        // switch on the first 7 bits
        method.Switch(jumps);

        for (int i = 0; i < length; i++)
        {
            method.MarkLabel(jumps[i]);
            // for each destination matching the bit mask emit check for the equality
            foreach (ushort dest in jumpDestinations.Keys.Where(dest => (dest & bitMask) == i))
            {
                method.LoadLocal(jmpDestination);
                method.LoadConstant(dest);
                method.Duplicate();
                method.StoreLocal(uint32A);
                method.BranchIfEqual(jumpDestinations[dest]);
            }
            // each bucket ends with a jump to invalid access to do not fall through to another one
            method.Branch(evmExceptionLabels[EvmExceptionType.InvalidJumpDestination]);
        }

        foreach (var kvp in evmExceptionLabels)
        {
            method.MarkLabel(kvp.Value);
            if(bakeInTracerCalls)
            {
                EmitCallToErrorTrace(method, gasAvailable, kvp);
            }

            method.LoadArgument(VMSTATE_INDEX);
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmException)));
            method.Branch(exit);
        }

        // return
        method.MarkLabel(exit);
        method.Return();

        return jumpDestinations.Keys.ToArray();
    }

    private static void EmitCallToErrorTrace(Emit<ExecuteSegment> method, Local gasAvailable, KeyValuePair<EvmExceptionType, Label> kvp)
    {
        Label skipTracing = method.DefineLabel();
        method.LoadArgument(TXTRACER_INDEX);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        method.LoadArgument(TXTRACER_INDEX);
        method.LoadLocal(gasAvailable);
        method.LoadConstant((int)kvp.Key);
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing>.EndInstructionTraceError), BindingFlags.Static | BindingFlags.NonPublic));

        method.MarkLabel(skipTracing);
    }

    private static void EmitCallToEndInstructionTrace(Emit<ExecuteSegment> method, Local gasAvailable)
    {
        Label skipTracing = method.DefineLabel();
        method.LoadArgument(TXTRACER_INDEX);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        method.LoadArgument(TXTRACER_INDEX);
        method.LoadLocal(gasAvailable);
        method.LoadArgument(VMSTATE_INDEX);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
        method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing>.EndInstructionTrace), BindingFlags.Static | BindingFlags.NonPublic));

        method.MarkLabel(skipTracing);
    }


    private static void EmitCallToStartInstructionTrace(Emit<ExecuteSegment> method, Local gasAvailable, Local head, OpcodeInfo op)
    {
        Label skipTracing = method.DefineLabel();
        method.LoadArgument(TXTRACER_INDEX);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        method.LoadArgument(TXTRACER_INDEX);
        method.LoadConstant((int)op.Operation);
        method.LoadArgument(0);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
        method.LoadLocal(gasAvailable);
        method.LoadConstant(op.ProgramCounter);
        method.LoadLocal(head);
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing>.StartInstructionTrace), BindingFlags.Static | BindingFlags.NonPublic));

        method.MarkLabel(skipTracing);
    }

    private static void EmitShiftUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, bool isLeft, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        MethodInfo shiftOp = typeof(UInt256).GetMethod(isLeft ? nameof(UInt256.LeftShift) : nameof(UInt256.RightShift));
        Label skipPop = il.DefineLabel();
        Label endOfOpcode = il.DefineLabel();

        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the stack
        Local shiftBit = il.DeclareLocal<uint>();

        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.Duplicate();
        il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
        il.Convert<uint>();
        il.StoreLocal(shiftBit);
        il.StoreLocal(locals[0]);

        il.LoadLocalAddress(locals[0]);
        il.LoadConstant(Word.FullSize);
        il.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        il.BranchIfFalse(skipPop);

        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.LoadLocalAddress(locals[1]);

        il.LoadLocal(shiftBit);

        il.LoadLocalAddress(uint256R);

        il.Call(shiftOp);

        il.StackPop(stack.idx, 2);
        il.CleanAndLoadWord(stack.span, stack.idx);
        il.LoadLocal(uint256R);
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx, 1);
        il.Branch(endOfOpcode);

        il.MarkLabel(skipPop);

        il.StackPop(stack.idx, 2);
        il.CleanWord(stack.span, stack.idx);
        il.StackPush(stack.idx, 1);

        il.MarkLabel(endOfOpcode);
    }

    private static void EmitShiftInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label skipPop = il.DefineLabel();
        Label signIsNeg = il.DefineLabel();
        Label endOfOpcode = il.DefineLabel();

        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the stack
        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);

        il.LoadLocalAddress(locals[0]);
        il.LoadConstant(Word.FullSize);
        il.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        il.BranchIfTrue(skipPop);

        il.LoadLocalAddress(locals[0]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetInt0);
        il.LoadLocalAddress(uint256R);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.Call(typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.RightShift), [typeof(int), typeof(Int256.Int256).MakeByRefType()]));
        il.StackPop(stack.idx, 2);
        il.CleanAndLoadWord(stack.span, stack.idx);
        il.LoadLocal(uint256R);
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx, 1);
        il.Branch(endOfOpcode);

        il.MarkLabel(skipPop);
        il.StackPop(stack.idx, 2);

        il.LoadLocalAddress(locals[0]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.Call(GetPropertyInfo(typeof(Int256.Int256), nameof(Int256.Int256.Sign), false, out _));
        il.LoadConstant(0);
        il.BranchIfLess(signIsNeg);

        il.CleanWord(stack.span, stack.idx);
        il.StackPush(stack.idx);
        il.Branch(endOfOpcode);

        // sign
        il.MarkLabel(signIsNeg);
        il.CleanAndLoadWord(stack.span, stack.idx);
        il.LoadFieldAddress(GetFieldInfo(typeof(Int256.Int256), nameof(Int256.Int256.MinusOne)));
        il.Call(GetAsMethodInfo<Int256.Int256, UInt256>());
        il.LoadObject<UInt256>();
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx);
        il.Branch(endOfOpcode);

        il.MarkLabel(endOfOpcode);
    }

    private static void EmitBitwiseUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the stack
        var refWordToRefByteMethod = GetAsMethodInfo<Word, byte>();
        var readVector256Method = GetReadUnalignedMethodInfo<Vector256<byte>>();
        var writeVector256Method = GetWriteUnalignedMethodInfo<Vector256<byte>>();
        var operationUnegenerified = operation.MakeGenericMethod(typeof(byte));

        using Local vectorResult = il.DeclareLocal<Vector256<byte>>();

        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(refWordToRefByteMethod);
        il.Call(readVector256Method);
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(refWordToRefByteMethod);
        il.Call(readVector256Method);

        il.Call(operationUnegenerified);
        il.StoreLocal(vectorResult);

        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(refWordToRefByteMethod);
        il.LoadLocal(vectorResult);
        il.Call(writeVector256Method);
        il.StackPop(stack.idx);
    }

    private static void EmitComparaisonUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        // we the two uint256 from the stack
        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.StackPop(stack.idx, 2);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(locals[1]);
        il.Call(operation);

        // convert to conv_i
        il.Convert<int>();
        il.Call(ConvertionExplicit<UInt256, int>());
        il.StoreLocal(uint256R);

        // push the result to the stack
        il.CleanAndLoadWord(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx);
    }

    private static void EmitComparaisonInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, bool isGreaterThan, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label endOpcodeHandling = il.DefineLabel();
        Label pushZerohandling = il.DefineLabel();
        // we the two uint256 from the stack
        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.StackPop(stack.idx, 2);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(locals[1]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadObject<Int256.Int256>();
        il.Call(operation);
        il.LoadConstant(0);
        if (isGreaterThan)
        {
            il.BranchIfLess(pushZerohandling);
        }
        else
        {
            il.BranchIfGreater(pushZerohandling);
        }

        il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.One)));
        il.StoreLocal(uint256R);
        il.Branch(endOpcodeHandling);

        il.MarkLabel(pushZerohandling);

        il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.Zero)));
        il.StoreLocal(uint256R);
        il.MarkLabel(endOpcodeHandling);
        // push the result to the stack
        il.CleanAndLoadWord(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx);
    }

    private static void EmitBinaryUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel();

        // we the two uint256 from the stack
        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.StackPop(stack.idx, 2);

        // incase of custom handling, we branch to the label
        customHandling?.Invoke(il, label, locals);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(uint256R);
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack
        il.CleanAndLoadWord(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx);
    }

    private static void EmitBinaryInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel();

        // we the two uint256 from the stack
        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.StackPop(stack.idx, 2);

        // incase of custom handling, we branch to the label
        customHandling?.Invoke(il, label, locals);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(locals[1]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(uint256R);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack
        il.CleanAndLoadWord(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx);
    }

    private static void EmitTrinaryUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel();

        // we the two uint256 from the stack
        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.StackLoadPrevious(stack.span, stack.idx, 3);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[2]);
        il.StackPop(stack.idx, 3);

        // incase of custom handling, we branch to the label
        customHandling?.Invoke(il, label, locals);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(locals[2]);
        il.LoadLocalAddress(uint256R);
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack
        il.CleanAndLoadWord(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx);
    }


    private static void EmitLogMethod<T>(
        Emit<T> il,
        (Local span, Local idx) stack,
        sbyte topicsCount,
        Dictionary<EvmExceptionType, Label> exceptions,
        Local uint256Position, Local uint256Length, Local int64A, Local gasAvailable, Local hash256, Local localReadOnlyMemory
    )
    {
        using Local logEntry = il.DeclareLocal<LogEntry>();
        Action loadExecutingAccount = () =>
        {
            // Executing account
            il.LoadArgument(VMSTATE_INDEX);
            il.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
            il.LoadField(
                GetFieldInfo(
                    typeof(ExecutionEnvironment),
                    nameof(ExecutionEnvironment.ExecutingAccount)
                )
            );
        };

        Action loadMemoryIntoByteArray = () =>
        {
            // memory load
            il.LoadArgument(VMSTATE_INDEX);
            il.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
            il.LoadLocalAddress(uint256Position); // position
            il.LoadLocalAddress(uint256Length); // length
            il.Call(
                typeof(EvmPooledMemory).GetMethod(
                    nameof(EvmPooledMemory.Load),
                    [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]
                )
            );
            il.StoreLocal(localReadOnlyMemory);
            il.LoadLocalAddress(localReadOnlyMemory);
            il.Call(typeof(ReadOnlyMemory<byte>).GetMethod(nameof(ReadOnlyMemory<byte>.ToArray)));
        };

        // Pop an item off the Stack, create a Hash256 object, store it in a local
        Action<int> storeLocalHash256AtStackIndex = (int index) =>
        {
            using (var keccak = il.DeclareLocal(typeof(ValueHash256)))
            {
                il.StackLoadPrevious(stack.span, stack.idx, index);
                il.Call(Word.GetKeccak);
                il.StoreLocal(keccak);
                il.LoadLocalAddress(keccak);
                il.NewObject(typeof(Hash256), typeof(ValueHash256).MakeByRefType());
            }
        };

        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(uint256Position); // position
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(uint256Length); // length
        il.StackPop(stack.idx, 2);
        // UpdateMemoryCost
        il.LoadArgument(VMSTATE_INDEX);
        il.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
        il.LoadLocalAddress(gasAvailable);
        il.LoadLocalAddress(uint256Position); // position
        il.LoadLocalAddress(uint256Length); // length
        il.Call(
            typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(
                nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)
            )
        );
        il.BranchIfFalse(exceptions[EvmExceptionType.OutOfGas]);

        // update gasAvailable
        il.LoadLocal(gasAvailable);
        il.LoadConstant(topicsCount * GasCostOf.LogTopic);
        il.Convert<ulong>();
        il.LoadLocalAddress(uint256Length); // length
        il.Call(typeof(UInt256Extensions).GetMethod(nameof(UInt256Extensions.ToLong), BindingFlags.Static | BindingFlags.Public, [typeof(UInt256).MakeByRefType()]));
        il.Convert<ulong>();
        il.LoadConstant(GasCostOf.LogData);
        il.Multiply();
        il.Add();
        il.Subtract();
        il.Duplicate();
        il.StoreLocal(gasAvailable); // gasAvailable -= gasCost
        il.LoadConstant((ulong)0);
        il.BranchIfLess(exceptions[EvmExceptionType.OutOfGas]);

        loadExecutingAccount();
        loadMemoryIntoByteArray();

        il.LoadConstant(topicsCount);
        il.NewArray<Hash256>();
        for (int i = 0; i < topicsCount; i++)
        {
            il.Duplicate();
            il.LoadConstant(i);
            storeLocalHash256AtStackIndex(i);
            il.StoreElement<Hash256>();
        }
        // Creat an LogEntry Object from Items on the Stack
        il.NewObject(typeof(LogEntry), typeof(Address), typeof(byte[]), typeof(Hash256[]));
        il.StoreLocal(logEntry);
        il.StackPop(stack.idx, topicsCount);

        il.LoadArgument(0);
        il.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
        il.LoadFieldAddress(typeof(EvmState).GetField("_accessTracker", BindingFlags.Instance | BindingFlags.NonPublic));
        il.CallVirtual(GetPropertyInfo(typeof(StackAccessTracker), nameof(StackAccessTracker.Logs), getSetter: false, out _));
        il.LoadLocal(logEntry);
        il.CallVirtual(
            typeof(ICollection<LogEntry>).GetMethod(nameof(ICollection<LogEntry>.Add))
        );

    }

    private static void EmitGasAvailabilityCheck<T>(
        Emit<T> il,
        Local gasAvailable,
        Label outOfGasLabel)
    {
        il.LoadLocal(gasAvailable);
        il.LoadConstant(0);
        il.BranchIfLess(outOfGasLabel);
    }
    private static Dictionary<int, long> BuildCostLookup(ReadOnlySpan<OpcodeInfo> code)
    {
        Dictionary<int, long> costs = new();
        int costStart = code[0].ProgramCounter;
        long coststack = 0;

        for (int pc = 0; pc < code.Length; pc++)
        {

            OpcodeInfo op = code[pc];
            switch (op.Operation)
            {
                case Instruction.JUMPDEST:
                    costs[costStart] = coststack; // remember the stack chain of opcodes
                    costStart = op.ProgramCounter;
                    coststack = op.Metadata.GasCost;
                    break;
                case Instruction.RETURN:
                case Instruction.REVERT:
                case Instruction.STOP:
                case Instruction.GAS:
                case Instruction.JUMPI:
                case Instruction.JUMP:
                    coststack += op.Metadata.GasCost;
                    costs[costStart] = coststack; // remember the stack chain of opcodes
                    costStart = op.ProgramCounter + 1;             // start with the next again
                    coststack = 0;
                    break;
                default:
                    coststack += op.Metadata.GasCost;
                    break;
            }
        }

        if (coststack > 0)
        {
            costs[costStart] = coststack;
        }
        return costs;
    }
}
