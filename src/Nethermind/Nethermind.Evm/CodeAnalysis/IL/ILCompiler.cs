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
using ProjectedEvmState = (Nethermind.Evm.EvmExceptionType, Nethermind.Evm.CodeAnalysis.IL.ILEvmState);

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class ILCompiler
{
    public static Func<long, ProjectedEvmState> CompileSegment(string segmentName, OpcodeInfo[] code)
    {
        Emit<Func<long, ProjectedEvmState>> method = Emit<Func<long, ProjectedEvmState>>.NewDynamicMethod(segmentName, doVerify: true, strictBranchVerification: true);

        using Local jmpDestination = method.DeclareLocal(Word.Int0Field.FieldType);
        using Local address = method.DeclareLocal(typeof(Address));
        using Local consumeJumpCondition = method.DeclareLocal(typeof(int));
        using Local uint256A = method.DeclareLocal(typeof(UInt256));
        using Local uint256B = method.DeclareLocal(typeof(UInt256));
        using Local uint256C = method.DeclareLocal(typeof(UInt256));
        using Local uint256R = method.DeclareLocal(typeof(UInt256));
        using Local returnState = method.DeclareLocal(typeof(EvmState));
        using Local gasAvailable = method.DeclareLocal(typeof(long));

        using Local stack = method.DeclareLocal(typeof(Word*));
        using Local currentSP = method.DeclareLocal(typeof(Word*));

        using Local memory = method.DeclareLocal(typeof(byte*));

        const int wordToAlignTo = 32;


        // allocate stack
        method.LoadConstant(EvmStack.MaxStackSize * Word.Size + wordToAlignTo);
        method.LocalAllocate();

        method.StoreLocal(stack);

        method.LoadLocal(stack);
        method.StoreLocal(currentSP); // copy to the currentSP

        // gas
        method.LoadArgument(0);
        method.StoreLocal(gasAvailable);
        Label outOfGas = method.DefineLabel("OutOfGas");

        Label ret = method.DefineLabel("Return"); // the label just before return
        Label invalidAddress = method.DefineLabel("InvalidAddress"); // invalid jump address
        Label jumpTable = method.DefineLabel("Jumptable"); // jump table

        Dictionary<int, Label> jumpDestinations = new();


        // Idea(Ayman) : implement every opcode as a method, and then inline the IL of the method in the main method

        Dictionary<int, long> gasCost = BuildCostLookup(code);
        for (int pc = 0; pc < code.Length; pc++)
        {
            OpcodeInfo op = code[pc];

            // load gasAvailable
            method.LoadLocal(gasAvailable);

            // get pc gas cost
            method.LoadConstant(gasCost[pc]);
            method.Subtract();
            method.Duplicate();
            method.LoadConstant(0);
            method.BranchIfLess(outOfGas);
            method.StoreLocal(gasAvailable);

            switch (op.Operation)
            {
                case Instruction.JUMPDEST:
                    jumpDestinations[pc] = method.DefineLabel();
                    method.MarkLabel(jumpDestinations[pc]);
                    break;
                case Instruction.JUMP:
                    method.Branch(jumpTable);
                    break;
                case Instruction.JUMPI:
                    Label noJump = method.DefineLabel();
                    method.StackLoadPrevious(currentSP, 2);
                    method.Call(Word.GetIsZero, null);
                    method.BranchIfTrue(noJump);

                    // load the jump address
                    method.LoadConstant(1);
                    method.StoreLocal(consumeJumpCondition);
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
                    int count = (int)op.Operation - (int)Instruction.PUSH0;
                    ZeroPaddedSpan bytes = new ZeroPaddedSpan(op.Arguments.Value.Span, 32 - count, PadDirection.Left);
                    method.LoadArray(bytes.ToArray());
                    method.LoadConstant(0);
                    method.NewObject(typeof(UInt256), typeof(Span<byte>), typeof(bool));

                    method.StackPush(currentSP);
                    break;
                case Instruction.ADD:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!);
                    break;

                case Instruction.SUB:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!);
                    break;

                case Instruction.MUL:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!);
                    break;

                case Instruction.MOD:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel) =>
                        {
                            Label label = il.DefineLabel();

                            il.Duplicate();
                            il.LoadConstant(0);
                            il.CompareEqual();

                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        });
                    break;

                case Instruction.DIV:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!,
                        (il, postInstructionLabel) =>
                        {
                            Label label = il.DefineLabel();

                            il.Duplicate();
                            il.LoadConstant(0);
                            il.CompareEqual();

                            il.BranchIfFalse(label);

                            il.LoadConstant(0);
                            il.Branch(postInstructionLabel);

                            il.MarkLabel(label);
                        });
                    break;

                case Instruction.EXP:
                    EmitBinaryUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!);
                    break;
                case Instruction.LT:
                    EmitComparaisonUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256), typeof(UInt256) }));
                    break;
                case Instruction.GT:
                    EmitComparaisonUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256), typeof(UInt256) }));
                    break;
                case Instruction.EQ:
                    EmitComparaisonUInt256Method(method, uint256R, currentSP, typeof(UInt256).GetMethod("op_Equality", new[] { typeof(UInt256), typeof(UInt256) }));
                    break;
                case Instruction.ISZERO:
                    method.StackLoadPrevious(currentSP, 1);
                    method.Call(Word.GetIsZero);
                    method.StackPush(currentSP);
                    method.StackPop(currentSP, 1);
                    break;
                case Instruction.CODESIZE:
                    method.LoadConstant(code.Length);
                    method.Call(typeof(UInt256).GetMethod("op_Implicit", new[] { typeof(int) }));
                    method.StackPush(currentSP);
                    break;
                case Instruction.POP:
                    method.Pop();
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
                    count = (int)op.Operation - (int)Instruction.DUP1 + 1;
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
                default:
                    throw new NotSupportedException();
            }
        }

        // prepare ILEvmState
        method.MarkLabel(ret);

        method.LoadLocal(currentSP);
        method.LoadLocal(stack);
        method.Subtract();
        method.LoadConstant(Word.Size);
        method.Divide();
        method.StoreLocal(uint256R);

        method.LoadLocal(uint256R);
        method.NewArray<Word>();
        // pop the stack to the array
        method.ForBranch(uint256R, (il, i) =>
        {
            il.Duplicate();

            il.LoadLocal(i);
            il.StackLoadPrevious(currentSP);
            il.LoadObject(typeof(Word));
            il.StoreElement<Word>();

            il.StackPop(currentSP);
        });


        method.LoadConstant((int)EvmExceptionType.None);
        method.Branch(ret);

        method.MarkLabel(jumpTable);
        method.StackPop(currentSP);

        // emit the jump table
        // if (jumpDest > uint.MaxValue)
        // ULong3 | Ulong2 | Ulong1 | Uint1 | Ushort1
        method.LoadLocal(currentSP);
        method.LoadConstant(uint.MaxValue);
        method.Call(Word.GetUInt256);
        method.Call(typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256), typeof(UInt256) }));

        method.BranchIfTrue(invalidAddress);

        const int jumpFanOutLog = 7; // 128
        const int bitMask = (1 << jumpFanOutLog) - 1;
        Label[] jumps = new Label[jumpFanOutLog];
        for (int i = 0; i < jumpFanOutLog; i++)
        {
            jumps[i] = method.DefineLabel();
        }

        method.Load(currentSP, Word.Int0Field);
        method.Call(typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), BindingFlags.Public | BindingFlags.Static, new[] { typeof(uint) }), null);
        method.StoreLocal(jmpDestination);

        method.LoadLocal(jmpDestination);
        method.LoadConstant(bitMask);
        method.And();

        method.Switch(jumps);
        int[] destinations = jumpDestinations.Keys.ToArray();

        for (int i = 0; i < jumpFanOutLog; i++)
        {
            method.MarkLabel(jumps[i]);

            // for each destination matching the bit mask emit check for the equality
            foreach (int dest in destinations.Where(dest => (dest & bitMask) == i))
            {
                method.LoadLocal(jmpDestination);
                method.LoadConstant(dest);
                method.BranchIfEqual(jumpDestinations[dest]);
            }

            // each bucket ends with a jump to invalid access to do not fall through to another one
            method.Branch(invalidAddress);
        }

        // out of gas
        method.MarkLabel(outOfGas);
        method.LoadConstant((int)EvmExceptionType.OutOfGas);
        method.Branch(ret);

        // invalid address return
        method.MarkLabel(invalidAddress);
        method.LoadConstant((int)EvmExceptionType.InvalidJumpDestination);
        method.Branch(ret);

        // pop items from 


        // return
        method.Return();

        Func<long, ProjectedEvmState> del = method.CreateDelegate();
        return del;
    }

    private static void EmitComparaisonUInt256Method<T>(Emit<T> il, Local uint256R, Local currentSP, MethodInfo operatin)
    {
        il.StackLoadPrevious(currentSP, 1);
        il.Call(Word.GetUInt256);
        il.StackLoadPrevious(currentSP, 2);
        il.Call(Word.GetUInt256);
        // invoke op < on the uint256
        il.Call(operatin, null);
        // if true, push 1, else 0
        il.LoadConstant(0);
        il.CompareEqual();

        // convert to conv_i
        il.Convert<int>();
        il.Call(typeof(UInt256).GetMethod("op_Implicit", new[] { typeof(int) }));
        il.StoreLocal(uint256R);
        il.StackPop(currentSP, 2);

        il.LoadLocal(currentSP);
        il.LoadLocal(uint256R); // stack: word*, uint256
        il.Call(Word.SetUInt256);
        il.StackPush(currentSP);
    }

    private static void EmitBinaryUInt256Method<T>(Emit<T> il, Local uint256R, Local currentSP, MethodInfo operation, Action<Emit<T>, Label> customHandling = null)
    {
        Label label = il.DefineLabel();

        il.StackLoadPrevious(currentSP, 1);
        il.Call(Word.GetUInt256);
        il.StackLoadPrevious(currentSP, 2);
        il.Call(Word.GetUInt256);

        customHandling.Invoke(il, label);

        il.Call(operation);
        il.StoreLocal(uint256R);
        il.StackPop(currentSP, 2);

        il.MarkLabel(label);
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
                    break;
            }

            pc += op.Metadata?.AdditionalBytes ?? 0;
        }

        if (costcurrentSP > 0)
        {
            costs[costStart] = costcurrentSP;
        }

        return costs;
    }
}
