using System;
using System.Collections;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Store;

namespace Nevermind.Evm
{
    // precached executions for arithmetic
    public class VirtualMachine
    {
        private static readonly bool IsLogging = false;

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
            IBlockhashProvider blockhashProvider,
            IWorldStateProvider worldStateProvider)
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

            byte[] GetCallDataSlice(BigInteger position, BigInteger length)
            {
                BigInteger bytesFromInput = BigInteger.Max(0, BigInteger.Min(env.InputData.Length - position, length));
                if (position > env.InputData.Length)
                {
                    return new byte[(int)length];
                }

                return env.InputData.Slice((int) position, (int) bytesFromInput).PadRight((int) length);
            }

            byte[] output = new byte[0];
            byte[] code = env.MachineCode;

            BigInteger[] i256Reg = new BigInteger[17];
            byte[][] bytesReg = new byte[17][];
            int intReg;

            Instruction instruction;

            BitArray bits1;
            BitArray bits2;

            while (true)
            {
                if (state.GasAvailable < 0)
                {
                    throw new Exception();
                }

                if (state.ProgramCounter >= code.Length)
                {
                    break;
                }

                instruction = (Instruction) code[(int) state.ProgramCounter];                
                state.ProgramCounter++;
                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        return output;
                    }
                    case Instruction.ADD:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.ModPow(i256Reg[0] + i256Reg[1], 1, P256Int).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.MUL:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.ModPow(i256Reg[0] * i256Reg[1], 1, P256Int).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.SUB:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        BigInteger res = i256Reg[0] - i256Reg[1];
                        if (res < 0)
                        {
                            res += P256Int;
                        }

