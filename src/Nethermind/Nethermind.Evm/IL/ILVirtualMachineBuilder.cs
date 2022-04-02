//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Iced.Intel;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Implementation;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Label = System.Reflection.Emit.Label;

namespace Nethermind.Evm.IL;

class ILVirtualMachineBuilder
{
    public static Func<long, EvmExceptionType> Build(byte[] code)
    {
        Dictionary<int, long> gasCost = BuildCostLookup(code);

        // TODO: stack invariants, gasCost application

        string name = "ILVM_" + Guid.NewGuid();

        DynamicMethod method = new(name, typeof(EvmExceptionType), new[] { typeof(long) }, typeof(ILVirtualMachineBuilder).Assembly.Modules.First(), true)
        {
            InitLocals = false
        };

        ILGenerator il = method.GetILGenerator();

        LocalBuilder jmpDestination = il.DeclareLocal(Word.Int0Field.FieldType);
        LocalBuilder consumeJumpCondition = il.DeclareLocal(typeof(int));
        LocalBuilder uint256A = il.DeclareLocal(typeof(UInt256));
        LocalBuilder uint256B = il.DeclareLocal(typeof(UInt256));
        LocalBuilder uint256C = il.DeclareLocal(typeof(UInt256));

        LocalBuilder gasAvailable = il.DeclareLocal(typeof(long));

        // TODO: stack check for head
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

        for (int pc = 0; pc < code.Length; pc++)
        {
            Operation op = Operation.Operations[(Instruction)code[pc]];

            if (gasCost.TryGetValue(pc, out long required))
            {
                // check required > available
                il.LoadValue(required);
                il.Load(gasAvailable);
                il.Emit(OpCodes.Cgt);
                il.Emit(OpCodes.Brtrue, outOfGas); // jump to out of gas

                // gasAvailable = gasAvailable - required
                il.Load(gasAvailable);
                il.LoadValue(required);
                il.Emit(OpCodes.Sub);
                il.Store(gasAvailable);
            }

            switch (op.Instruction)
            {
                case Instruction.PC:
                    il.CleanWord(current);
                    il.Load(current);
                    il.LoadValue(BinaryPrimitives.ReverseEndianness(pc)); // TODO: assumes little endian machine
                    il.Emit(OpCodes.Stfld, Word.UInt0Field);
                    il.StackPush(current);
                    break;

                // pushes work as follows
                // 1. load the next top pointer of the stack
                // 2. zero it
                // 3. load the value
                // 4. set the field
                // 5. advance pointer
                case Instruction.PUSH1:
                    il.Load(current);
                    il.Emit(OpCodes.Initobj, typeof(Word));
                    il.Load(current);
                    byte push1 = (byte)((pc + 1 >= code.Length) ? 0 : code[pc + 1]);
                    il.Emit(OpCodes.Ldc_I4_S, push1);
                    il.Emit(OpCodes.Stfld, Word.Byte0Field);
                    il.StackPush(current);
                    pc += 1;
                    break;
                case Instruction.PUSH2:
                    il.Load(current);
                    il.Emit(OpCodes.Initobj, typeof(Word));
                    il.Load(current);

                    if (pc + 2 >= code.Length)
                        throw new NotImplementedException("Not handled yet!");

                    if (!BitConverter.IsLittleEndian)
                        throw new NotImplementedException("Currently only little endian!");

                    il.Emit(OpCodes.Ldc_I4, BinaryPrimitives.ReadInt16LittleEndian(code.Slice(pc + 1)) << 16);
                    il.Emit(OpCodes.Stfld, Word.Int0Field);
                    il.StackPush(current);
                    pc += 2;
                    break;
                case Instruction.PUSH4:
                    il.Load(current);
                    il.Emit(OpCodes.Initobj, typeof(Word));
                    il.Load(current);

                    if (pc + 4 >= code.Length)
                        throw new NotImplementedException("Not handled yet!");

                    if (!BitConverter.IsLittleEndian)
                        throw new NotImplementedException("Currently only little endian!");

                    il.Emit(OpCodes.Ldc_I4, BinaryPrimitives.ReadInt32LittleEndian(code.Slice(pc + 1)));
                    il.Emit(OpCodes.Stfld, Word.Int0Field);
                    il.StackPush(current);
                    pc += 4;
                    break;
                case Instruction.DUP1:
                    il.Load(current);
                    il.StackLoadPrevious(current, 1 + op.Instruction - Instruction.DUP1);       // TODO: ready for other DUP_N with the substitution
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word));
                    il.StackPush(current);
                    break;
                case Instruction.SWAP1:
                    // copy to a helper variable the top item
                    il.LoadAddress(uint256C); // reuse uint as a swap placeholder
                    il.StackLoadPrevious(current, 1);
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word));

                    byte swapWith = 2 + op.Instruction - Instruction.SWAP1; // TODO: ready for other SWAP_N with the substitution

                    // write to the top item
                    il.StackLoadPrevious(current, 1);
                    il.StackLoadPrevious(current, swapWith);
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word)); // top item overwritten

                    // write to the more nested one from local variable
                    il.StackLoadPrevious(current, swapWith);
                    il.LoadAddress(uint256C);
                    il.Emit(OpCodes.Ldobj, typeof(Word));
                    il.Emit(OpCodes.Stobj, typeof(Word));
                    break;
                case Instruction.POP:
                    il.StackPop(current);
                    break;
                case Instruction.JUMPDEST:
                    Label dest = il.DefineLabel();
                    jumpDestinations[pc] = dest;
                    il.MarkLabel(dest);
                    break;
                case Instruction.JUMP:
                    il.Emit(OpCodes.Br, jumpTable);
                    break;
                case Instruction.JUMPI:
                    Label noJump = il.DefineLabel();

                    il.StackLoadPrevious(current, 2); // load condition that is on the second
                    il.EmitCall(OpCodes.Call, Word.GetIsZero, null);

                    il.Emit(OpCodes.Brtrue_S, noJump); // if zero, just jump to removal two values and move on

                    // condition is met, mark condition as to be removed
                    il.LoadValue(1);
                    il.Store(consumeJumpCondition);
                    il.Emit(OpCodes.Br, jumpTable);

                    // condition is not met, just consume
                    il.MarkLabel(noJump);
                    il.StackPop(current, 2);
                    break;
                case Instruction.SUB:
                    // a
                    il.StackLoadPrevious(current, 1);
                    il.EmitCall(OpCodes.Call, Word.GetUInt256, null);
                    il.Store(uint256A);

                    // b
                    il.StackLoadPrevious(current, 2);
                    il.EmitCall(OpCodes.Call, Word.GetUInt256, null); // stack: uint256, uint256
                    il.Store(uint256B);

                    // a - b = c
                    il.LoadAddress(uint256A);
                    il.LoadAddress(uint256B);
                    il.LoadAddress(uint256C);

                    MethodInfo subtract = typeof(UInt256).GetMethod(nameof(UInt256.Subtract), BindingFlags.Public | BindingFlags.Static)!;
                    il.EmitCall(OpCodes.Call, subtract, null); // stack: _

                    il.StackPop(current, 2);
                    il.Load(current);
                    il.Load(uint256C); // stack: word*, uint256
                    il.EmitCall(OpCodes.Call, Word.SetUInt256, null);
                    il.StackPush(current);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        // jump to return
        il.LoadValue((int)EvmExceptionType.None);
        il.Emit(OpCodes.Br, ret);

        // jump table
        il.MarkLabel(jumpTable);

        il.StackPop(current); // move the stack down to address

        // if (jumpDest > uint.MaxValue)
        // ULong3 | Ulong2 | Ulong1 | Uint1 | Ushort1
        il.Load(current, Word.Ulong3Field);
        il.Load(current, Word.Ulong2Field);
        il.Emit(OpCodes.Or);
        il.Load(current, Word.Ulong1Field);
        il.Emit(OpCodes.Or);
        il.Load(current, Word.UInt1Field);
        il.Emit(OpCodes.Conv_U8);
        il.Emit(OpCodes.Or);

        il.Emit(OpCodes.Brtrue, invalidAddress);

        // emit actual jump table with first switch statement covering fanout of values, then ifs in specific branches
        const int jumpFanOutLog = 7; // 128
        const int bitMask = (1 << jumpFanOutLog) - 1;

        Label[] jumps = new Label[jumpFanOutLog];
        for (int i = 0; i < jumpFanOutLog; i++)
        {
            jumps[i] = il.DefineLabel();
        }

        // save to helper
        il.Load(current, Word.Int0Field);

        // endianess!
        il.EmitCall(OpCodes.Call, typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), BindingFlags.Public | BindingFlags.Static, new[] { typeof(uint) }), null);
        il.Store(jmpDestination);

        // consume if this was a conditional jump and zero it. Notice that this is a branch-free approach that uses 0 or 1 + multiplication to advance the word pointer or not
        il.StackPop(current, consumeJumpCondition);
        il.LoadValue(0);
        il.Store(consumeJumpCondition);

        // & with mask
        il.Load(jmpDestination);
        il.LoadValue(bitMask);
        il.Emit(OpCodes.And);

        il.Emit(OpCodes.Switch, jumps); // actual jump table to jump directly to the specific range of addresses

        int[] destinations = jumpDestinations.Keys.ToArray();

        for (int i = 0; i < jumpFanOutLog; i++)
        {
            il.MarkLabel(jumps[i]);

            // for each destination matching the bit mask emit check for the equality
            foreach (int dest in destinations.Where(dest => (dest & bitMask) == i))
            {
                il.Load(jmpDestination);
                il.LoadValue(dest);
                il.Emit(OpCodes.Beq, jumpDestinations[dest]);
            }

            // each bucket ends with a jump to invalid access to do not fall through to another one
            il.Emit(OpCodes.Br, invalidAddress);
        }

        // out of gas
        il.MarkLabel(outOfGas);
        il.LoadValue((int)EvmExceptionType.OutOfGas);
        il.Emit(OpCodes.Br, ret);

        // invalid address return
        il.MarkLabel(invalidAddress);
        il.LoadValue((int)EvmExceptionType.InvalidJumpDestination);
        il.Emit(OpCodes.Br, ret);

        // return
        il.MarkLabel(ret);
        il.Emit(OpCodes.Ret);

        Func<long, EvmExceptionType> del = method.CreateDelegate<Func<long, EvmExceptionType>>();

        // TODO: extracting ASM requires to have this run at least once!
        //using DataTarget dt = DataTarget.CreateSnapshotAndAttach(Process.GetCurrentProcess().Id);
        //using ClrRuntime runtime = dt.ClrVersions.Single().CreateRuntime();
        //ClrHeap heap = runtime.Heap;

        //ClrmdMethod clrMethod = heap.GetProxies("System.Reflection.Emit.DynamicMethod")
        //    .Select(proxy =>
        //    {
        //        ulong mdToken = (ulong)proxy.m_methodHandle.m_value.m_handle;
        //        return runtime.GetMethodByHandle(mdToken);
        //    }).Single(m => m.Name == name) as ClrmdMethod;

        //TextWriter writer = Console.Out;

        //ulong address = clrMethod.NativeCode;
        //const int length = 2048;
        
        //writer.WriteLine($"ASM-------------");

        //unsafe
        //{
        //    byte[] asm = new Span<byte>(new UIntPtr(address).ToPointer(), length).ToArray();

        //    Decoder decoder = Decoder.Create(IntPtr.Size * 8, asm);

        //    IntelFormatter formatter = new();
        //    StringOutput? output = new();

        //    decoder.IP = address;
        //    while (decoder.IP < (address + length))
        //    {
        //        decoder.Decode(out Iced.Intel.Instruction instruction);

        //        formatter.Format(instruction, output);

        //        writer.Write((instruction.IP - address).ToString("x4"));
        //        writer.Write(": ");
        //        writer.WriteLine(output.ToStringAndReset());
        //    }
        //}

        //writer.WriteLine("ASM END-------------");
        //writer.WriteLine();

        return del;
    }
    
    private static Dictionary<int, long> BuildCostLookup(ReadOnlySpan<byte> code)
    {
        Dictionary<int, long> costs = new();
        int costStart = 0;
        long costCurrent = 0;

        for (int pc = 0; pc < code.Length; pc++)
        {
            Operation op = Operation.Operations[(Instruction)code[pc]];
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
