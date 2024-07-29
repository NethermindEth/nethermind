// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.IL;
using Nethermind.Int256;
using Sigil;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using Label = Sigil.Label;
using static Nethermind.Evm.IL.EmitExtensions;
using MathGmp.Native;
using Nethermind.Evm.Tracing.GethStyle.Custom.Native.FourByte;
using System.Drawing;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class ILCompiler
{
    public delegate void ExecuteSegment(ref ILEvmState vmstate, IBlockhashProvider blockhashProvider, IWorldState worldState, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec, byte[][] immediatesData);
    public class SegmentExecutionCtx
    {
        public ExecuteSegment Method;
        public byte[][] Data;
    }
    public static SegmentExecutionCtx CompileSegment(string segmentName, OpcodeInfo[] code, byte[][] data)
    {
        // code is optimistic assumes stack underflow and stack overflow to not occure (WE NEED EOF FOR THIS)
        // Note(Ayman) : What stops us from adopting stack analysis from EOF in ILVM?
        // Note(Ayman) : verify all endianness arguments and bytes

        Emit<ExecuteSegment> method = Emit<ExecuteSegment>.NewDynamicMethod(segmentName, doVerify: true, strictBranchVerification: true);

        if (code.Length == 0)
        {
            method.Return();
        }
        else
        {
            EmitSegmentBody(method, code);
        }

        ExecuteSegment dynEmitedDelegate = method.CreateDelegate();
        return new SegmentExecutionCtx
        {
            Method = dynEmitedDelegate,
            Data = data
        };
    }

    private static void EmitSegmentBody(Emit<ExecuteSegment> method, OpcodeInfo[] code)
    {

        using Local jmpDestination = method.DeclareLocal(Word.Int0Field.FieldType);
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
        using Local int64A = method.DeclareLocal(typeof(long));
        using Local byte8A = method.DeclareLocal(typeof(byte));
        using Local buffer = method.DeclareLocal(typeof(byte*));

        using Local storageCell = method.DeclareLocal(typeof(StorageCell));

        using Local gasAvailable = method.DeclareLocal(typeof(long));
        using Local programCounter = method.DeclareLocal(typeof(ushort));

        using Local stack = method.DeclareLocal(typeof(Span<Word>));
        using Local head = method.DeclareLocal(typeof(int));

        // allocate stack
        method.LoadArgument(0);
        method.Duplicate();
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Stack)));
        method.Call(GetCastMethodInfo<byte, Word>());
        method.StoreLocal(stack);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.StackHead)));
        method.StoreLocal(head);

        // set gas to local
        method.LoadArgument(0);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.GasAvailable)));
        method.Convert<long>();
        method.StoreLocal(gasAvailable);

        // set pc to local
        method.LoadArgument(0);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ProgramCounter)));
        method.StoreLocal(programCounter);

        Dictionary<EvmExceptionType, Label> evmExceptionLabels = new();

        foreach (var exception in Enum.GetValues<EvmExceptionType>())
        {
            evmExceptionLabels.Add(exception, method.DefineLabel());
        }

        Label exit = method.DefineLabel(); // the label just before return
        Label jumpTable = method.DefineLabel(); // jump table
        Label ret = method.DefineLabel();

        Dictionary<int, Label> jumpDestinations = new();


        // Idea(Ayman) : implement every opcode as a method, and then inline the IL of the method in the main method

        Dictionary<int, long> gasCost = BuildCostLookup(code);
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
                continue;
            }
            // set pc
            method.LoadConstant(op.ProgramCounter);
            method.StoreLocal(programCounter);

            // load gasAvailable
            method.LoadLocal(gasAvailable);

            // get pc gas cost
            method.LoadConstant(op.Metadata?.GasCost ?? 0);

            // subtract the gas cost
            method.Subtract();
            // check if gas is available
            method.Duplicate();
            method.StoreLocal(gasAvailable);
            method.LoadConstant((long)0);

            // if gas is not available, branch to out of gas
            method.BranchIfLess(evmExceptionLabels[EvmExceptionType.OutOfGas]);

            // else emit 
            switch (op.Operation)
            {
                case Instruction.STOP:
                    method.LoadArgument(0);
                    method.LoadConstant(true);
                    method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ShouldStop)));
                    method.Branch(ret);
                    break;
                case Instruction.INVALID:
                    method.Branch(evmExceptionLabels[EvmExceptionType.InvalidCode]);
                    break;
                case Instruction.CHAINID:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ChainId)));
                    method.StoreField(Word.Ulong0Field);
                    method.StackPush(head);
                    break;
                case Instruction.NOT:
                    method.Load(stack, head);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackPop(head);

                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256R);
                    method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Not)));
                    method.Load(stack, head);
                    method.LoadLocal(uint256R);
                    method.Call(Word.SetUInt256);
                    break;
                case Instruction.JUMP:
                    // we jump into the jump table
                    method.Branch(jumpTable);
                    break;
                case Instruction.JUMPI:
                    // consume the jump condition
                    Label noJump = method.DefineLabel();
                    method.StackLoadPrevious(stack, head, 2);
                    method.Call(Word.GetIsZero);

                    // if the jump condition is false, we do not jump
                    method.BranchIfTrue(noJump);

                    // load the jump address
                    method.LoadConstant(1);
                    method.StoreLocal(consumeJumpCondition);

                    // we jump into the jump table
                    method.Branch(jumpTable);

                    method.MarkLabel(noJump);
                    method.StackPop(head, 2);
                    break;
                case Instruction.PUSH0:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.Call(Word.SetToZero);
                    method.StackPush(head);
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
                    // we load the stack
                    method.CleanWord(stack, head);
                    method.Load(stack, head);

                    // we load the span of bytes
                    method.LoadArgument(5);
                    method.LoadConstant(op.Arguments.Value);
                    method.LoadElement<byte[]>();
                    method.Call(typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(byte[]) }));
                    method.StoreLocal(localReadonOnlySpan);

                    // we call UInt256 constructor taking a span of bytes and a bool
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.LoadConstant(BitConverter.IsLittleEndian);
                    method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));

                    // we store the UInt256 in the stack
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                case Instruction.ADD:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, null, uint256A, uint256B);
                    break;
                case Instruction.SUB:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, null, uint256A, uint256B);
                    break;
                case Instruction.MUL:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, null, uint256A, uint256B);
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
                            il.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, uint256A, uint256B);
                    break;
                case Instruction.SMOD:
                    EmitBinaryInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(Int256.Int256.Mod), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label = il.DefineLabel();

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZeroOrOne), false, out _));
                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, uint256A, uint256B);
                    break;
                case Instruction.DIV:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label = il.DefineLabel();

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZero), false, out _));
                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, uint256A, uint256B);
                    break;
                case Instruction.SDIV:
                    EmitBinaryInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(Int256.Int256.Divide), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label1 = il.DefineLabel();
                            Label label2 = il.DefineLabel();

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZeroOrOne), false, out _));
                            il.BranchIfFalse(label1);

                            il.LoadConstant(0);
                            il.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label1);

                            il.LoadLocalAddress(locals[1]);
                            il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
                            il.LoadField(typeof(Int256.Int256).GetField(nameof(Int256.Int256.MinusOne), BindingFlags.Static | BindingFlags.Public));
                            il.Call(typeof(Int256.Int256).GetMethod("op_Equality"));

                            il.LoadLocalAddress(locals[0]);
                            il.Call(typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.P255), BindingFlags.Static).GetMethod);
                            il.Call(typeof(Int256.Int256).GetMethod("op_Equality"));
                            il.And();
                            il.BranchIfFalse(label2);

                            il.Call(typeof(VirtualMachine).GetProperty(nameof(VirtualMachine.P255), BindingFlags.Static).GetMethod);
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label2);
                        }, uint256A, uint256B);
                    break;
                case Instruction.ADDMOD:
                    EmitTrinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.AddMod), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label = il.DefineLabel();

                            il.LoadLocalAddress(locals[2]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZeroOrOne), false, out _));
                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, uint256A, uint256B, uint256C);
                    break;
                case Instruction.MULMOD:
                    EmitTrinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.MultiplyMod), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label = il.DefineLabel();

                            il.LoadLocalAddress(locals[2]);
                            il.Call(GetPropertyInfo(typeof(UInt256), nameof(UInt256.IsZeroOrOne), false, out _));
                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                            il.StoreLocal(uint256R);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, uint256A, uint256B, uint256C);
                    break;
                case Instruction.SHL:
                    EmitShiftUInt256Method(method, uint256R, (stack, head), isLeft: true, uint256A, uint256B);
                    break;
                case Instruction.SHR:
                    EmitShiftUInt256Method(method, uint256R, (stack, head), isLeft: false, uint256A, uint256B);
                    break;
                case Instruction.SAR:
                    EmitShiftInt256Method(method, uint256R, (stack, head), uint256A, uint256B);
                    break;
                case Instruction.AND:
                    EmitBitwiseUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.And), BindingFlags.Public | BindingFlags.Static)!, uint256A, uint256B);
                    break;
                case Instruction.OR:
                    EmitBitwiseUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Or), BindingFlags.Public | BindingFlags.Static)!, uint256A, uint256B);
                    break;
                case Instruction.XOR:
                    EmitBitwiseUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Xor), BindingFlags.Public | BindingFlags.Static)!, uint256A, uint256B);
                    break;
                case Instruction.EXP:
                    EmitBinaryUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!, null, uint256A, uint256B);
                    break;
                case Instruction.LT:
                    EmitComparaisonUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), uint256A, uint256B);
                    break;
                case Instruction.GT:
                    EmitComparaisonUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), uint256A, uint256B);
                    break;
                case Instruction.SLT:
                    EmitComparaisonInt256Method(method, uint256R, (stack, head), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), false, uint256A, uint256B);
                    break;
                case Instruction.SGT:
                    EmitComparaisonInt256Method(method, uint256R, (stack, head), typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.CompareTo), new[] { typeof(Int256.Int256) }), true, uint256A, uint256B);
                    break;
                case Instruction.EQ:
                    EmitComparaisonUInt256Method(method, uint256R, (stack, head), typeof(UInt256).GetMethod("op_Equality", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), uint256A, uint256B);
                    break;
                case Instruction.ISZERO:
                    // we load the stack
                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetIsZero);
                    method.StackPop(head, 1);
                    method.StoreLocal(byte8A);

                    // we convert the result to a Uint256 and store it in the stack
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocal(byte8A);
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.Byte0)));
                    method.StackPush(head);
                    break;
                case Instruction.POP:
                    method.StackPop(head);
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
                    int count = (int)op.Operation - (int)Instruction.DUP1 + 1;
                    method.Load(stack, head);
                    method.StackLoadPrevious(stack, head, count);
                    method.LoadObject(typeof(Word));
                    method.StoreObject(typeof(Word));
                    method.StackPush(head);
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
                    count = (int)op.Operation - (int)Instruction.SWAP1 + 1;

                    method.LoadLocalAddress(uint256R);
                    method.StackLoadPrevious(stack, head, 1);
                    method.LoadObject(typeof(Word));
                    method.StoreObject(typeof(Word));

                    method.StackLoadPrevious(stack, head, 1);
                    method.StackLoadPrevious(stack, head, count);
                    method.LoadObject(typeof(Word));
                    method.StoreObject(typeof(Word));

                    method.StackLoadPrevious(stack, head, count);
                    method.LoadLocalAddress(uint256R);
                    method.LoadObject(typeof(Word));
                    method.StoreObject(typeof(Word));
                    break;

                // Note(Ayman): following opcode need double checking
                // is pushing to stack happening correctly
                case Instruction.CODESIZE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadConstant(code.Length);
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.UInt0)));
                    method.StackPush(head);
                    break;
                case Instruction.PC:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocal(programCounter);
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.UInt0)));
                    method.StackPush(head);
                    break;
                case Instruction.COINBASE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                    method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                    method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
                    method.Call(Word.SetAddress);
                    method.StackPush(head);
                    break;
                case Instruction.TIMESTAMP:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                    method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                    method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
                    method.StoreField(Word.Ulong0Field);
                    method.StackPush(head);
                    break;
                case Instruction.NUMBER:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                    method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                    method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
                    method.StoreField(Word.Ulong0Field);
                    method.StackPush(head);
                    break;
                case Instruction.GASLIMIT:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                    method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                    method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasLimit), false, out _));
                    method.StoreField(Word.Ulong0Field);
                    method.StackPush(head);
                    break;
                case Instruction.CALLER:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
                    method.Call(Word.SetAddress);
                    method.StackPush(head);
                    break;
                case Instruction.ADDRESS:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                    method.Call(Word.SetAddress);
                    method.StackPush(head);
                    break;
                case Instruction.ORIGIN:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                    method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin), false, out _));
                    method.Call(Word.SetAddress);
                    method.StackPush(head);
                    break;
                case Instruction.CALLVALUE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Value)));
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                case Instruction.GASPRICE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                    method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice), false, out _));
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                case Instruction.CALLDATACOPY:
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
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling)));
                    method.LoadConstant(GasCostOf.Memory);
                    method.Multiply();
                    method.LoadConstant(GasCostOf.VeryLow);
                    method.Add();
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                    method.BranchIfTrue(endOfOpcode);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);


                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.InputBuffer)));
                    method.LoadObject(typeof(ReadOnlyMemory<byte>));
                    method.LoadLocalAddress(uint256B);
                    method.LoadLocal(uint256C);
                    method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                    method.Convert<int>();
                    method.LoadConstant((int)PadDirection.Right);
                    method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                    method.StoreLocal(localZeroPaddedSpan);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(localZeroPaddedSpan);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                    method.MarkLabel(endOfOpcode);
                    break;
                case Instruction.CALLDATALOAD:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);

                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetUInt256);
                    method.StackPop(head, 1);
                    method.StoreLocal(uint256A);


                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.InputBuffer)));
                    method.LoadObject(typeof(ReadOnlyMemory<byte>));

                    method.LoadLocalAddress(uint256A);
                    method.LoadConstant(Word.Size);
                    method.LoadConstant((int)PadDirection.Right);
                    method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                    method.LoadField(GetFieldInfo(typeof(ZeroPaddedSpan), nameof(ZeroPaddedSpan.Span)));
                    method.Call(Word.SetSpan);
                    method.StackPush(head);
                    break;
                case Instruction.CALLDATASIZE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.InputBuffer)));
                    method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.Int0)));
                    method.StackPush(head);
                    break;
                case Instruction.MSIZE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.Ulong0)));
                    method.StackPush(head);
                    break;
                case Instruction.MSTORE:
                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackLoadPrevious(stack, head, 2);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256B);
                    method.StackPop(head, 2);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadConstant(Word.Size);
                    method.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                    method.StoreLocal(uint256C);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.LoadConstant(Word.Size);
                    method.Call(typeof(UInt256).GetMethod(nameof(UInt256.PaddedBytes)));
                    method.Call(typeof(Span<byte>).GetMethod("op_Implicit", new[] { typeof(byte[]) }));
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
                    break;
                case Instruction.MSTORE8:
                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackLoadPrevious(stack, head, 2);
                    method.LoadField(Word.Byte0Field);
                    method.StoreLocal(byte8A);
                    method.StackPop(head, 2);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadConstant(1);
                    method.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                    method.StoreLocal(uint256C);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.LoadConstant(Word.Size);
                    method.Call(typeof(UInt256).GetMethod(nameof(UInt256.PaddedBytes)));
                    method.LoadConstant(0);
                    method.LoadElement<byte>();

                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
                    break;
                case Instruction.MLOAD:
                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackPop(head, 1);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadFieldAddress(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BigInt32)));
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
                    method.Call(typeof(Span<byte>).GetMethod("op_Implicit", new[] { typeof(Span<byte>) }));
                    method.StoreLocal(localReadonOnlySpan);

                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.LoadConstant(BitConverter.IsLittleEndian);
                    method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                case Instruction.MCOPY:
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
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling)));
                    method.LoadConstant((long)1);
                    method.Add();
                    method.LoadConstant(GasCostOf.VeryLow);
                    method.Multiply();
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Max)));
                    method.StoreLocal(uint256R);
                    method.LoadLocalAddress(uint256R);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256B);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
                    break;
                case Instruction.KECCAK256:
                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackLoadPrevious(stack, head, 2);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256B);
                    method.StackPop(head, 2);

                    method.LoadLocal(gasAvailable);
                    method.LoadLocalAddress(uint256B);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling)));
                    method.LoadConstant(GasCostOf.Sha3Word);
                    method.Multiply();
                    method.LoadConstant(GasCostOf.Sha3);
                    method.Add();
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);


                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                    method.Call(typeof(ValueKeccak).GetMethod(nameof(ValueKeccak.Compute), [typeof(ReadOnlySpan<byte>)]));
                    method.Call(Word.SetKeccak);
                    method.StackPush(head);
                    break;
                case Instruction.BYTE:
                    // load a
                    method.StackLoadPrevious(stack, head, 1);
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

                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.LoadLocal(uint256A);
                    method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                    method.Convert<int>();
                    method.Call(typeof(ReadOnlySpan<byte>).GetMethod("get_Item"));
                    method.LoadIndirect<byte>();
                    method.StoreField(Word.Byte0Field);
                    method.StackPush(head);
                    method.Branch(endOfInstructionImpl);

                    method.MarkLabel(pushZeroLabel);
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.Call(Word.SetToZero);
                    method.StackPush(head);

                    method.MarkLabel(endOfInstructionImpl);
                    break;
                case Instruction.CODECOPY:
                    endOfOpcode = method.DefineLabel();

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
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling)));
                    method.LoadConstant(GasCostOf.Memory);
                    method.Multiply();
                    method.LoadConstant(GasCostOf.VeryLow);
                    method.Add();
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                    method.BranchIfTrue(endOfOpcode);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.MachineCode)));
                    method.Call(typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(byte[]) }));
                    method.LoadLocalAddress(uint256B);
                    method.LoadLocal(uint256C);
                    method.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                    method.Call(typeof(ReadOnlySpan<byte>).GetMethod(nameof(ReadOnlySpan<byte>.Slice), [typeof(UInt256).MakeByRefType(), typeof(int)]));
                    method.StoreLocal(localReadonOnlySpan);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));

                    method.MarkLabel(endOfOpcode);
                    break;
                case Instruction.GAS:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocal(gasAvailable);
                    method.StoreField(Word.Ulong0Field);
                    method.StackPush(head);
                    break;
                case Instruction.RETURNDATASIZE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ReturnBuffer)));
                    method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                    method.StoreField(Word.Int0Field);
                    method.StackPush(head);
                    break;
                case Instruction.RETURNDATACOPY:
                    endOfOpcode = method.DefineLabel();

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
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling)));
                    method.LoadConstant(GasCostOf.Memory);
                    method.Multiply();
                    method.LoadConstant(GasCostOf.VeryLow);
                    method.Add();
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    // Note : check if c + b > returnData.Size

                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                    method.BranchIfTrue(endOfOpcode);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ReturnBuffer)));
                    method.Call(GetPropertyInfo(typeof(ReadOnlyMemory<byte>), nameof(ReadOnlyMemory<byte>.Span), false, out _));
                    method.LoadLocalAddress(uint256B);
                    method.LoadLocal(uint256C);
                    method.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                    method.Call(typeof(ReadOnlySpan<byte>).GetMethod(nameof(ReadOnlySpan<byte>.Slice), [typeof(UInt256).MakeByRefType(), typeof(int)]));
                    method.StoreLocal(localReadonOnlySpan);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));

                    method.MarkLabel(endOfOpcode);
                    break;
                case Instruction.RETURN or Instruction.REVERT:
                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackLoadPrevious(stack, head, 2);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256B);
                    method.StackPop(head, 2);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ReturnBuffer)));
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Load), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                    method.StoreObject<ReadOnlyMemory<byte>>();

                    method.LoadArgument(0);
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

                    break;
                case Instruction.BASEFEE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                    method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                    method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.BaseFeePerGas), false, out _));
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                case Instruction.BLOBBASEFEE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                    method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.BlobBaseFee), false, out _));
                    method.Call(GetPropertyInfo(typeof(UInt256?), nameof(Nullable<UInt256>.Value), false, out _));
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                case Instruction.PREVRANDAO:
                    Label isPostMergeBranch = method.DefineLabel();
                    endOfOpcode = method.DefineLabel();
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.BlkCtx)));
                    method.Call(GetPropertyInfo(typeof(BlockExecutionContext), nameof(BlockExecutionContext.Header), false, out _));
                    method.Duplicate();
                    method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.IsPostMerge), false, out _));
                    method.BranchIfTrue(isPostMergeBranch);
                    method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.Random), false, out _));
                    method.Call(GetPropertyInfo(typeof(Hash256), nameof(Hash256.Bytes), false, out _));
                    method.Call(typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(Span<byte>) }));
                    method.StoreLocal(localReadonOnlySpan);
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.LoadConstant(BitConverter.IsLittleEndian);
                    method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));
                    method.StackPush(head);
                    method.Branch(endOfOpcode);

                    method.MarkLabel(isPostMergeBranch);
                    method.Call(GetPropertyInfo(typeof(BlockHeader), nameof(BlockHeader.Difficulty), false, out _));
                    method.StackPush(head);

                    method.MarkLabel(endOfOpcode);
                    break;
                case Instruction.BLOBHASH:
                    Label blobVersionedHashNotFound = method.DefineLabel();
                    endOfOpcode = method.DefineLabel();


                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                    method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                    method.LoadNull();
                    method.BranchIfEqual(blobVersionedHashNotFound);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                    method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                    method.Call(GetPropertyInfo(typeof(byte[][]), nameof(Array.Length), false, out _));
                    method.StackLoadPrevious(stack, head, 1);
                    method.LoadField(Word.Int0Field);
                    method.Duplicate();
                    method.StoreLocal(uint32A);
                    method.StackPop(head, 1);
                    method.BranchIfLessOrEqual(blobVersionedHashNotFound);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                    method.Call(GetPropertyInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.BlobVersionedHashes), false, out _));
                    method.LoadLocal(uint32A);
                    method.LoadElement<Byte[]>();
                    method.StoreLocal(localArray);

                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocal(localArray);
                    method.Call(Word.SetArray);
                    method.Branch(endOfOpcode);

                    method.MarkLabel(blobVersionedHashNotFound);
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.Call(Word.SetToZero);

                    method.MarkLabel(endOfOpcode);
                    method.StackPush(head, 1);
                    break;
                case Instruction.BLOCKHASH:
                    Label blockHashReturnedNull = method.DefineLabel();
                    Label pushToStackRegion = method.DefineLabel();

                    method.StackLoadPrevious(stack, head, 1);
                    method.StackPop(head, 1);
                    method.LoadField(Word.Ulong0Field);
                    method.Convert<long>();
                    method.LoadConstant(long.MaxValue);
                    method.Call(typeof(Math).GetMethod(nameof(Math.Min), [typeof(long), typeof(long)]));
                    method.StoreLocal(int64A);

                    method.LoadArgument(1);
                    method.LoadArgument(0);
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
                    method.Call(typeof(Span<byte>).GetMethod("op_Implicit", new[] { typeof(Span<byte>) }));
                    method.StoreLocal(localReadonOnlySpan);
                    method.Branch(pushToStackRegion);
                    // equal to null

                    method.MarkLabel(blockHashReturnedNull);

                    method.LoadField(GetFieldInfo(typeof(VirtualMachine), nameof(VirtualMachine.BytesZero32)));
                    method.Call(typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(byte[]) }));
                    method.StoreLocal(localReadonOnlySpan);

                    method.MarkLabel(pushToStackRegion);
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.LoadConstant(BitConverter.IsLittleEndian);
                    method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                case Instruction.SIGNEXTEND:
                    Label signIsNegative = method.DefineLabel();
                    Label endOfOpcodeHandling = method.DefineLabel();

                    method.StackLoadPrevious(stack, head, 1);
                    method.LoadField(Word.UInt0Field);
                    method.StoreLocal(uint32A);
                    method.StackLoadPrevious(stack, head, 2);
                    method.Call(Word.GetSpan);
                    method.StoreLocal(localSpan);
                    method.StackPop(head, 2);

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
                    method.Convert<int>();
                    method.Call(typeof(MemoryExtensions).GetMethod(nameof(MemoryExtensions.AsSpan), [typeof(byte[]), typeof(int), typeof(int)]));
                    method.LoadLocalAddress(localSpan);
                    method.Call(typeof(Span<byte>).GetMethod(nameof(Span<byte>.CopyTo), [typeof(Span<byte>)]));
                    break;

                case Instruction.TSTORE:
                    method.LoadArgument(0);
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

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                    method.LoadLocalAddress(uint256A);
                    method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                    method.StoreLocal(storageCell);

                    method.LoadArgument(2);
                    method.LoadLocalAddress(storageCell);
                    method.LoadLocal(localArray);
                    method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.SetTransientState), [typeof(StorageCell).MakeByRefType(), typeof(byte[])]));
                    break;
                case Instruction.TLOAD:
                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackPop(head, 1);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                    method.LoadLocalAddress(uint256A);
                    method.NewObject(typeof(StorageCell), [typeof(Address), typeof(UInt256).MakeByRefType()]);
                    method.StoreLocal(storageCell);

                    method.LoadArgument(2);
                    method.LoadLocalAddress(storageCell);
                    method.CallVirtual(typeof(IWorldState).GetMethod(nameof(IWorldState.GetTransientState), [typeof(StorageCell).MakeByRefType()]));
                    method.StoreLocal(localReadonOnlySpan);

                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadLocal(localReadonOnlySpan);
                    method.Call(Word.SetSpan);
                    method.StackPush(head);
                    break;
                case Instruction.EXTCODESIZE:
                    method.LoadLocal(gasAvailable);
                    method.LoadArgument(4);
                    method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetAddress);
                    method.StoreLocal(address);
                    method.StackPop(head, 1);

                    method.LoadLocalAddress(gasAvailable);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                    method.LoadLocal(address);
                    method.LoadArgument(4);
                    method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                    method.LoadConstant(true);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.CleanWord(stack, head);
                    method.Load(stack, head);

                    method.LoadArgument(3);
                    method.LoadArgument(2);
                    method.LoadLocal(address);
                    method.LoadArgument(4);
                    method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.GetCachedCodeInfo), [typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                    method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));
                    method.StoreLocal(localReadOnlyMemory);
                    method.LoadLocalAddress(localReadOnlyMemory);
                    method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));

                    method.StoreField(Word.Int0Field);
                    method.StackPush(head);
                    break;

                case Instruction.EXTCODECOPY:
                    endOfOpcode = method.DefineLabel();

                    method.LoadLocal(gasAvailable);
                    method.LoadArgument(4);
                    method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeCost)));
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetAddress);
                    method.StoreLocal(address);
                    method.StackLoadPrevious(stack, head, 2);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackLoadPrevious(stack, head, 3);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256B);
                    method.StackLoadPrevious(stack, head, 4);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256C);
                    method.StackPop(head, 4);

                    method.LoadLocal(gasAvailable);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling)));
                    method.LoadConstant(GasCostOf.Memory);
                    method.Multiply();
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.LoadLocalAddress(gasAvailable);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                    method.LoadLocal(address);
                    method.LoadArgument(4);
                    method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                    method.LoadConstant(true);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(UInt256).GetProperty(nameof(UInt256.IsZero)).GetMethod!);
                    method.BranchIfTrue(endOfOpcode);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(3);
                    method.LoadArgument(2);
                    method.LoadLocal(address);
                    method.LoadArgument(4);
                    method.CallVirtual(typeof(ICodeInfoRepository).GetMethod(nameof(ICodeInfoRepository.GetCachedCodeInfo), [typeof(IWorldState), typeof(Address), typeof(IReleaseSpec)]));
                    method.Call(GetPropertyInfo<CodeInfo>(nameof(CodeInfo.MachineCode), false, out _));

                    method.LoadLocalAddress(uint256B);
                    method.LoadLocal(uint256C);
                    method.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
                    method.Convert<int>();
                    method.LoadConstant((int)PadDirection.Right);
                    method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), [typeof(ReadOnlyMemory<byte>), typeof(UInt256).MakeByRefType(), typeof(int), typeof(PadDirection)]));
                    method.StoreLocal(localZeroPaddedSpan);

                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Memory)));
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(localZeroPaddedSpan);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(ZeroPaddedSpan).MakeByRefType()]));

                    method.MarkLabel(endOfOpcode);
                    break;
                case Instruction.EXTCODEHASH:
                    endOfOpcode = method.DefineLabel();

                    method.LoadLocal(gasAvailable);
                    method.LoadArgument(4);
                    method.Call(typeof(ReleaseSpecExtensions).GetMethod(nameof(ReleaseSpecExtensions.GetExtCodeHashCost)));
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetAddress);
                    method.StoreLocal(address);
                    method.StackPop(head, 1);

                    method.LoadLocalAddress(gasAvailable);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                    method.LoadLocal(address);
                    method.LoadArgument(4);
                    method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                    method.LoadConstant(true);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(2);
                    method.LoadLocal(address);
                    method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IWorldState.AccountExists)));
                    method.LoadConstant(false);
                    method.CompareEqual();
                    method.LoadArgument(2);
                    method.LoadLocal(address);
                    method.CallVirtual(typeof(IReadOnlyStateProvider).GetMethod(nameof(IWorldState.IsDeadAccount)));
                    method.Or();
                    method.BranchIfTrue(endOfOpcode);

                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(2);
                    method.LoadLocal(address);
                    method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetCodeHash)));
                    method.Call(Word.SetKeccak);
                    method.MarkLabel(endOfOpcode);
                    method.StackPush(head);
                    break;
                case Instruction.SELFBALANCE:
                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(2);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.ExecutingAccount)));
                    method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                case Instruction.BALANCE:
                    method.StackLoadPrevious(stack, head, 1);
                    method.Call(Word.GetAddress);
                    method.StoreLocal(address);
                    method.StackPop(head, 1);

                    method.LoadLocalAddress(gasAvailable);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmState)));
                    method.LoadLocal(address);
                    method.LoadArgument(4);
                    method.Call(GetPropertyInfo<NullTxTracer>(nameof(NullTxTracer.Instance), false, out _));
                    method.LoadConstant(true);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.ChargeAccountAccessGas)));
                    method.BranchIfFalse(evmExceptionLabels[EvmExceptionType.OutOfGas]);

                    method.CleanWord(stack, head);
                    method.Load(stack, head);
                    method.LoadArgument(2);
                    method.LoadLocal(address);
                    method.CallVirtual(typeof(IAccountStateProvider).GetMethod(nameof(IWorldState.GetBalance)));
                    method.Call(Word.SetUInt256);
                    method.StackPush(head);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        Label skipProgramCounterSetting = method    .DefineLabel();
        Local isEphemeralJump = method.DeclareLocal<bool>();
        // prepare ILEvmState
        // check if returnState is null
        method.MarkLabel(ret);
        // we get stack size
        method.LoadArgument(0);
        method.LoadLocal(head);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.StackHead)));

        // set stack
        method.LoadArgument(0);
        method.LoadLocal(stack);
        method.Call(GetCastMethodInfo<Word, byte>());
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Stack)));

        // set gas available
        method.LoadArgument(0);
        method.LoadLocal(gasAvailable);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.GasAvailable)));

        // set program counter
        method.LoadLocal(isEphemeralJump);
        method.BranchIfTrue(skipProgramCounterSetting);

        method.LoadArgument(0);
        method.LoadLocal(programCounter);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ProgramCounter)));

        method.MarkLabel(skipProgramCounterSetting);


        // set exception
        method.LoadArgument(0);
        method.LoadConstant((int)EvmExceptionType.None);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmException)));

        // go to return
        method.Branch(exit);

        // jump table
        method.MarkLabel(jumpTable);
        method.StackLoadPrevious(stack, head, 1);
        method.LoadField(Word.Int0Field);
        method.Call(typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), BindingFlags.Public | BindingFlags.Static, new[] { typeof(uint) }), null);
        method.StoreLocal(jmpDestination);
        method.StackPop(head);

        //check if jump crosses segment boundaies
        Label jumpIsLocal = method.DefineLabel();
        method.LoadLocal(jmpDestination);
        method.LoadConstant(code[code.Length - 1].ProgramCounter + code[code.Length - 1].Metadata?.AdditionalBytes ?? 0);
        method.BranchIfLessOrEqual(jumpIsLocal);

        method.LoadArgument(0);
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
        method.StackPop(head, consumeJumpCondition);
        method.LoadConstant(0);
        method.StoreLocal(consumeJumpCondition);

        // if (jumpDest > uint.MaxValue)



        method.LoadConstant(uint.MaxValue);
        method.LoadLocal(jmpDestination);
        // goto invalid address
        method.BranchIfGreater(evmExceptionLabels[EvmExceptionType.InvalidJumpDestination]);
        // else

        const int bitMask = (1 << 4) - 1; // 128
        Label[] jumps = new Label[bitMask];
        for (int i = 0; i < bitMask; i++)
        {
            jumps[i] = method.DefineLabel();
        }

        // we get first Word.Size bits of the jump destination since it is less than int.MaxValue


        method.LoadLocal(jmpDestination);
        method.LoadConstant(bitMask);
        method.And();

        // switch on the first 7 bits
        method.Switch(jumps);

        for (int i = 0; i < bitMask; i++)
        {
            method.MarkLabel(jumps[i]);
            method.Print(jmpDestination);
            // for each destination matching the bit mask emit check for the equality
            foreach (int dest in jumpDestinations.Keys.Where(dest => (dest & bitMask) == i))
            {
                method.LoadLocal(jmpDestination);
                method.LoadConstant(dest);
                method.Duplicate();
                method.StoreLocal(uint32A);
                method.Print(uint32A);
                method.BranchIfEqual(jumpDestinations[dest]);
            }
            method.Print(jmpDestination);
            // each bucket ends with a jump to invalid access to do not fall through to another one
            method.Branch(evmExceptionLabels[EvmExceptionType.InvalidCode]);
        }

        foreach (var kvp in evmExceptionLabels)
        {
            method.MarkLabel(kvp.Value);
            method.LoadArgument(0);
            method.LoadConstant((int)kvp.Key);
            method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmException)));
            method.Branch(exit);
        }

        // return
        method.MarkLabel(exit);
        method.Return();
    }

    private static void EmitShiftUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, bool isLeft, params Local[] locals)
    {
        MethodInfo shiftOp = typeof(UInt256).GetMethod(isLeft ? nameof(UInt256.LeftShift) : nameof(UInt256.RightShift));
        Label skipPop = il.DefineLabel();
        Label endOfOpcode = il.DefineLabel();

        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the stack
        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);

        il.LoadLocalAddress(locals[0]);
        il.LoadConstant(Word.Size * sizeof(byte));
        il.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        il.BranchIfTrue(skipPop);

        il.LoadLocalAddress(locals[0]);
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.LoadField(Word.Int0Field);
        il.LoadLocalAddress(uint256R);
        il.Call(shiftOp);
        il.StackPop(stack.idx, 2);
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
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

    private static void EmitShiftInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, params Local[] locals)
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
        il.LoadConstant(Word.Size * sizeof(byte));
        il.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        il.BranchIfTrue(skipPop);

        il.LoadLocalAddress(locals[0]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.LoadField(Word.Int0Field);
        il.LoadLocalAddress(uint256R);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.Call(typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.RightShift), [typeof(int), typeof(Int256.Int256).MakeByRefType()]));
        il.StackPop(stack.idx, 2);
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
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
        il.Load(stack.span, stack.idx);
        il.Call(Word.SetToZero);
        il.StackPush(stack.idx);
        il.Branch(endOfOpcode);

        // sign
        il.MarkLabel(signIsNeg);
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
        il.LoadFieldAddress(GetFieldInfo(typeof(Int256.Int256), nameof(Int256.Int256.MinusOne)));
        il.Call(GetAsMethodInfo<Int256.Int256, UInt256>());
        il.LoadObject<UInt256>();
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx);
        il.Branch(endOfOpcode);

        il.MarkLabel(endOfOpcode);
    }

    private static void EmitBitwiseUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, params Local[] locals)
    {
        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the stack
        il.StackLoadPrevious(stack.span, stack.idx, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.span, stack.idx, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.StackPop(stack.idx, 2);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(uint256R);
        il.Call(operation, null);

        // push the result to the stack
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
    }

    private static void EmitComparaisonUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, params Local[] locals)
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
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(locals[0]);
        il.Call(operation, null);

        // convert to conv_i
        il.Convert<int>();
        il.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
        il.StoreLocal(uint256R);

        // push the result to the stack
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
    }

    private static void EmitComparaisonInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, bool isGreaterThan, params Local[] locals)
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
        il.LoadLocalAddress(locals[1]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(locals[0]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadObject<Int256.Int256>();
        il.Call(operation, null);
        il.LoadConstant(0);
        if(isGreaterThan)
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
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
    }

    private static void EmitBinaryUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, params Local[] locals)
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
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(uint256R);
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx, 1);
    }

    private static void EmitBinaryInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, params Local[] locals)
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
        il.LoadLocalAddress(locals[1]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(locals[0]);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(uint256R);
        il.Call(GetAsMethodInfo<UInt256, Int256.Int256>());
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx, 1);
    }

    private static void EmitTrinaryUInt256Method<T>(Emit<T> il, Local uint256R, (Local span, Local idx) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, params Local[] locals)
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
        il.LoadLocalAddress(locals[2]);
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(uint256R);
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack
        il.CleanWord(stack.span, stack.idx);
        il.Load(stack.span, stack.idx);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(stack.idx, 1);
    }

    private static Dictionary<int, long> BuildCostLookup(ReadOnlySpan<OpcodeInfo> code)
    {
        Dictionary<int, long> costs = new();
        int costStart = 0;
        long coststack = 0;

        for (int pc = 0; pc < code.Length; pc++)
        {
            OpcodeInfo op = code[pc];
            switch (op.Operation)
            {
                case Instruction.JUMPDEST:
                    costs[costStart] = coststack; // remember the stack chain of opcodes
                    costStart = pc;
                    coststack = op.Metadata?.GasCost ?? 0;
                    break;
                case Instruction.JUMPI:
                case Instruction.JUMP:
                    coststack += op.Metadata?.GasCost ?? 0;
                    costs[costStart] = coststack; // remember the stack chain of opcodes
                    costStart = pc + 1;             // start with the next again
                    coststack = 0;
                    break;
                default:
                    coststack += op.Metadata?.GasCost ?? 0;
                    costs[pc] = coststack;
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