                        stack.Push(res.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.DIV:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(i256Reg[1] == BigInteger.Zero
                            ? P0
                            : BigInteger.Divide(i256Reg[0], i256Reg[1]).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.SDIV:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (i256Reg[1] == BigInteger.Zero)
                        {
                            stack.Push(P0);
                        }
                        else if (i256Reg[1] == -1 && i256Reg[0] == P255Int)
                        {
                            stack.Push(P255);
                        }
                        else
                        {
                            stack.Push(BigInteger.Divide(i256Reg[0], i256Reg[1]).ToBigEndianByteArray(false, 32));
                        }
                        break;
                    }
                    case Instruction.MOD:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(i256Reg[1] == BigInteger.Zero
                            ? P0
                            : BigInteger.Remainder(i256Reg[0], i256Reg[1]).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(
                            i256Reg[1] == BigInteger.Zero
                                ? P0
                                : (i256Reg[0].Sign * BigInteger.Remainder(i256Reg[0].Abs(), i256Reg[1].Abs()))
                                .ToBigEndianByteArray(false,
                                    32));
                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        state.GasAvailable -= GasCostOf.Mid;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(
                            i256Reg[2] == BigInteger.Zero
                                ? P0
                                : BigInteger.Remainder(i256Reg[0] + i256Reg[1], i256Reg[2]).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        state.GasAvailable -= GasCostOf.Mid;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(
                            i256Reg[2] == BigInteger.Zero
                                ? P0
                                : BigInteger.Remainder(i256Reg[0] * i256Reg[1], i256Reg[2]).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.EXP:
                    {
                        state.GasAvailable -= GasCostOf.Exp;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (i256Reg[1] > 0)
                        {
                            int expSize = (int) BigInteger.Log(i256Reg[1], 256);
                            i256Reg[2] = BigInteger.Pow(256, expSize);
                            i256Reg[3] = i256Reg[2] * 256;
                            if (i256Reg[2] > i256Reg[1])
                            {
                                expSize--;
                            }
                            else if (i256Reg[3] <= i256Reg[1])
                            {
                                expSize++;
                            }

                            state.GasAvailable -= GasCostOf.ExpByte * (1 + expSize);
                        }

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (i256Reg[0] == 0)
                        {
                            stack.Push(P0);
                        }
                        else if (i256Reg[0] == 1)
                        {
                            stack.Push(P1);
                        }
                        else
                        {
                            stack.Push(BigInteger.ModPow(i256Reg[0], i256Reg[1], P256Int).ToBigEndianByteArray());
                        }
                        break;
                    }
                    case Instruction.SIGNEXTEND:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes().PadLeft(32);
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        bits1 = bytesReg[1].ToBigEndianBitArray256();

                        int bitNumber = (int) BigInteger.Max(0, 256 - 8 * (bytesReg[0].ToUnsignedBigInteger() + 1));
                        bool isSet = bits1[bitNumber];
                        for (int i = 0; i < bitNumber; i++)
                        {
                            bits1[i] = isSet;
                        }

                        byte[] extended = bits1.ToBytes();
                        stack.Push(extended);
                        break;
                    }
                    case Instruction.LT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(i256Reg[0], i256Reg[1]) < 0
                            ? P1
                            : P0);
                        break;
                    }
                    case Instruction.GT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(i256Reg[0], i256Reg[1]) > 0
                            ? P1
                            : P0);
                        break;
                    }
                    case Instruction.SLT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(i256Reg[0], i256Reg[1]) < 0 ? P1 : P0);
                        break;
                    }
                    case Instruction.SGT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(BigInteger.Compare(i256Reg[0], i256Reg[1]) > 0 ? P1 : P0);
                        break;
                    }
                    case Instruction.EQ:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(i256Reg[0] == i256Reg[1] ? P1 : P0);
                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(i256Reg[0] == 0 ? P1 : P0);
                        break;
                    }
                    case Instruction.AND:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        bits1 = bytesReg[0].ToBigEndianBitArray256();
                        bits2 = bytesReg[1].ToBigEndianBitArray256();
                        stack.Push(bits1.And(bits2).ToBytes());
                        break;
                    }
                    case Instruction.OR:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        bytesReg[0] = PopBytes32();
                        bytesReg[1] = PopBytes32();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        bits1 = bytesReg[0].ToBigEndianBitArray256();
                        bits2 = bytesReg[1].ToBigEndianBitArray256();
                        stack.Push(bits1.Or(bits2).ToBytes());
                        break;
                    }
                    case Instruction.XOR:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        bits1 = bytesReg[0].ToBigEndianBitArray256();
                        bits2 = bytesReg[1].ToBigEndianBitArray256();
                        stack.Push(bits1.Xor(bits2).ToBytes());
                        break;
                    }
                    case Instruction.NOT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        bytesReg[0] = PopBytes32();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        for (int i = 0; i < 32; ++i)
                        {
                            bytesReg[0][i] = (byte) ~bytesReg[0][i];
                        }

                        stack.Push(bytesReg[0].WithoutLeadingZeros());
                        break;
                    }
                    case Instruction.BYTE:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        bytesReg[0] = PopBytes().PadLeft(32);

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(bytesReg[0].Length < i256Reg[0] ? P0 : bytesReg[0].Slice((int) i256Reg[0], 1));
                        break;
                    }
                    case Instruction.SHA3:
                    {
                        state.GasAvailable -= GasCostOf.Sha3;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        state.GasAvailable -= GasCostOf.Sha3Word * EvmMemory.Div32Ceiling(i256Reg[1]);

                        (bytesReg[0], i256Reg[2]) = state.Memory.Load(i256Reg[0], i256Reg[1]);

                        stack.Push(Keccak.Compute(bytesReg[0]).Bytes);

                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, i256Reg[2]);
                        state.ActiveWordsInMemory = i256Reg[2];
                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.CodeOwner.Hex);
                        break;
                    }
                    case Instruction.BALANCE:
                    {
                        state.GasAvailable -= GasCostOf.Balance;
                        bytesReg[0] = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Account account =
                            worldStateProvider.GetOrCreateAccount(new Address(bytesReg[0]
                                .Slice(bytesReg[0].Length - 20, 20)));
                        stack.Push(account?.Balance.ToBigEndianByteArray() ?? P0);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.Caller.Hex);
                        break;
                    }
                    case Instruction.CALLVALUE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.Value.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.ORIGIN:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.Originator.Hex);
                        break;
                    }
                    case Instruction.CALLDATALOAD:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(GetCallDataSlice(i256Reg[0], 32));
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.InputData.Length.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        byte[] callDataSlice = GetCallDataSlice(i256Reg[1], i256Reg[2]);
                        BigInteger newMemoryState = state.Memory.Save(i256Reg[0], callDataSlice);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, newMemoryState);
                        state.GasAvailable -= GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]);
                        state.ActiveWordsInMemory = newMemoryState;
                        break;
                    }
                    case Instruction.CODESIZE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.MachineCode.Length.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        throw new NotImplementedException();
                    }
                    case Instruction.GASPRICE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.GasPrice.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        state.GasAvailable -= GasCostOf.ExtCode;
                        bytesReg[0] = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Address address = new Address(bytesReg[0].Slice(bytesReg[0].Length - 20, 20));
                        Account account =
                            worldStateProvider.GetOrCreateAccount(address);
                        byte[] accountCode = storageProvider.GetOrCreateStorage(address).GetCode(account.CodeHash);
                        stack.Push(accountCode?.Length.ToBigEndianByteArray() ?? P0);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        throw new NotImplementedException();
                    }
                    case Instruction.BLOCKHASH:
                    {
                        state.GasAvailable -= GasCostOf.BlockHash;
                        i256Reg[0] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (i256Reg[0] > 256)
                        {
                            stack.Push(P0);
                        }
                        else if (i256Reg[0] == 0)
                        {
                            stack.Push(P0);
                        }
                        else
                        {
                            stack.Push(blockhashProvider.GetBlockhash(env.CurrentBlock, (int) i256Reg[0]).Bytes);
                        }

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.Beneficiary.Hex);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.Difficulty.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.Timestamp.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.Number.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(env.CurrentBlock.Header.GasLimit.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.POP:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Pop();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        break;
                    }
                    case Instruction.MLOAD:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        (bytesReg[0], i256Reg[1]) = state.Memory.Load(i256Reg[0]);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, i256Reg[1]);
                        state.ActiveWordsInMemory = i256Reg[1];
                        stack.Push(bytesReg[0]);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        bytesReg[1] = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        i256Reg[1] = state.Memory.SaveWord(i256Reg[0], bytesReg[1]);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, i256Reg[1]);
                        state.ActiveWordsInMemory = i256Reg[1];
                        break;
                    }
                    case Instruction.MSTORES:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        bytesReg[1] = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        i256Reg[1] = state.Memory.SaveByte(i256Reg[0], bytesReg[1]);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, i256Reg[1]);
                        state.ActiveWordsInMemory = i256Reg[1];
                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        state.GasAvailable -= GasCostOf.SLoad;
                        i256Reg[0] = PopInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Address accountAddressLoad = env.CodeOwner;
                        StorageTree treeLoad = storageProvider.GetOrCreateStorage(accountAddressLoad);
                        stack.Push(treeLoad.Get(i256Reg[0]));
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        i256Reg[0] = PopInt();
                        bytesReg[0] = PopBytes();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Address accountAddress = env.CodeOwner;
                        StorageTree tree = storageProvider.GetOrCreateStorage(accountAddress);
                        byte[] previousValue = tree.Get(i256Reg[0]);
                        if (!bytesReg[0].IsZero() && !Bytes.UnsafeCompare(bytesReg[0], previousValue))
                        {
                            tree.Set(i256Reg[0], bytesReg[0].WithoutLeadingZeros());
                            state.GasAvailable -= GasCostOf.SSet;
                        }
                        else
                        {
                            state.GasAvailable -= GasCostOf.SReset;
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        state.GasAvailable -= GasCostOf.Mid;
                        i256Reg[0] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        state.ProgramCounter = i256Reg[0];

                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        state.GasAvailable -= GasCostOf.High;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        if (i256Reg[1] > 0)
                        {
                            state.ProgramCounter = i256Reg[0];
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(state.ProgramCounter.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push((state.ActiveWordsInMemory * 32).ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.GAS:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(state.GasAvailable.ToBigEndianByteArray());
                        break;
                    }
                    case Instruction.JUMPDEST:
                    {
                        state.GasAvailable -= GasCostOf.JumpDest;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        break;
                    }
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
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        intReg = instruction - Instruction.PUSH1 + 1;
                        int usedFromCode = (int) BigInteger.Min(code.Length - state.ProgramCounter, intReg);

                        stack.Push(usedFromCode != intReg
                            ? code.Slice((int) state.ProgramCounter, usedFromCode).PadRight(intReg)
                            : code.Slice((int) state.ProgramCounter, usedFromCode));

                        state.ProgramCounter += intReg;
                        break;
                    }
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
                        state.GasAvailable -= GasCostOf.VeryLow;
                        intReg = instruction - Instruction.DUP1 + 1;
                        for (int i = 0; i < intReg; i++)
                        {
                            bytesReg[i] = PopBytes();
                        }

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        for (int i = 0; i < intReg; i++)
                        {
                            stack.Push(bytesReg[intReg - i - 1]);
                        }

                        stack.Push(bytesReg[intReg - 1]);

                        break;
                    }
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
                        state.GasAvailable -= GasCostOf.VeryLow;
                        intReg = instruction - Instruction.SWAP1 + 2;
                        for (int i = 0; i < intReg; i++)
                        {
                            bytesReg[i] = PopBytes();
                        }

                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        stack.Push(bytesReg[0]);

                        for (int i = intReg - 2; i > 0; i--)
                        {
                            stack.Push(bytesReg[i]);
                        }

                        stack.Push(bytesReg[intReg - 1]);

                        break;
                    }
                    case Instruction.RETURN:
                    {
                        state.GasAvailable -= GasCostOf.Zero;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        (byte[] returnValue, BigInteger _) = state.Memory.Load(i256Reg[0], i256Reg[1]);
                        return returnValue;
                    }
                    case Instruction.CREATE:
                    {
                        state.GasAvailable -= GasCostOf.Create;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        if (IsLogging)
                        {
                            Console.WriteLine(instruction);
                        }

                        Account account = new Account();
                        account.Balance = i256Reg[0];

                        (byte[] accountCode, BigInteger newMemoryAllocation) =
                            state.Memory.Load(i256Reg[1], i256Reg[2]);
                        Keccak codeHash = Keccak.Compute(accountCode);

                        Address address = new Address(codeHash);
                        StorageTree storageTree = storageProvider.GetOrCreateStorage(address);
                        storageTree.SetCode(accountCode);
                        account.CodeHash = codeHash;
                        account.Nonce = 0;
                        account.StorageRoot = storageTree.RootHash;

                        worldStateProvider.State.Set(address, Rlp.Encode(account));
                        stack.Push(address.Hex);
                        break;
                    }
                    default:
                    {
                        if (IsLogging)
                        {
                            Console.WriteLine($"INVALID INSTRUCTION 0x{instruction:X}");
                        }
                        throw new ArgumentOutOfRangeException();
                    }
                }

                if (IsLogging)
                {
                    Console.WriteLine($"GAS {state.GasAvailable}");
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