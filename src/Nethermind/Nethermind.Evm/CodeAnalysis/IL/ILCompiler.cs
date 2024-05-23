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

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class ILCompiler
{
    public delegate void ExecuteSegment(ref ILEvmState state, ref EvmPooledMemory memory, byte[][] immediatesData);
    public class SegmentExecutionCtx {
        public ExecuteSegment Method;
        public byte[][] Data;
    }
    public static SegmentExecutionCtx CompileSegment(string segmentName, OpcodeInfo[] code, byte[][] data)
    {
        // code is optimistic assumes stack underflow and stack overflow to not occure (WE NEED EOF FOR THIS)
        // Note(Ayman) : What stops us from adopting stack analysis from EOF in ILVM?
        // Note(Ayman) : verify all endianness arguments and bytes

        Emit<ExecuteSegment> method = Emit<ExecuteSegment>.NewDynamicMethod(segmentName, doVerify: true, strictBranchVerification: true);

        using Local jmpDestination = method.DeclareLocal(Word.Int0Field.FieldType);
        using Local consumeJumpCondition = method.DeclareLocal(typeof(int));

        using Local address = method.DeclareLocal(typeof(Address));
        using Local uint256A = method.DeclareLocal(typeof(UInt256));
        using Local uint256B = method.DeclareLocal(typeof(UInt256));
        using Local uint256C = method.DeclareLocal(typeof(UInt256));
        using Local uint256R = method.DeclareLocal(typeof(UInt256));
        using Local localReadonOnlySpan = method.DeclareLocal(typeof(ReadOnlySpan<byte>));
        using Local uint64A = method.DeclareLocal(typeof(ulong));
        using Local uint32A = method.DeclareLocal(typeof(uint));
        using Local byte8A = method.DeclareLocal(typeof(byte));

        using Local gasAvailable = method.DeclareLocal(typeof(long));
        using Local programCounter = method.DeclareLocal(typeof(ushort));

        using Local stack = method.DeclareLocal(typeof(Word*));
        using Local currentSP = method.DeclareLocal(typeof(Word*));

        const int wordToAlignTo = 32;

        // allocate stack
        method.LoadConstant(EvmStack.MaxStackSize * Word.Size + wordToAlignTo);
        method.LocalAllocate();

        method.StoreLocal(stack);

        method.LoadLocal(stack);
        method.StoreLocal(currentSP); // copy to the currentSP

        // set gas to local
        method.LoadArgument(0);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.GasAvailable)));
        method.Convert<long>();
        method.StoreLocal(gasAvailable);

        Dictionary<EvmExceptionType, Label> labels = new();

        foreach (var exception in Enum.GetValues<EvmExceptionType>())
        {
            labels.Add(exception, method.DefineLabel(exception.ToString()));
        }

        // set pc to local
        method.LoadArgument(0);
        method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ProgramCounter)));
        method.StoreLocal(programCounter);

        Label exit = method.DefineLabel("Return"); // the label just before return
        Label jumpTable = method.DefineLabel("Jumptable"); // jump table
        Label ret = method.DefineLabel("return");

        Dictionary<int, Label> jumpDestinations = new();


        // Idea(Ayman) : implement every opcode as a method, and then inline the IL of the method in the main method

        Dictionary<int, long> gasCost = BuildCostLookup(code);
        for (int pc = 0; pc < code.Length; pc++)
        {
            OpcodeInfo op = code[pc];

            // set pc
            method.LoadConstant(op.ProgramCounter);
            method.StoreLocal(programCounter);

            // load gasAvailable
            method.LoadLocal(gasAvailable);

            // get pc gas cost
            method.LoadConstant(gasCost[pc]);

            // subtract the gas cost
            method.Subtract();

            // check if gas is available
            method.Duplicate();
            method.StoreLocal(gasAvailable);
            method.LoadConstant((long)0);

            // if gas is not available, branch to out of gas
            method.BranchIfLess(labels[EvmExceptionType.OutOfGas]);

            // else emit 
            switch (op.Operation)
            {
                case Instruction.STOP:
                    method.LoadArgument(0);
                    method.LoadConstant(true);
                    method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.StopExecution)));
                    method.Branch(ret);
                    break;
                case Instruction.JUMPDEST:
                    // mark the jump destination
                    jumpDestinations[pc] = method.DefineLabel();
                    method.MarkLabel(jumpDestinations[pc]);
                    break;
                case Instruction.JUMP:
                    // we jump into the jump table
                    method.Branch(jumpTable);
                    break;
                case Instruction.JUMPI:
                    // consume the jump condition
                    Label noJump = method.DefineLabel();
                    method.StackLoadPrevious(currentSP, 2);
                    method.Call(Word.GetIsZero, null);

                    // if the jump condition is false, we do not jump
                    method.BranchIfTrue(noJump);

                    // load the jump address
                    method.LoadConstant(1);
                    method.StoreLocal(consumeJumpCondition);

                    // we jump into the jump table
                    method.Branch(jumpTable);

                    method.MarkLabel(noJump);
                    method.StackPop(currentSP, 2);
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
                    // we load the currentSP
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);

                    // we load the span of bytes
                    method.LoadArgument(2);
                    method.LoadConstant(op.Arguments.Value);
                    method.LoadElement<byte[]>();
                    method.Call(typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(byte[]) }));
                    method.StoreLocal(localReadonOnlySpan);

                    // we call UInt256 constructor taking a span of bytes and a bool
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.LoadConstant(BitConverter.IsLittleEndian);
                    method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));

                    // we store the UInt256 in the currentSP
                    method.Call(Word.SetUInt256);
                    method.StackPush(currentSP);
                    break;
                case Instruction.ADD:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!, null, uint256A, uint256B);
                    break;

                case Instruction.SUB:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!, null, uint256A, uint256B);
                    break;

                case Instruction.MUL:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!, null, uint256A, uint256B);
                    break;

                case Instruction.MOD:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!, 
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label = il.DefineLabel();

                            il.LoadLocal(locals[1]);
                            il.LoadConstant(0);
                            il.CompareEqual();

                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, uint256A, uint256B);
                    break;

                case Instruction.DIV:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel, locals) =>
                        {
                            Label label = il.DefineLabel();

                            il.LoadLocal(locals[1]);
                            il.LoadConstant(0);
                            il.CompareEqual();

                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        }, uint256A, uint256B);
                    break;

                case Instruction.EXP:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!, null, uint256A, uint256B);
                    break;
                case Instruction.LT:
                    EmitComparaisonUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), uint256A, uint256B);
                    break;
                case Instruction.GT:
                    EmitComparaisonUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), uint256A, uint256B);
                    break;
                case Instruction.EQ:
                    EmitComparaisonUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod("op_Equality", new[] { typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType() }), uint256A, uint256B);
                    break;
                case Instruction.ISZERO:

                    // we load the currentSP
                    method.StackLoadPrevious(currentSP, 1);
                    method.Call(Word.GetIsZero);
                    method.StackPop(currentSP, 1);
                    method.StoreLocal(byte8A);

                    // we convert the result to a Uint256 and store it in the currentSP
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadLocal(byte8A);
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.Byte0)));
                    method.StackPush(currentSP);
                    break;
                case Instruction.POP:
                    method.StackPop(currentSP);
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
                    method.LoadLocal(currentSP);
                    method.StackLoadPrevious(currentSP, count);
                    method.LoadObject(typeof(Word));
                    method.StoreObject(typeof(Word));
                    method.StackPush(currentSP);
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
                    method.StackLoadPrevious(currentSP, 1);
                    method.LoadObject(typeof(Word));
                    method.StoreObject(typeof(Word));

                    method.StackLoadPrevious(currentSP, 1);
                    method.StackLoadPrevious(currentSP, count);
                    method.LoadObject(typeof(Word));
                    method.StoreObject(typeof(Word));

                    method.StackLoadPrevious(currentSP, count);
                    method.LoadLocalAddress(uint256R);
                    method.LoadObject(typeof(Word));
                    method.StoreObject(typeof(Word));
                    break;

                // Note(Ayman): following opcode need double checking
                // is pushing to stack happening correctly
                case Instruction.CODESIZE:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadConstant(code.Length);
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.UInt0)));
                    method.StackPush(currentSP);
                    break;
                case Instruction.PC:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadLocal(programCounter);
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.UInt0)));
                    method.StackPush(currentSP);
                    break;
                case Instruction.COINBASE:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Header)));
                    method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasBeneficiary), false, out _));
                    method.Call(Word.SetAddress);
                    method.StackPush(currentSP);
                    break;
                case Instruction.TIMESTAMP:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Header)));
                    method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Timestamp), false, out _));
                    method.Call(Word.SetUInt256);
                    method.StackPush(currentSP);
                    break;
                case Instruction.NUMBER:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Header)));
                    method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.Number), false, out _));
                    method.Call(Word.SetUInt256);
                    method.StackPush(currentSP);
                    break;
                case Instruction.GASLIMIT:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Header)));
                    method.Call(GetPropertyInfo<BlockHeader>(nameof(BlockHeader.GasLimit), false, out _));
                    method.Call(Word.SetUInt256);
                    method.StackPush(currentSP);
                    break;
                case Instruction.CALLER:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(ExecutionEnvironment), nameof(ExecutionEnvironment.Caller)));
                    method.Call(Word.SetAddress);
                    method.StackPush(currentSP);
                    break;
                case Instruction.ADDRESS:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(TxExecutionContext), nameof(ExecutionEnvironment.ExecutingAccount)));
                    method.Call(Word.SetAddress);
                    method.StackPush(currentSP);
                    break;
                case Instruction.ORIGIN:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.TxCtx)));
                    method.LoadField(GetFieldInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.Origin)));
                    method.Call(Word.SetAddress);
                    method.StackPush(currentSP);
                    break;
                case Instruction.CALLVALUE:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(TxExecutionContext), nameof(ExecutionEnvironment.Value)));
                    method.Call(Word.SetUInt256);
                    method.StackPush(currentSP);
                    break;
                case Instruction.GASPRICE:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(TxExecutionContext), nameof(TxExecutionContext.GasPrice)));
                    method.Call(Word.SetUInt256);
                    method.StackPush(currentSP);
                    break;

                case Instruction.CALLDATALOAD:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(TxExecutionContext), nameof(ExecutionEnvironment.InputData)));
                    method.StackLoadPrevious(currentSP, 1);
                    method.LoadConstant(32);
                    method.Call(typeof(ByteArrayExtensions).GetMethod(nameof(ByteArrayExtensions.SliceWithZeroPadding), BindingFlags.Static | BindingFlags.Public));
                    method.Call(typeof(ZeroPaddedSpan).GetMethod(nameof(ZeroPaddedSpan.ToArray), BindingFlags.Instance | BindingFlags.Public));
                    method.StoreLocal(localReadonOnlySpan);
                    // we call UInt256 constructor taking a span of bytes and a bool
                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.LoadConstant(BitConverter.IsLittleEndian);
                    method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));

                    method.Call(Word.SetUInt256);
                    method.StackPush(currentSP);
                    break;
                case Instruction.CALLDATASIZE:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);
                    method.LoadArgument(0);
                    method.LoadField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Env)));
                    method.LoadField(GetFieldInfo(typeof(TxExecutionContext), nameof(ExecutionEnvironment.InputData)));
                    method.Call(GetPropertyInfo<ReadOnlyMemory<byte>>(nameof(ReadOnlyMemory<byte>.Length), false, out _));
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.Int0)));
                    method.StackPush(currentSP);
                    break;

                case Instruction.MSIZE:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);

                    method.LoadArgumentAddress(1);
                    method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
                    method.StoreField(GetFieldInfo(typeof(Word), nameof(Word.Int0)));
                    method.StackPush(currentSP);
                    break;
                case Instruction.MSTORE:
                    method.StackLoadPrevious(currentSP, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackLoadPrevious(currentSP, 2);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256B);
                    method.StackPop(currentSP, 2);

                    method.LoadArgument(1);
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadConstant(32);
                    method.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                    method.StoreLocal(uint256C);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(labels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(1);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.LoadConstant(32);
                    method.Call(typeof(UInt256).GetMethod(nameof(UInt256.PaddedBytes)));
                    method.Call(typeof(Span<byte>).GetMethod("op_Implicit", new[] { typeof(byte[]) }));
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveWord)));
                    break;
                case Instruction.MSTORE8:
                    method.StackLoadPrevious(currentSP, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackLoadPrevious(currentSP, 2);
                    method.LoadField(Word.Byte0Field);
                    method.StoreLocal(byte8A);
                    method.StackPop(currentSP, 2);

                    method.LoadArgument(1);
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadConstant(1);
                    method.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                    method.StoreLocal(uint256C);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(labels[EvmExceptionType.OutOfGas]);

                    method.LoadArgumentAddress(1);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.LoadConstant(32);
                    method.Call(typeof(UInt256).GetMethod(nameof(UInt256.PaddedBytes)));
                    method.LoadConstant(0);
                    method.LoadElement<byte>();

                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.SaveByte)));
                    break;
                case Instruction.MLOAD:
                    method.CleanWord(currentSP);
                    method.LoadLocal(currentSP);

                    method.StackLoadPrevious(currentSP, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);
                    method.StackPop(currentSP, 1);

                    method.LoadArgument(1);
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadConstant(32);
                    method.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
                    method.StoreLocal(uint256C);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(labels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(1);
                    method.LoadLocalAddress(uint256A);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType()]));
                    method.Call(typeof(Span<byte>).GetMethod("op_Implicit", new[] { typeof(Span<byte>) }));
                    method.StoreLocal(localReadonOnlySpan);

                    method.LoadLocalAddress(localReadonOnlySpan);
                    method.LoadConstant(BitConverter.IsLittleEndian);
                    method.NewObject(typeof(UInt256), typeof(ReadOnlySpan<byte>).MakeByRefType(), typeof(bool));

                    method.Call(Word.SetUInt256);
                    method.StackPush(currentSP);
                    break;
                case Instruction.MCOPY:
                    method.StackLoadPrevious(currentSP, 1);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256A);

                    method.StackLoadPrevious(currentSP, 2);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256B);

                    method.StackLoadPrevious(currentSP, 3);
                    method.Call(Word.GetUInt256);
                    method.StoreLocal(uint256C);

                    method.StackPop(currentSP, 3);

                    method.LoadLocal(gasAvailable);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Div32Ceiling)));
                    method.LoadConstant(GasCostOf.VeryLow);
                    method.Multiply();
                    method.Subtract();
                    method.StoreLocal(gasAvailable);

                    method.LoadArgument(1);
                    method.LoadLocalAddress(gasAvailable);
                    method.LoadLocalAddress(uint256A);
                    method.LoadLocalAddress(uint256B);
                    method.Call(typeof(UInt256).GetMethod(nameof(UInt256.Max)));
                    method.StoreLocal(uint256R);
                    method.LoadLocalAddress(uint256R);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(VirtualMachine<VirtualMachine.NotTracing>).GetMethod(nameof(VirtualMachine<VirtualMachine.NotTracing>.UpdateMemoryCost)));
                    method.BranchIfFalse(labels[EvmExceptionType.OutOfGas]);

                    method.LoadArgument(1);
                    method.LoadLocalAddress(uint256A);
                    method.LoadArgument(1);
                    method.LoadLocalAddress(uint256B);
                    method.LoadLocalAddress(uint256C);
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.LoadSpan), [typeof(UInt256).MakeByRefType(), typeof(UInt256).MakeByRefType()]));
                    method.Call(typeof(EvmPooledMemory).GetMethod(nameof(EvmPooledMemory.Save), [typeof(UInt256).MakeByRefType(), typeof(Span<byte>)]));
                    break;
                default:
                    throw new NotSupportedException();
            }
        }


        // prepare ILEvmState
        // check if returnState is null
        method.MarkLabel(ret);
        // we get stack size
        method.LoadLocal(currentSP);
        method.LoadLocal(stack);
        method.Subtract();
        method.Convert<uint>();
        method.StoreLocal(uint32A);

        // set stack
        method.LoadArgument(0);
        method.LoadLocal(stack);
        method.Convert<nuint>();
        method.Call(typeof(nuint).GetMethods().Where(method => method.Name == "op_Explicit" && method.ReturnType == typeof(void*)).FirstOrDefault());
        method.LoadLocal(uint32A);
        method.NewObject(typeof(Span<byte>).GetConstructor([typeof(void*), typeof(int)]));
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.Stack)));

        // set gas available
        method.LoadArgument(0);
        method.LoadLocal(gasAvailable);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.GasAvailable)));

        // set program counter
        method.LoadArgument(0);
        method.LoadLocal(programCounter);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.ProgramCounter)));

        // set exception
        method.LoadArgument(0);
        method.LoadConstant((int)EvmExceptionType.None);
        method.StoreField(GetFieldInfo(typeof(ILEvmState), nameof(ILEvmState.EvmException)));

        // go to return
        method.Branch(exit);


        // jump table
        method.MarkLabel(jumpTable);
        method.StackPop(currentSP);

        // emit the jump table

        // load the jump destination
        method.LoadLocal(currentSP);
        method.Call(Word.GetUInt256);
        method.StoreLocal(uint256A);

        // if (jumpDest > uint.MaxValue)
        method.LoadLocalAddress(uint256B);
        method.LoadConstant(uint.MaxValue);
        method.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        // goto invalid address
        method.BranchIfTrue(labels[EvmExceptionType.InvalidJumpDestination]);
        // else

        const int jumpFanOutLog = 7; // 128
        const int bitMask = (1 << jumpFanOutLog) - 1;
        Label[] jumps = new Label[jumpFanOutLog];
        for (int i = 0; i < jumpFanOutLog; i++)
        {
            jumps[i] = method.DefineLabel();
        }

        // we get first 32 bits of the jump destination since it is less than int.MaxValue
        method.Load(currentSP, Word.Int0Field);
        method.Call(typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), BindingFlags.Public | BindingFlags.Static, new[] { typeof(uint) }), null);
        method.StoreLocal(jmpDestination);

        method.LoadLocal(jmpDestination);
        method.LoadConstant(bitMask);
        method.And();

        // switch on the first 7 bits
        method.Switch(jumps);

        for (int i = 0; i < jumpFanOutLog; i++)
        {
            method.MarkLabel(jumps[i]);

            // for each destination matching the bit mask emit check for the equality
            foreach (int dest in jumpDestinations.Keys.Where(dest => (dest & bitMask) == i))
            {
                method.LoadLocal(jmpDestination);
                method.LoadConstant(dest);
                method.BranchIfEqual(jumpDestinations[dest]);
            }

            // each bucket ends with a jump to invalid access to do not fall through to another one
            method.Branch(labels[EvmExceptionType.InvalidJumpDestination]);
        }

        foreach (var kvp in labels)
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

        ExecuteSegment dynEmitedDelegate = method.CreateDelegate();
        return new SegmentExecutionCtx
        {
            Method = dynEmitedDelegate,
            Data = data
        };
    }

    private static void EmitComparaisonUInt256Method<T>(Emit<T> il, Local uint256R, Local currentSP, MethodInfo operation, params Local[] locals)
    {
        // we the two uint256 from the stack
        il.StackLoadPrevious(currentSP, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(currentSP, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(locals[0]);
        il.Call(operation, null);

        // convert to conv_i
        il.Convert<int>();
        il.Call(typeof(UInt256).GetMethod("op_Explicit", new[] { typeof(int) }));
        il.StoreLocal(uint256R);
        il.StackPop(currentSP, 2);

        // push the result to the stack
        il.CleanWord(currentSP);
        il.LoadLocal(currentSP);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(currentSP);
    }

    private static void EmitBinaryUInt256Method<T>(Emit<T> il, Local uint256R, Local currentSP, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, params Local[] locals)
    {
        Label label = il.DefineLabel("SkipHandlingBinaryOp");

        // we the two uint256 from the stack
        il.StackLoadPrevious(currentSP, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(currentSP, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.StackPop(currentSP, 2);

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
        il.CleanWord(currentSP);
        il.LoadLocal(currentSP);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(currentSP);
    }

    private static Dictionary<int, long> BuildCostLookup(ReadOnlySpan<OpcodeInfo> code)
    {
        Dictionary<int, long> costs = new();
        int costStart = 0;
        long costcurrentSP = 0;

        for (int pc = 0; pc < code.Length; pc++)
        {
            OpcodeInfo op = code[pc];
            switch (op.Operation)
            {
                case Instruction.JUMPDEST:
                    costs[costStart] = costcurrentSP; // remember the currentSP chain of opcodes
                    costStart = pc;
                    costcurrentSP = op.Metadata?.GasCost ?? 0;
                    break;
                case Instruction.JUMPI:
                case Instruction.JUMP:
                    costcurrentSP += op.Metadata?.GasCost ?? 0;
                    costs[costStart] = costcurrentSP; // remember the currentSP chain of opcodes
                    costStart = pc + 1;             // start with the next again
                    costcurrentSP = 0;
                    break;
                default:
                    costcurrentSP += op.Metadata?.GasCost ?? 0;
                    costs[pc] = costcurrentSP;
                    break;
            }
        }

        if (costcurrentSP > 0)
        {
            costs[costStart] = costcurrentSP;
        }

        return costs;
    }
}
