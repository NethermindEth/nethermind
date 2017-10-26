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

        public byte[] Run(
            ExecutionEnvironment env,
            MachineState state,
            IStorageProvider storageProvider,
            IBlockhashProvider blockhashProvider)
        {
            EvmStack stack = state.Stack;

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

            byte[] PopBytes32()
            {
                return stack.Pop().PadLeft(32);
            }

            byte[] output = new byte[0];
            byte[] code = env.MachineCode;

            while (true)
            {
                // TODO: 
                if (state.GasAvailable < 0)
                {
                    throw new Exception();
                }

                bool stopExecution = state.ProgramCounter >= code.Length;
                if (stopExecution)
                {
                    break;
                }

                Instruction instruction = (Instruction) code[(int) state.ProgramCounter];
                if (instruction == Instruction.STOP)
                {
                    break;
                }

                BigInteger reg1;
                BigInteger reg2;
                BigInteger reg3;

                byte[] byte1;
                byte[] byte2;

                BitArray bits1;
                BitArray bits2;

                // TODO: must be in P256
                state.ProgramCounter++;
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
                        state.GasAvailable -= GasCostOf.VeryLow;
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
                        state.GasAvailable -= GasCostOf.Low;
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
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        reg2 = PopUInt();

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        BigInteger res = reg1 - reg2;
                        if (res < 0)
                        {
                            res += P256Int;
                        }

                        stack.Push(res.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.DIV:
                        state.GasAvailable -= GasCostOf.Low;
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
                        state.GasAvailable -= GasCostOf.Low;
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
                            stack.Push(BigInteger.Divide(reg1, reg2).ToBigEndianByteArray(false, 32));
                        }
                        break;
                    case Instruction.MOD:
                        state.GasAvailable -= GasCostOf.Low;
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
                        state.GasAvailable -= GasCostOf.Low;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(
                            reg2 == BigInteger.Zero
                                ? P0
                                : (reg1.Sign * BigInteger.Remainder(reg1.Abs(), reg2.Abs())).ToBigEndianByteArray(false,
                                    32));
                        break;
                    case Instruction.ADDMOD:
                        state.GasAvailable -= GasCostOf.Mid;
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
                        state.GasAvailable -= GasCostOf.Mid;
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
                        state.GasAvailable -= GasCostOf.Exp;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (reg2 > 0)
                        {
                            int expSize = (int) BigInteger.Log(reg2, 256);
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

                            state.GasAvailable -= GasCostOf.ExpByte * (1 + expSize);
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
                        state.GasAvailable -= GasCostOf.Low;
                        byte1 = PopBytes();
                        byte2 = PopBytes().PadLeft(32);
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        BitArray bitArray = byte2.ToBigEndianBitArray256();

                        int bitNumber = (int) BigInteger.Max(0, 256 - 8 * (byte1.ToUnsignedBigInteger() + 1));
                        bool isSet = bitArray[bitNumber];
                        for (int i = 0; i < bitNumber; i++)
                        {
                            bitArray[i] = isSet;
                        }

                        byte[] extended = bitArray.ToBytes();
                        stack.Push(extended);
                        break;
                    case Instruction.LT:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(reg1, reg2) < 0
                            ? P1
                            : P0);
                        break;
                    case Instruction.GT:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(reg1, reg2) > 0
                            ? P1
                            : P0);
                        break;
                    case Instruction.SLT:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(reg1, reg2) < 0 ? P1 : P0);
                        break;
                    case Instruction.SGT:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(reg1, reg2) > 0 ? P1 : P0);
                        break;
                    case Instruction.EQ:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopInt();
                        reg2 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(reg1 == reg2 ? P1 : P0);
                        break;
                    case Instruction.ISZERO:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(reg1 == 0 ? P1 : P0);
                        break;
                    case Instruction.AND:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        byte1 = PopBytes();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        bits1 = byte1.ToBigEndianBitArray256();
                        bits2 = byte2.ToBigEndianBitArray256();
                        stack.Push(bits1.And(bits2).ToBytes());
                        break;
                    case Instruction.OR:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        byte1 = PopBytes32();
                        byte2 = PopBytes32();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        bits1 = byte1.ToBigEndianBitArray256();
                        bits2 = byte2.ToBigEndianBitArray256();
                        stack.Push(bits1.Or(bits2).ToBytes());
                        break;
                    case Instruction.XOR:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        byte1 = PopBytes();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        bits1 = byte1.ToBigEndianBitArray256();
                        bits2 = byte2.ToBigEndianBitArray256();
                        stack.Push(bits1.Xor(bits2).ToBytes());
                        break;
                    case Instruction.NOT:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        byte1 = PopBytes32();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        for (int i = 0; i < 32; ++i)
                        {
                            byte1[i] = (byte) ~byte1[i];
                        }

                        stack.Push(byte1.WithoutLeadingZeros());
                        break;
                    case Instruction.BYTE:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        byte1 = PopBytes().PadLeft(32);

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(byte1.Length < reg1 ? P0 : byte1.Slice((int) reg1, 1));
                        break;
                    case Instruction.SHA3:
                        throw new NotImplementedException();
                    case Instruction.ADDRESS:
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.CodeOwner.Hex);
                        break;
                    case Instruction.BALANCE:
                        state.GasAvailable -= GasCostOf.Base;

                        // get balance of current account
                        throw new NotImplementedException();
                    case Instruction.CALLER:
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.Caller.Hex);
                        break;
                    case Instruction.CALLVALUE:
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.Value.ToBigEndianByteArray());
                        break;
                    case Instruction.ORIGIN:
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.Originator.Hex);
                        break;
                    case Instruction.CALLDATALOAD:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.InputData.Slice((int) reg1, 32));
                        break;
                    case Instruction.CODECOPY:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        throw new NotImplementedException();
                    case Instruction.CODESIZE:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.MachineCode.Length.ToBigEndianByteArray());
                        break;
                    case Instruction.GASPRICE:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.GasPrice.ToBigEndianByteArray());
                        break;
                    case Instruction.EXTCODESIZE:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        throw new NotImplementedException();
                    case Instruction.EXTCODECOPY:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        throw new NotImplementedException();

                    case Instruction.BLOCKHASH:
                        state.GasAvailable -= GasCostOf.BlockHash;
                        reg1 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (reg1 > 256)
                        {
                            stack.Push(P0);
                        }
                        else if (reg1 == 0)
                        {
                            stack.Push(P0);
                        }
                        else
                        {
                            stack.Push(blockhashProvider.GetBlockhash(env.CurrentBlock, (int)reg1).Bytes);
                        }

                        break;
                    case Instruction.COINBASE:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.Beneficiary.Hex);
                        break;
                    case Instruction.DIFFICLUTY:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.Difficulty.ToBigEndianByteArray());
                        break;
                    case Instruction.TIMESTAMP:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.Timestamp.ToBigEndianByteArray());
                        break;
                    case Instruction.NUMBER:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.Number.ToBigEndianByteArray());
                        break;
                    case Instruction.GASLIMIT:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.GasLimit.ToBigEndianByteArray());
                        break;
                    case Instruction.POP:
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Pop();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }
                        
                        break;
                    case Instruction.MLOAD:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        (byte[] word, BigInteger newActiveWordsL) = state.Memory.Load(reg1);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, newActiveWordsL);
                        state.ActiveWordsInMemory = newActiveWordsL;
                        stack.Push(word);
                        break;
                    case Instruction.MSTORE:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        BigInteger newActiveWords = state.Memory.SaveWord(reg1, byte2);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, newActiveWords);
                        state.ActiveWordsInMemory = newActiveWords;
                        break;
                    case Instruction.MSTORES:
                        state.GasAvailable -= GasCostOf.VeryLow;
                        reg1 = PopUInt();
                        byte2 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        BigInteger newActiveWordsS = state.Memory.SaveByte(reg1, byte2);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, newActiveWordsS);
                        state.ActiveWordsInMemory = newActiveWordsS;
                        break;
                    case Instruction.SLOAD:
                        state.GasAvailable -= GasCostOf.SLoad;
                        reg1 = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Address accountAddressLoad = env.CodeOwner;
                        StorageTree treeLoad = storageProvider.GetStorage(accountAddressLoad);
                        stack.Push(treeLoad.Get(reg1));
                        break;
                    case Instruction.SSTORE:
                        reg1 = PopInt();
                        byte1 = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Address accountAddress = env.CodeOwner;
                        StorageTree tree = storageProvider.GetStorage(accountAddress);
                        byte[] previousValue = tree.Get(reg1);
                        if (!byte1.IsZero() && !Bytes.UnsafeCompare(byte1, previousValue))
                        {
                            tree.Set(reg1, byte1.WithoutLeadingZeros());
                            state.GasAvailable -= GasCostOf.SSet;
                        }
                        else
                        {
                            state.GasAvailable -= GasCostOf.SReset;
                        }

                        break;
                    case Instruction.JUMP:
                        state.GasAvailable -= GasCostOf.Mid;
                        reg1 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        state.ProgramCounter = reg1;

                        break;
                    case Instruction.JUMPI:
                        state.GasAvailable -= GasCostOf.High;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (reg2 > 0)
                        {
                            state.ProgramCounter = reg1;
                        }

                        break;
                    case Instruction.PC:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(state.ProgramCounter.ToBigEndianByteArray());
                        break;
                    case Instruction.MSIZE:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push((state.ActiveWordsInMemory * 32).ToBigEndianByteArray());
                        break;
                    case Instruction.GAS:
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push((state.GasAvailable).ToBigEndianByteArray());
                        break;
                    case Instruction.JUMPDEST:
                        state.GasAvailable -= GasCostOf.JumpDest;
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
                        state.GasAvailable -= GasCostOf.VeryLow;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        int bytesToPush = instruction - Instruction.PUSH1 + 1;
                        int usedFromCode = Math.Min(code.Length - (int)state.ProgramCounter, bytesToPush);

                        stack.Push(usedFromCode != bytesToPush
                            ? code.Slice((int)state.ProgramCounter, usedFromCode).PadRight(bytesToPush)
                            : code.Slice((int) state.ProgramCounter, usedFromCode));

                        state.ProgramCounter += bytesToPush;
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
                        state.GasAvailable -= GasCostOf.VeryLow;
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
                        state.GasAvailable -= GasCostOf.VeryLow;
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
                        state.GasAvailable -= GasCostOf.Zero;
                        reg1 = PopUInt();
                        reg2 = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        return state.Memory.Load(reg1, reg2);
                    default:
                        if (IsLogging)
                        {
                            Console.WriteLine($"INVALID INSTRUCTION 0x{instruction:X}");
                        }
                        throw new ArgumentOutOfRangeException();
                }

                if (IsLogging)
                {
                    Console.WriteLine($"GAS {state.GasAvailable}");
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