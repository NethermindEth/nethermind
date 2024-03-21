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
using System.Text;
using System.Threading.Tasks;
using Label = Sigil.Label;

namespace Nethermind.Evm.CodeAnalysis.IL;
internal class ILCompiler
{
    public static Func<long, EvmExceptionType> Build(CodeInfo codeinfo, int segmentId)
    {
        Dictionary<int, long> gasCost = BuildCostLookup(codeinfo.IlInfo.Segments[segmentId]);

        // TODO: stack invariants, gasCost application

        string name = "ILVM_" + Guid.NewGuid();

        Emit<Action> method = Emit<Action>.NewDynamicMethod($"{codeinfo.GetHashCode()}_{segmentId}", doVerify: true, strictBranchVerification: true);

        Local jmpDestination = method.DeclareLocal(Word.Int0Field.FieldType);
        Local address = method.DeclareLocal(typeof(Address));
        Local consumeJumpCondition = method.DeclareLocal(typeof(int));
        Local uint256A = method.DeclareLocal(typeof(UInt256));
        Local uint256B = method.DeclareLocal(typeof(UInt256));
        Local uint256C = method.DeclareLocal(typeof(UInt256));
        Local uint256R = method.DeclareLocal(typeof(UInt256));
        Local gasAvailable = method.DeclareLocal(typeof(long));

        Local stack = method.DeclareLocal(typeof(Word*));
        Local current = method.DeclareLocal(typeof(Word*));

        const int wordToAlignTo = 32;


        // allocate stack
        method.LoadConstant(EvmStack.MaxStackSize * Word.Size + wordToAlignTo);
        method.LocalAllocate();

        method.StoreLocal(stack);

        method.LoadLocal(stack);
        method.StoreLocal(current); // copy to the current

        // gas
        method.LoadArgument(0);
        method.StoreLocal(gasAvailable);
        Label outOfGas = method.DefineLabel("OutOfGas");

        Label ret = method.DefineLabel("Return"); // the label just before return
        Label invalidAddress = method.DefineLabel("InvalidAddress"); // invalid jump address
        Label jumpTable = method.DefineLabel("Jumptable"); // jump table

        Dictionary<int, Label> jumpDestinations = new();


        OpcodeInfo[] code = codeinfo.IlInfo.Segments[segmentId];
        for(int pc = 0; pc < code.Length; pc++)
        {
            OpcodeInfo op = code[pc];

            // load gasAvailable
            il.Emit(OpCodes.Ldloc, gasAvailable);

            // get pc gas cost
            il.LoadValue(gasCost[pc]);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Blt, outOfGas);
            il.Store(gasAvailable);

            switch (op.Operation)
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
                    int count = (int)op.Operation - (int)Instruction.PUSH0;
                    ZeroPaddedSpan bytes = new ZeroPaddedSpan(op.Arguments.Value.Span, 32 - count, PadDirection.Left);
                    il.LoadArray(bytes.ToArray());
                    il.LoadValue(0);
                    il.LoadValue(0);
                    il.Emit(OpCodes.Newobj, typeof(UInt256).GetConstructor(new[] { typeof(Span<byte>), typeof(bool) }));

                    il.StackPush(current);
                    pc += op.Metadata?.AdditionalBytes ?? 0;
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
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Mod), BindingFlags.Public | BindingFlags.Static)!,
                        il => {
                            Label label = il.DefineLabel();

                            il.Emit(OpCodes.Dup);
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ceq);

                            il.Emit(OpCodes.Brfalse, label);

                            il.Emit(OpCodes.Ldc_I4_0);

                            il.MarkLabel(label);
                        });
                    break;

                case Instruction.DIV:
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Divide), BindingFlags.Public | BindingFlags.Static)!,
                        il => {
                            Label label = il.DefineLabel();

                            il.Emit(OpCodes.Dup);
                            il.Emit(OpCodes.Ldc_I4_0);
                            il.Emit(OpCodes.Ceq);

                            il.Emit(OpCodes.Brfalse, label);

                            il.Emit(OpCodes.Ldc_I4_0);

                            il.MarkLabel(label);
                        });
                    break;

                case Instruction.EXP:
                    EmitBinaryUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod(nameof(UInt256.Exp), BindingFlags.Public | BindingFlags.Static)!);
                    break;
                case Instruction.LT:
                    EmitComparaisonUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256), typeof(UInt256) }));
                    break;
                case Instruction.GT:
                    EmitComparaisonUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod("op_GreaterThan", new[] { typeof(UInt256), typeof(UInt256) }));
                    break;
                case Instruction.EQ:
                    EmitComparaisonUInt256Method(il, uint256R, current, typeof(UInt256).GetMethod("op_Equality", new[] { typeof(UInt256), typeof(UInt256) }));
                    break;
                case Instruction.ISZERO:
                    il.StackLoadPrevious(current, 1);
                    il.EmitCall(OpCodes.Call, Word.GetIsZero, null);
                    il.StackPush(current);
                    il.StackPop(current, 1);
                    break;
                case Instruction.CODESIZE:
                    il.LoadValue(code.Length);
                    il.EmitCall(OpCodes.Call, typeof(UInt256).GetMethod("op_Implicit", new[] { typeof(int) }), null);
                    il.StackPush(current);
                    break;
                case Instruction.POP:
                    il.StackPop(current);
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
                    il.Load(current);
                    il.StackLoadPrevious(current, count);
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word));
                    il.StackPush(current);

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

                    il.LoadAddress(uint256R);
                    il.StackLoadPrevious(current, 1);
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word));

                    il.StackLoadPrevious(current, 1);
                    il.StackLoadPrevious(current, count);
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word));

                    il.StackLoadPrevious(current, count);
                    il.LoadAddress(uint256R);
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word));
                    break;


            }
        }

        Func<long, EvmExceptionType> del = method.CreateDelegate<Func<long, EvmExceptionType>>();
        return del;
    }

    private static void EmitComparaisonUInt256Method(ILGenerator il, LocalBuilder uint256R, LocalBuilder current, MethodInfo operatin)
    {
        il.StackLoadPrevious(current, 1);
        il.EmitCall(OpCodes.Call, Word.GetUInt256, null);
        il.StackLoadPrevious(current, 2);
        il.EmitCall(OpCodes.Call, Word.GetUInt256, null);
        // invoke op < on the uint256
        il.EmitCall(OpCodes.Call, operatin, null);
        // if true, push 1, else 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);

        // convert to conv_i
        il.Emit(OpCodes.Conv_I);
        il.EmitCall(OpCodes.Call, typeof(UInt256).GetMethod("op_Implicit", new[] { typeof(int) }), null);
        il.Store(uint256R);
        il.StackPop(current, 2);

        il.Load(current);
        il.Load(uint256R); // stack: word*, uint256
        il.EmitCall(OpCodes.Call, Word.SetUInt256, null);
        il.StackPush(current);
    }

    private static void EmitBinaryUInt256Method(ILGenerator il, LocalBuilder uint256R, LocalBuilder current, MethodInfo operation, Action<ILGenerator> customHandling = null)
    {
        il.StackLoadPrevious(current, 1);
        il.EmitCall(OpCodes.Call, Word.GetUInt256, null);
        il.StackLoadPrevious(current, 2);
        il.EmitCall(OpCodes.Call, Word.GetUInt256, null);

        customHandling.Invoke(il);


        il.EmitCall(OpCodes.Call, operation, null);
        il.Store(uint256R);
        il.StackPop(current, 2);

        il.Load(current);
        il.Load(uint256R); // stack: word*, uint256
        il.EmitCall(OpCodes.Call, Word.SetUInt256, null);
        il.StackPush(current);
    }

    private static Dictionary<int, long> BuildCostLookup(ReadOnlySpan<OpcodeInfo> code)
    {
        Dictionary<int, long> costs = new();
        int costStart = 0;
        long costCurrent = 0;

        for (int pc = 0; pc < code.Length; pc++)
        {
            OpcodeInfo op = code[pc];
            switch (op.Operation)
            {
                case Instruction.JUMPDEST:
                    costs[costStart] = costCurrent; // remember the current chain of opcodes
                    costStart = pc;
                    costCurrent = op.Metadata?.GasCost ?? 0;
                    break;
                case Instruction.JUMPI:
                case Instruction.JUMP:
                    costCurrent += op.Metadata?.GasCost ?? 0;
                    costs[costStart] = costCurrent; // remember the current chain of opcodes
                    costStart = pc + 1;             // start with the next again
                    costCurrent = 0;
                    break;
                default:
                    costCurrent += op.Metadata?.GasCost ?? 0;
                    break;
            }

            pc += op.Metadata?.AdditionalBytes ?? 0;
        }

        if (costCurrent > 0)
        {
            costs[costStart] = costCurrent;
        }

        return costs;
    }
}
