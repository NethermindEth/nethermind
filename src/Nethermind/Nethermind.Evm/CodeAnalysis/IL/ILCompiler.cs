// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.IL;
using Nethermind.Int256;
using Sigil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using Label = System.Reflection.Emit.Label;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class ILCompiler
{
    public static Func<long, EvmExceptionType> Build(byte[] code)
    {
        Dictionary<int, long> gasCost = BuildCostLookup(code);

        // TODO: stack invariants, gasCost application

        string name = "ILVM_" + Guid.NewGuid();

        DynamicMethod method = new(name, typeof(EvmExceptionType), new[] { typeof(long) }, typeof(ILCompiler).Assembly.Modules.First(), true)
        {
            InitLocals = false
        };

        ILGenerator il = method.GetILGenerator();

        LocalBuilder jmpDestination = il.DeclareLocal(Word.Int0Field.FieldType);
        LocalBuilder address = il.DeclareLocal(typeof(Address));
        LocalBuilder consumeJumpCondition = il.DeclareLocal(typeof(int));
        LocalBuilder uint256A = il.DeclareLocal(typeof(UInt256));
        LocalBuilder uint256B = il.DeclareLocal(typeof(UInt256));
        LocalBuilder uint256C = il.DeclareLocal(typeof(UInt256));
        LocalBuilder uint256R = il.DeclareLocal(typeof(UInt256));
        LocalBuilder gasAvailable = il.DeclareLocal(typeof(long));

        LocalBuilder stack = il.DeclareLocal(typeof(Word*));
        LocalBuilder current = il.DeclareLocal(typeof(Word*));

        const int wordToAlignTo = 32;

        il.Emit(OpCodes.Ldc_I4, EvmStack.MaxStackSize * Word.Size + wordToAlignTo);
        il.Emit(OpCodes.Localloc);

        // align to the boundary, so that the Word can be written using the aligned longs.
        il.LoadValue(wordToAlignTo);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4, ~(wordToAlignTo - 1));
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.And);

        il.Store(stack); // store as start

        il.Load(stack);
        il.Store(current); // copy to the current

        // gas
        il.Emit(OpCodes.Ldarg_0);
        il.Store(gasAvailable);
        Label outOfGas = il.DefineLabel();

        Label ret = il.DefineLabel(); // the label just before return
        Label invalidAddress = il.DefineLabel(); // invalid jump address
        Label jumpTable = il.DefineLabel(); // jump table

        Dictionary<int, Label> jumpDestinations = new();

        for(int pc = 0; pc < code.Length; pc++)
        {
            OpcodeInfo op = OpcodeInfo.Operations[(Instruction)code[pc]];

            // load gasAvailable
            il.Emit(OpCodes.Ldloc, gasAvailable);

            // get pc gas cost
            il.LoadValue(gasCost[pc]);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Blt, outOfGas);
            il.Store(gasAvailable);

            switch (op.Instruction)
            {
                case Instruction.JUMPDEST:
                    jumpDestinations[pc] = il.DefineLabel();
                    il.MarkLabel(jumpDestinations[pc]);
                    break;
                case Instruction.JUMP:
                    il.Emit(OpCodes.Br , jumpTable);
                    break;
                case Instruction.JUMPI:
                    Label noJump = il.DefineLabel();
                    il.StackLoadPrevious(current, 2);
                    il.EmitCall(OpCodes.Call, Word.GetIsZero, null);
                    il.Emit(OpCodes.Brtrue, noJump);

                    // load the jump address
                    il.LoadValue(1);
                    il.Store(consumeJumpCondition);
                    il.Emit(OpCodes.Br, jumpTable);

                    il.MarkLabel(noJump);
                    il.StackPop(current, 2);    
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
                    Span<byte> bytes = code.AsSpan(pc + 1, op.AdditionalBytes);
                    il.LoadArray(bytes);
                    il.LoadValue(0);
                    il.EmitCall(OpCodes.Call, typeof(BitConverter).GetProperty(nameof(BitConverter.IsLittleEndian)).GetMethod, null);
                    il.Emit(OpCodes.Newobj, typeof(UInt256).GetConstructor(new[] { typeof(Span<byte>), typeof(bool) }));

                    il.StackPush(current);
                    pc += op.AdditionalBytes;
                    break;
                case Instruction.ADD:
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Add), BindingFlags.Public | BindingFlags.Static)!);
                    break;

                case Instruction.SUB:
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!);
                    break;

                case Instruction.MUL:
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Multiply), BindingFlags.Public | BindingFlags.Static)!);
                    break;

                case Instruction.MOD:
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!);
                    break;

                case Instruction.DIV:
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!);
                    break;

                case Instruction.EXP:
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!);
                    break;
            }
        }
    }

    private static void EmitBinaryUInt256Method(ILGenerator il, LocalBuilder uint256R, LocalBuilder current, MethodInfo operation)
    {
        il.StackLoadPrevious(current, 1);
        il.EmitCall(OpCodes.Call, Word.GetUInt256, null);
        il.StackLoadPrevious(current, 2);
        il.EmitCall(OpCodes.Call, Word.GetUInt256, null);
        il.EmitCall(OpCodes.Call, operation, null);
        il.Store(uint256R);
        il.StackPop(current, 2);

        il.Load(current);
        il.Load(uint256R); // stack: word*, uint256
        il.EmitCall(OpCodes.Call, Word.SetUInt256, null);
        il.StackPush(current);
    }

    private static Dictionary<int, long> BuildCostLookup(ReadOnlySpan<byte> code)
    {
        Dictionary<int, long> costs = new();
        int costStart = 0;
        long costCurrent = 0;

        for (int pc = 0; pc < code.Length; pc++)
        {
            OpcodeInfo op = OpcodeInfo.Operations[(Instruction)code[pc]]
            switch (op.Instruction)
            {
                case Instruction.JUMPDEST:
                    costs[costStart] = costCurrent; // remember the current chain of opcodes
                    costStart = pc;
                    costCurrent = op.GasCost;
                    break;
                case Instruction.JUMPI:
                case Instruction.JUMP:
                    costCurrent += op.GasCost;
                    costs[costStart] = costCurrent; // remember the current chain of opcodes
                    costStart = pc + 1;             // start with the next again
                    costCurrent = 0;
                    break;
                default:
                    costCurrent += op.GasCost;
                    break;
            }

            pc += op.AdditionalBytes;
        }

        if (costCurrent > 0)
        {
            costs[costStart] = costCurrent;
        }

        return costs;
    }
}
