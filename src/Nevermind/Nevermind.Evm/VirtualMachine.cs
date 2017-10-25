using System;
using System.Collections;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Sugar;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class VirtualMachine
    {
        private static readonly bool IsLogging = true;

        private static readonly BigInteger P255Int = BigInteger.Pow(2, 255);
        private static readonly BigInteger P256Int = P255Int * 2;
        private static readonly BigInteger P256IntMax = P256Int - 1;
        private static readonly byte[] P255 = P255Int.ToBigEndianByteArray();
        private static readonly byte[] P0 = BigInteger.Zero.ToBigEndianByteArray();
        private static readonly byte[] P1 = BigInteger.One.ToBigEndianByteArray();

        public byte[] Run(ExecutionEnvironment executionEnvironment, MachineState machineState,
            IStorageProvider storageProvider)
        {
            EvmStack stack = machineState.Stack;
            BigInteger PopUInt()
            {
                return PopBytes().ToUnsignedBigInteger();
            }

            BigInteger PopInt()
            {
                return PopBytes().ToSignedBigInteger();
            }

            byte[] PopBytes()
            {
                return stack.Pop();
            }

            byte[] output = new byte[0];
            byte[] code = executionEnvironment.MachineCode;
            while (true)
            {
                bool stopExecution = machineState.ProgramCounter == code.Length;
                if (stopExecution)
                {
                    break;
                }

                Instruction instruction = (Instruction)code[(int)machineState.ProgramCounter];
                if (instruction == Instruction.STOP)
                {
                    break;
                }

                BigInteger reg1;
                BigInteger reg2;
                BigInteger reg3;

                byte[] byte1;
                byte[] byte2;

                // TODO: must be in P256
                machineState.ProgramCounter++;
                switch (instruction)
                {
                    case Instruction.STOP:
                        {
                            if (IsLogging)
                            {
                                Console.WriteLine(instruction);
                            }

                            stopExecution = true;
                            break;
                        }
                    case Instruction.ADD:
                        {
                            machineState.GasAvailable -= GasCostOf.VeryLow;
                            reg1 = PopUInt();
                            reg2 = PopUInt();

                            if (IsLogging)
                            {
                                Console.WriteLine(instruction);
                            }

                            stack.Push(BigInteger.ModPow(reg1 + reg2, 1, P256Int).ToBigEndianByteArray());
                            break;
                        }
                    case Instruction.MUL:
                        {
                            machineState.GasAvailable -= GasCostOf.Low;
                            reg1 = PopUInt();
                            reg2 = PopUInt();

                            if (IsLogging)
                            {
                                Console.WriteLine(instruction);
                            }

                            stack.Push(BigInteger.ModPow(reg1 * reg2, 1, P256Int).ToBigEndianByteArray());
                            break;
                        }
                    case Instruction.SUB:
                        {
                            machineState.GasAvailable -= GasCostOf.VeryLow;
                            reg1 = PopUInt();
                            reg2 = PopUInt();
                            BigInteger res = reg1 - reg2;
                            if (res < 0)
                            {
                                res += P256Int;
                            }

                            if (IsLogging)
                            {
                                Console.WriteLine(instruction);
                            }

                            stack.Push(res.ToBigEndianByteArray());
                            break;
                        }
                    case Instruction.DIV:
                        machineState.GasAvailable -= GasCostOf.Low;
                        reg1 = PopUInt();
                        reg2 = PopUInt();

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(reg2 == BigInteger.Zero
                            ? P0
                            : BigInteger.Divide(reg1, reg2).ToBigEndianByteArray());
                        break;
                    case Instruction.SDIV:
                        machineState.GasAvailable -= GasCostOf.Low;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (reg2 == BigInteger.Zero)
                        {
                            stack.Push(P0);
                        }
                        else if (reg2 == -1 && reg1 == P255Int)
                        {
                            stack.Push(P255);
                        }
                        else
                        {
                            stack.Push(BigInteger.Divide(reg1, reg2).ToBigEndianByteArray(false, 32).WithoutLeadingZeros());
                        }
                        break;
                    case Instruction.MOD:
                        machineState.GasAvailable -= GasCostOf.Low;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(reg2 == BigInteger.Zero
                            ? P0
                            : BigInteger.Remainder(reg1, reg2).ToBigEndianByteArray());
                        break;
                    case Instruction.SMOD:
                        machineState.GasAvailable -= GasCostOf.Low;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(
                            reg2 == BigInteger.Zero
                                ? P0
                                : (reg1.Sign * BigInteger.Remainder(reg1.Abs(), reg2.Abs())).ToBigEndianByteArray(false, 32).WithoutLeadingZeros());
                        break;
                    case Instruction.ADDMOD:
                        machineState.GasAvailable -= GasCostOf.Mid;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        reg3 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(
                            reg3 == BigInteger.Zero
                                ? P0
                                : BigInteger.Remainder(reg1 + reg2, reg3).ToBigEndianByteArray());
                        break;
                    case Instruction.MULMOD:
                        machineState.GasAvailable -= GasCostOf.Mid;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        reg3 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(
                            reg3 == BigInteger.Zero
                                ? P0
                                : BigInteger.Remainder(reg1 * reg2, reg3).ToBigEndianByteArray());
                        break;
                    case Instruction.EXP:
                        machineState.GasAvailable -= GasCostOf.Exp;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (reg2 > 0)
                        {
                            int expSize = (int)BigInteger.Log(reg2, 256);
                            BigInteger actual = BigInteger.Pow(256, expSize);
                            BigInteger actualP1 = actual * 256;
                            if (actual > reg2)
                            {
                                expSize--;
                            }
                            else if (actualP1 <= reg2)
                            {
                                expSize++;
                            }

                            machineState.GasAvailable -= GasCostOf.ExpByte * (1 + expSize);
                        }

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (reg1 == 0)
                        {
                            stack.Push(P0);
                        }
                        else if (reg1 == 1)
                        {
                            stack.Push(P1);
                        }
                        else
                        {
                            stack.Push(BigInteger.ModPow(reg1, reg2, P256Int).ToBigEndianByteArray());
                        }
                        break;
                    case Instruction.SIGNEXTEND:
                        machineState.GasAvailable -= GasCostOf.Low;
                        byte1 = PopBytes();
                        byte2 = Bytes.PadLeft(PopBytes(), 32);
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        BitArray bitArray = new BitArray(256);
                        for (int i = 0; i < 256; i++)
                        {
                            bitArray[i] = byte2[i / 8].GetBit(i % 8);
                        }

                        int bitNumber = (int)BigInteger.Max(0, 256 - 8 * (byte1.ToUnsignedBigInteger() + 1));
                        bool isSet = bitArray[bitNumber];
                        for (int i = 0; i < bitNumber; i++)
                        {
                            bitArray[i] = isSet;
                        }

                        byte[] extended = bitArray.ToBigEndianBytes();
                        stack.Push(extended.WithoutLeadingZeros());
                        break;
                    case Instruction.LT:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(reg1, reg2) < 0
                            ? reg1.ToBigEndianByteArray()
                            : reg2.ToBigEndianByteArray());
                        break;
                    case Instruction.GT:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(reg1, reg2) > 0
                            ? reg1.ToBigEndianByteArray()
                            : reg2.ToBigEndianByteArray());
                        break;
                    case Instruction.SLT:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(reg1, reg2) < 0 ? P1 : P0);
                        break;
                    case Instruction.SGT:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(reg1, reg2) > 0 ? P1 : P0);
                        break;
                    case Instruction.EQ:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(reg1 == reg2 ? P1 : P0);
                        break;
                    case Instruction.ISZERO:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(reg1 == 0 ? P1 : P0);
                        break;
                    case Instruction.AND:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        byte1 = PopBytes();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        for (int i = 0; i <= 255; ++i)
                        {
                            byte1[i] = (byte)(byte1[i] & byte2[i]);
                        }

                        stack.Push(byte1);
                        break;
                    case Instruction.OR:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        byte1 = PopBytes();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        for (int i = 0; i <= 255; ++i)
                        {
                            byte1[i] = (byte)(byte1[i] | byte2[i]);
                        }

                        stack.Push(byte1);
                        break;
                    case Instruction.XOR:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        byte1 = PopBytes();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        for (int i = 0; i <= 255; ++i)
                        {
                            byte1[i] = (byte)(byte1[i] % byte2[i]);
                        }

                        stack.Push(byte1);
                        break;
                    case Instruction.NOT:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        byte1 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        for (int i = 0; i < 32; ++i)
                        {
                            byte1[i] = (byte)~byte1[i];
                        }

                        stack.Push(byte1);
                        break;
                    case Instruction.BYTE:
                        throw new NotImplementedException();
                    case Instruction.SHA3:
                        throw new NotImplementedException();
                    case Instruction.CALLDATALOAD:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        stack.Push(executionEnvironment.InputData.Slice((int)reg1, 32));
                        break;
                    case Instruction.MLOAD:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        (byte[] word, BigInteger newActiveWordsL) = machineState.Memory.Load(reg1);
                        machineState.GasAvailable -=
                            CalculateMemoryCost(machineState.ActiveWordsInMemory, newActiveWordsL);
                        machineState.ActiveWordsInMemory = newActiveWordsL;
                        stack.Push(word);
                        break;
                    case Instruction.MSTORE:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        BigInteger newActiveWords = machineState.Memory.SaveWord(reg1, byte2);
                        machineState.GasAvailable -=
                            CalculateMemoryCost(machineState.ActiveWordsInMemory, newActiveWords);
                        machineState.ActiveWordsInMemory = newActiveWords;
                        break;
                    case Instruction.MSTORES:
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        BigInteger newActiveWordsS = machineState.Memory.SaveByte(reg1, byte2);
                        machineState.GasAvailable -=
                            CalculateMemoryCost(machineState.ActiveWordsInMemory, newActiveWordsS);
                        machineState.ActiveWordsInMemory = newActiveWordsS;
                        break;
                    case Instruction.SLOAD:
                        machineState.GasAvailable -= GasCostOf.SLoad;
                        BigInteger storagePositionLoad = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Address accountAddressLoad = executionEnvironment.CodeOwner;
                        StorageTree treeLoad = storageProvider.GetStorage(accountAddressLoad);
                        stack.Push(treeLoad.Get(storagePositionLoad));
                        break;
                    case Instruction.SSTORE:
                        BigInteger storagePosition = PopInt();
                        byte[] storageData = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Address accountAddress = executionEnvironment.CodeOwner;
                        StorageTree tree = storageProvider.GetStorage(accountAddress);
                        byte[] previousValue = tree.Get(storagePosition);
                        if (!storageData.IsZero() && !Bytes.UnsafeCompare(storageData, previousValue))
                        {
                            tree.Set(storagePosition, storageData);
                            machineState.GasAvailable -= GasCostOf.SSet;
                        }
                        else
                        {
                            machineState.GasAvailable -= GasCostOf.SReset;
                        }

                        // update account storage
                        break;
                    case Instruction.JUMP:
                        machineState.GasAvailable -= GasCostOf.Mid;
                        reg1 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        machineState.ProgramCounter = reg1;

                        break;
                    case Instruction.JUMPI:
                        machineState.GasAvailable -= GasCostOf.High;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (reg2 > 0)
                        {
                            machineState.ProgramCounter = reg1;
                        }

                        break;
                    case Instruction.JUMPDEST:
                        machineState.GasAvailable -= GasCostOf.JumpDest;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
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
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        int bytesToPush = instruction - Instruction.PUSH1 + 1;
                        stack.Push(code.Slice((int)machineState.ProgramCounter, bytesToPush));
                        machineState.ProgramCounter += bytesToPush;
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
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        int itemsToPop = instruction - Instruction.DUP1 + 1;
                        byte[][] tempDupItems = new byte[itemsToPop][];
                        for (int i = 0; i < itemsToPop; i++)
                        {
                            tempDupItems[i] = PopBytes();
                        }

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        for (int i = 0; i < itemsToPop; i++)
                        {
                            stack.Push(tempDupItems[itemsToPop - i - 1]);
                        }

                        stack.Push(tempDupItems[itemsToPop - 1]);

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
                        machineState.GasAvailable -= GasCostOf.VeryLow;
                        int swapDepth = instruction - Instruction.SWAP1 + 2;
                        byte[][] tempSwapItems = new byte[swapDepth][];
                        for (int i = 0; i < swapDepth; i++)
                        {
                            tempSwapItems[i] = PopBytes();
                        }

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(tempSwapItems[0]);

                        for (int i = swapDepth - 1; i > 1; i--)
                        {
                            stack.Push(tempSwapItems[i]);
                        }

                        stack.Push(tempSwapItems[swapDepth - 1]);

                        break;
                    case Instruction.RETURN:
                        machineState.GasAvailable -= GasCostOf.Zero;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        return machineState.Memory.Load(reg1, reg2);
                    default:
                        if (IsLogging)
                        {
                            Console.WriteLine($"INVALID INSTRUCTION 0x{instruction:X}");
                        }
                        throw new ArgumentOutOfRangeException();
                }

                if (IsLogging)
                {
                    Console.WriteLine($"GAS {machineState.GasAvailable}");
                }

                if (stopExecution)
                {
                    break;
                }
            }

            return output;
        }

        public static BigInteger CalculateMemoryCost(BigInteger initial, BigInteger final)
        {
            return
                final * GasCostOf.Memory + BigInteger.Divide(BigInteger.Pow(final, 2), 512)
                - initial * GasCostOf.Memory + BigInteger.Divide(BigInteger.Pow(initial, 2), 512);
        }
    }
}