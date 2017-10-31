using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class VirtualMachine
    {
        private static readonly BigInteger P255Int = BigInteger.Pow(2, 255);
        private static readonly BigInteger P256Int = P255Int * 2;
        private static readonly BigInteger P255 = P255Int;
        private static readonly byte[] Zero = BigInteger.Zero.ToBigEndianByteArray();
        private static readonly byte[] One = BigInteger.One.ToBigEndianByteArray();
        private static readonly BigInteger BigInt256 = 256;
        private static readonly BigInteger BigInt32 = 32;

        public static readonly BigInteger DaoExploitFixBlockNumber = 10
            ; // have not found this yet, setting to a random value for tests to pass

        public (byte[] output, TransactionSubstate) Run(
            ExecutionEnvironment env,
            EvmState state,
            IBlockhashProvider blockhashProvider,
            IWorldStateProvider worldStateProvider,
            IStorageProvider storageProvider)
        {
            EvmStack stack = state.Stack;

            BigInteger PopUInt()
            {
                return stack.PopInt(false);
            }

            BigInteger PopInt()
            {
                return stack.PopInt(true);
            }

            byte[] PopBytes()
            {
                return stack.PopBytes();
            }

            byte[] PopAddress()
            {
                byte[] bytes = stack.PopBytes();
                if (bytes.Length < 20)
                {
                    bytes = bytes.PadLeft(20, 0);
                }

                return bytes.Slice(bytes.Length - 20, 20);
            }

            byte[] GetPaddedSlice(byte[] data, BigInteger position, BigInteger length)
            {
                BigInteger bytesFromInput = BigInteger.Max(0, BigInteger.Min(data.Length - position, length));
                if (position > data.Length)
                {
                    return new byte[(int)length];
                }

                return data.Slice((int)position, (int)bytesFromInput).PadRight((int)length);
            }

            HashSet<Address> destroyList = new HashSet<Address>();
            List<LogEntry> logs = new List<LogEntry>();

            byte[] output = new byte[0];
            byte[] code = env.MachineCode;

            BigInteger[] i256Reg = new BigInteger[17];
            byte[][] bytesReg = new byte[17][];
            int intReg;

            Instruction instruction;
            BigInteger refund = BigInteger.Zero;

            BitArray bits1;
            BitArray bits2;

            int[] calls = new int[256];
            Stopwatch[] stopwatches = new Stopwatch[256];
            for (int i = 0; i < 256; i++)
            {
                stopwatches[i] = new Stopwatch();
            }

            Stopwatch totalTime = new Stopwatch();
            totalTime.Start();
            BigInteger gasBefore;
            while (true)
            {
                gasBefore = state.GasAvailable;

                if (state.GasAvailable < BigInteger.Zero)
                {
                    throw new OutOfGasException();
                }

                if (state.ProgramCounter >= code.Length)
                {
                    break;
                }

                instruction = (Instruction)code[(int)state.ProgramCounter];
                state.ProgramCounter++;
                stopwatches[(int)instruction].Start();
                calls[(int)instruction]++;

                if (ShouldLog.VM)
                {
                    Console.WriteLine($"{instruction} (0x{instruction:X})");
                }

                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
                    }
                    case Instruction.ADD:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        i256Reg[2] = i256Reg[0] + i256Reg[1];
                        stack.Push(
                            i256Reg[2] >= P256Int
                            ? (i256Reg[2] - P256Int)
                            : i256Reg[2]);

                        break;
                    }
                    case Instruction.MUL:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        stack.Push(BigInteger.Remainder(i256Reg[0] * i256Reg[1], P256Int)
                        );
                        break;
                    }
                    case Instruction.SUB:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = i256Reg[0] - i256Reg[1];
                        if (i256Reg[2] < BigInteger.Zero)
                        {
                            i256Reg[2] += P256Int;
                        }

                        stack.Push(i256Reg[2]);
                        break;
                    }
                    case Instruction.DIV:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        stack.Push(i256Reg[1] == BigInteger.Zero
                            ? BigInteger.Zero
                            : BigInteger.Divide(i256Reg[0], i256Reg[1]));
                        break;
                    }
                    case Instruction.SDIV:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        if (i256Reg[1] == BigInteger.Zero)
                        {
                            stack.Push(BigInteger.Zero);
                        }
                        else if (i256Reg[1] == BigInteger.MinusOne && i256Reg[0] == P255Int)
                        {
                            stack.Push(P255);
                        }
                        else
                        {
                            stack.Push(BigInteger.Divide(i256Reg[0], i256Reg[1]).ToBigEndianByteArray(true, 32));
                        }

                        break;
                    }
                    case Instruction.MOD:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        stack.Push(i256Reg[1] == BigInteger.Zero
                            ? BigInteger.Zero
                            : BigInteger.Remainder(i256Reg[0], i256Reg[1]));
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        stack.Push(
                            i256Reg[1] == BigInteger.Zero
                                ? Zero
                                : (i256Reg[0].Sign * BigInteger.Remainder(i256Reg[0].Abs(), i256Reg[1].Abs())).ToBigEndianByteArray(true, 32));

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        state.GasAvailable -= GasCostOf.Mid;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        stack.Push(
                            i256Reg[2] == BigInteger.Zero
                                ? BigInteger.Zero
                                : BigInteger.Remainder(i256Reg[0] + i256Reg[1], i256Reg[2]));

                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        state.GasAvailable -= GasCostOf.Mid;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        stack.Push(
                            i256Reg[2] == BigInteger.Zero
                                ? BigInteger.Zero
                                : BigInteger.Remainder(i256Reg[0] * i256Reg[1], i256Reg[2]));

                        break;
                    }
                    case Instruction.EXP:
                    {
                        state.GasAvailable -= GasCostOf.Exp;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (i256Reg[1] > BigInteger.Zero)
                        {
                            int expSize = (int)BigInteger.Log(i256Reg[1], 256);
                            i256Reg[2] = BigInteger.Pow(BigInt256, expSize);
                            i256Reg[3] = i256Reg[2] * BigInt256;
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

                        if (i256Reg[0] == BigInteger.Zero)
                        {
                            stack.Push(BigInteger.Zero);
                        }
                        else if (i256Reg[0] == BigInteger.One)
                        {
                            stack.Push(BigInteger.One);
                        }
                        else
                        {
                            stack.Push(BigInteger.ModPow(i256Reg[0], i256Reg[1], P256Int));
                        }

                        break;
                    }
                    case Instruction.SIGNEXTEND:
                    {
                        state.GasAvailable -= GasCostOf.Low;
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bits1 = bytesReg[1].ToBigEndianBitArray256();

                        int bitNumber =
                            (int)BigInteger.Max(0, 256 - 8 * (bytesReg[0].ToUnsignedBigInteger() + BigInteger.One));
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
                        stack.Push(i256Reg[0] < i256Reg[1] ? One : Zero);
                        break;
                    }
                    case Instruction.GT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        stack.Push(i256Reg[0] > i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SLT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        stack.Push(BigInteger.Compare(i256Reg[0], i256Reg[1]) < 0 ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SGT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        stack.Push(i256Reg[0] > i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.EQ:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        stack.Push(i256Reg[0] == i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopInt();
                        stack.Push(i256Reg[0].IsZero ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.AND:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bits1 = bytesReg[0].ToBigEndianBitArray256();
                        bits2 = bytesReg[1].ToBigEndianBitArray256();
                        stack.Push(bits1.And(bits2).ToBytes());
                        break;
                    }
                    case Instruction.OR:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
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
                        bits1 = bytesReg[0].ToBigEndianBitArray256();
                        bits2 = bytesReg[1].ToBigEndianBitArray256();
                        stack.Push(bits1.Xor(bits2).ToBytes());
                        break;
                    }
                    case Instruction.NOT:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        bytesReg[0] = PopBytes();
                        byte[] result = new byte[32];
                        for (int i = 0; i < 32; ++i)
                        {
                            if (bytesReg[0].Length < 32 - i)
                            {
                                result[i] = 0xff;
                            }
                            else
                            {
                                result[i] = (byte)~bytesReg[0][i - (32 - bytesReg[0].Length)];
                            }
                        }

                        stack.Push(result.WithoutLeadingZeros());
                        break;
                    }
                    case Instruction.BYTE:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        bytesReg[0] = PopBytes();

                        if (i256Reg[0] > BigInt32)
                        {
                            stack.Push(Zero);
                            break;
                        }

                        intReg = bytesReg[0].Length - 32 + (int)i256Reg[0];
                        stack.Push(intReg < 0 ? Zero : bytesReg[0].Slice(intReg, 1));
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

                        //state.GasAvailable -=
                        //    CalculateMemoryCost(state.ActiveWordsInMemory, i256Reg[2]);
                        BigInteger memCost = CalculateMemoryCost(state.ActiveWordsInMemory, i256Reg[2]);
                        state.GasAvailable -= memCost;
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
                        bytesReg[0] = PopAddress();
                        Account account = worldStateProvider.GetAccount(new Address(bytesReg[0]));
                        stack.Push(account?.Balance ?? BigInteger.Zero);
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
                        stack.Push(env.Value);
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
                        stack.Push(GetPaddedSlice(env.InputData, i256Reg[0], 32));
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.InputData.Length);
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        byte[] callDataSlice = GetPaddedSlice(env.InputData, i256Reg[1], i256Reg[2]);
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
                        stack.Push(env.MachineCode.Length);
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        byte[] callDataSlice = GetPaddedSlice(env.MachineCode, i256Reg[1], i256Reg[2]);
                        BigInteger newMemoryState = state.Memory.Save(i256Reg[0], callDataSlice);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, newMemoryState);
                        state.GasAvailable -= GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]);
                        state.ActiveWordsInMemory = newMemoryState;
                        break;
                    }
                    case Instruction.GASPRICE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.GasPrice);
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        state.GasAvailable -= GasCostOf.ExtCodeSize;
                        bytesReg[0] = PopBytes();
                        Address address = new Address(bytesReg[0].Slice(bytesReg[0].Length - 20, 20));
                        Account account =
                            worldStateProvider.GetOrCreateAccount(address);
                        byte[] accountCode = worldStateProvider.GetCode(account.CodeHash);
                        stack.Push(accountCode?.Length ?? BigInteger.Zero);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        state.GasAvailable -= GasCostOf.ExtCode;
                        bytesReg[0] = PopAddress();
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        Address address = new Address(bytesReg[0]);
                        Account account = worldStateProvider.GetAccount(address);

                        byte[] externalCode;
                        if (account == null)
                        {
                            externalCode = new byte[] { 0 };
                        }
                        else
                        {
                            externalCode = worldStateProvider.GetCode(account.CodeHash);
                        }

                        byte[] callDataSlice = GetPaddedSlice(externalCode, i256Reg[1], i256Reg[2]);
                        BigInteger newMemoryState = state.Memory.Save(i256Reg[0], callDataSlice);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, newMemoryState);
                        state.GasAvailable -= GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]);
                        state.ActiveWordsInMemory = newMemoryState;
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        state.GasAvailable -= GasCostOf.BlockHash;
                        i256Reg[0] = PopUInt();
                        if (i256Reg[0] > BigInt256)
                        {
                            stack.Push(Zero);
                        }
                        else if (i256Reg[0] == BigInteger.Zero)
                        {
                            stack.Push(Zero);
                        }
                        else
                        {
                            stack.Push(blockhashProvider.GetBlockhash(env.CurrentBlock, (int)i256Reg[0]).Bytes);
                        }

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.CurrentBlock.Header.Beneficiary.Hex);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.CurrentBlock.Header.Difficulty);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.CurrentBlock.Header.Timestamp);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.CurrentBlock.Header.Number);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(env.CurrentBlock.Header.GasLimit);
                        break;
                    }
                    case Instruction.POP:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.PopLimbo();
                        break;
                    }
                    case Instruction.MLOAD:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
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
                        i256Reg[1] = state.Memory.SaveWord(i256Reg[0], bytesReg[1]);
                        state.GasAvailable -=
                            CalculateMemoryCost(state.ActiveWordsInMemory, i256Reg[1]);
                        state.ActiveWordsInMemory = i256Reg[1];
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        state.GasAvailable -= GasCostOf.VeryLow;
                        i256Reg[0] = PopUInt();
                        bytesReg[1] = PopBytes();
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
                        StorageTree storage = storageProvider.GetOrCreateStorage(env.CodeOwner);
                        byte[] value = storage.Get(i256Reg[0]);
                        stack.Push(value);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        i256Reg[0] = PopInt();
                        bytesReg[0] = PopBytes();
                        StorageTree storage = storageProvider.GetOrCreateStorage(env.CodeOwner);
                        byte[] previousValue = storage.Get(i256Reg[0]);

                        bool isNewValueZero = bytesReg[0].IsZero();
                        bool isValueChanged = !(isNewValueZero && previousValue.IsZero()) ||
                                              !Bytes.UnsafeCompare(previousValue, bytesReg[0]);
                        if (isNewValueZero)
                        {
                            state.GasAvailable -= GasCostOf.SReset;
                            if (isValueChanged)
                            {
                                refund += RefundOf.SClear;
                            }
                        }
                        else
                        {
                            if (previousValue.IsZero())
                            {
                                state.GasAvailable -= GasCostOf.SSet;
                            }
                            else
                            {
                                state.GasAvailable -= GasCostOf.SReset;
                            }
                        }

                        // do not apply changes if not possible (reverting state)
                        if (isValueChanged && state.GasAvailable >= 0)
                        {
                            storage.Set(i256Reg[0], isNewValueZero ? new byte[] { 0 } : bytesReg[0].WithoutLeadingZeros());
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        state.GasAvailable -= GasCostOf.Mid;
                        i256Reg[0] = PopUInt();
                        state.ProgramCounter = i256Reg[0];

                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        state.GasAvailable -= GasCostOf.High;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (i256Reg[1] > BigInteger.Zero)
                        {
                            state.ProgramCounter = i256Reg[0];
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(state.ProgramCounter - 1);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(state.ActiveWordsInMemory * 32);
                        break;
                    }
                    case Instruction.GAS:
                    {
                        state.GasAvailable -= GasCostOf.Base;
                        stack.Push(state.GasAvailable);
                        break;
                    }
                    case Instruction.JUMPDEST:
                    {
                        state.GasAvailable -= GasCostOf.JumpDest;
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
                        intReg = instruction - Instruction.PUSH1 + 1;
                        int usedFromCode = (int)BigInteger.Min(code.Length - state.ProgramCounter, intReg);

                        stack.Push(usedFromCode != intReg
                            ? code.Slice((int)state.ProgramCounter, usedFromCode).PadRight(intReg)
                            : code.Slice((int)state.ProgramCounter, usedFromCode));

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
                        stack.Dup(instruction - Instruction.DUP1 + 1);
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
                        stack.Swap(instruction - Instruction.SWAP1 + 2);
                        break;
                    }
                    case Instruction.LOG0:
                    case Instruction.LOG1:
                    case Instruction.LOG2:
                    case Instruction.LOG3:
                    case Instruction.LOG4:
                    {
                        state.GasAvailable -= GasCostOf.Log;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        intReg = instruction - Instruction.LOG0;
                        byte[][] topics = new byte[intReg][];
                        for (int i = 0; i < intReg; i++)
                        {
                            topics[i] = PopBytes().PadLeft(32);
                        }

                        state.GasAvailable -= intReg * GasCostOf.LogTopic;
                        bytesReg[0] = state.Memory.Load(i256Reg[0], i256Reg[1], false).Item1;

                        state.GasAvailable -= bytesReg[0].Length * GasCostOf.LogData;

                        LogEntry logEntry = new LogEntry(
                            env.CodeOwner,
                            bytesReg[0],
                            topics);
                        logs.Add(logEntry);
                        break;
                    }
                    case Instruction.CREATE:
                    {
                        state.GasAvailable -= GasCostOf.Create;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        Account codeOwner = worldStateProvider.GetAccount(env.CodeOwner);
                        (byte[] accountCode, BigInteger newMemoryAllocation) =
                            state.Memory.Load(i256Reg[1], i256Reg[2], false);

                        Account account = new Account();
                        account.Balance = i256Reg[0];
                        if (i256Reg[0] > (codeOwner?.Balance ?? 0))
                        {
                            return (new byte[0], null);
                        }

                        Keccak codeHash = worldStateProvider.UpdateCode(accountCode);
                        account.CodeHash = codeHash;
                        account.Nonce = 0;

                        Address address = new Address(Keccak.Compute(Rlp.Encode(env.CodeOwner, codeOwner.Nonce - 1).Bytes));
                        worldStateProvider.State.Set(address, Rlp.Encode(account));
                        stack.Push(address.Hex);

                        break;
                    }
                    case Instruction.RETURN:
                    {
                        state.GasAvailable -= GasCostOf.Zero;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        totalTime.Stop();
                        Console.WriteLine(totalTime.ElapsedMilliseconds);
                        (byte[] returnValue, BigInteger newMemory) = state.Memory.Load(i256Reg[0], i256Reg[1]);
                        state.GasAvailable -= CalculateMemoryCost(state.ActiveWordsInMemory, newMemory);
                        state.ActiveWordsInMemory = newMemory;

                        return (returnValue, new TransactionSubstate(refund, destroyList, logs));
                    }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    {
                        if (state.GasAvailable < 0)
                        {
                            throw new OutOfGasException();
                        }

                        i256Reg[0] = PopUInt(); // gas
                        Address target = new Address(PopAddress());
                        i256Reg[1] = PopUInt(); // value
                        i256Reg[2] = PopUInt(); // data offset
                        i256Reg[3] = PopUInt(); // data length
                        i256Reg[4] = PopUInt(); // output offset
                        i256Reg[5] = PopUInt(); // output length
                        BigInteger gasExtra = instruction == Instruction.CALL ? GasCostOf.Call : GasCostOf.CallCode;
                        if (!i256Reg[1].IsZero)
                        {
                            gasExtra += GasCostOf.CallValue - GasCostOf.CallStipend;
                        }

                        state.GasAvailable -= gasExtra;

                        (byte[] callData, BigInteger newMemory) = state.Memory.Load(i256Reg[2], i256Reg[3]);
                        state.GasAvailable -= CalculateMemoryCost(state.ActiveWordsInMemory, newMemory);
                        state.ActiveWordsInMemory = newMemory;

                        if (!i256Reg[1].IsZero)
                        {
                            Account codeOwnerAccount = worldStateProvider.GetAccount(env.CodeOwner);
                            if (codeOwnerAccount.Balance < i256Reg[1])
                            {
                                stack.Push(BigInteger.One);
                                newMemory = state.Memory.Save(i256Reg[4], new byte[(int)i256Reg[5]]);
                                state.GasAvailable -= CalculateMemoryCost(state.ActiveWordsInMemory, newMemory);
                                state.ActiveWordsInMemory = newMemory;
                                break;
                            }

                            codeOwnerAccount.Balance -= i256Reg[1]; // do not subtract if failed
                            worldStateProvider.UpdateAccount(env.CodeOwner, codeOwnerAccount);
                        }

                        Account targetAccount = worldStateProvider.GetAccount(target);
                        if (targetAccount == null)
                        {
                            gasExtra += GasCostOf.NewAccount;
                            state.GasAvailable -= GasCostOf.NewAccount;
                            targetAccount = new Account();
                            targetAccount.Balance = i256Reg[1];
                            worldStateProvider.UpdateAccount(target, targetAccount);
                        }

                        targetAccount.Balance += i256Reg[1];
                        worldStateProvider.UpdateAccount(target, targetAccount);

                        BigInteger gasCap = gasExtra < state.GasAvailable
                            ? BigInteger.Min((state.GasAvailable - gasExtra) - (state.GasAvailable - gasExtra) / 64, i256Reg[0])
                            : i256Reg[0];

                        if (state.GasAvailable < gasCap + gasExtra)
                        {
                            throw new OutOfGasException();
                        }

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.Value = i256Reg[1];
                        callEnv.Caller = env.CodeOwner;
                        callEnv.Originator = env.Originator;
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.InputData = callData;
                        callEnv.CodeOwner = instruction == Instruction.CALL ? target : env.CodeOwner;
                        callEnv.MachineCode = worldStateProvider.GetCode(targetAccount.CodeHash);

                        if (callEnv.CallDepth >= 1024)
                        {
                            throw new InvalidOperationException();
                        }

                        // take snapshot

                        try
                        {
                            BigInteger callGas =
                                i256Reg[1].IsZero
                                    ? gasCap
                                    : gasCap + GasCostOf.CallStipend;
                            // stipend only with value
                            EvmState callState = new EvmState(callGas);
                            (byte[] callOutput, TransactionSubstate callResult) = Run(
                                callEnv,
                                callState,
                                blockhashProvider,
                                worldStateProvider,
                                storageProvider);
                            //state.GasAvailable -= callGas - callState.GasAvailable;
                            newMemory = state.Memory.Save(i256Reg[4], GetPaddedSlice(callOutput, 0, i256Reg[5]));
                            state.GasAvailable -= CalculateMemoryCost(state.ActiveWordsInMemory, newMemory);
                            state.ActiveWordsInMemory = newMemory;
                            stack.Push(1);
                        }
                        catch (Exception)
                        {
                            if (ShouldLog.VM)
                            {
                                Console.WriteLine("FAIL");
                            }

                            // restore from snapshot

                            stack.Push(1);
                        }

                        break;
                    }
                    case Instruction.INVALID:
                    {
                        break;
                    }
                    case Instruction.SUICIDE:
                    {
                        state.GasAvailable -= GasCostOf.Suicide;
                        bytesReg[0] = PopAddress();
                        if (!destroyList.Contains(env.CodeOwner))
                        {
                            destroyList.Add(env.CodeOwner);
                            refund += RefundOf.Destroy;
                        }

                        Account codeOwnerAccount = worldStateProvider.GetAccount(env.CodeOwner);
                        Address inheritorAddress = new Address(bytesReg[0]);
                        Account inheritorAccount = worldStateProvider.GetAccount(inheritorAddress);
                        if (inheritorAccount == null)
                        {
                            inheritorAccount = new Account();
                            inheritorAccount.Balance = codeOwnerAccount.Balance;
                            if (env.CurrentBlock.Header.Number > DaoExploitFixBlockNumber)
                            {
                                state.GasAvailable -= GasCostOf.NewAccount;
                            }
                        }
                        else
                        {
                            inheritorAccount.Balance += codeOwnerAccount.Balance;
                        }

                        worldStateProvider.UpdateAccount(inheritorAddress, inheritorAccount);
                        codeOwnerAccount.Balance = BigInteger.Zero;
                        worldStateProvider.UpdateAccount(env.CodeOwner, codeOwnerAccount);

                        return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
                    }
                    default:
                    {
                        if (ShouldLog.VM)
                        {
                            Console.WriteLine("UNKNOWN INSTRUCTION");
                        }

                        throw new InvalidOperationException();
                    }
                }

                stopwatches[(int)instruction].Stop();


                if (ShouldLog.VM)
                {
                    string extraInfo = instruction == Instruction.CALL || instruction == Instruction.CALLCODE ? " AFTER CALL " : " ";
                    Console.WriteLine($"  GAS{extraInfo}{state.GasAvailable} ({gasBefore - state.GasAvailable}) ({instruction})");
                }
            }

            totalTime.Stop();
            Console.WriteLine(totalTime.ElapsedMilliseconds);

            return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
        }

        private Dictionary<BigInteger, byte[]> TakeStorageSnapshot(Dictionary<BigInteger, byte[]> storage)
        {
            Dictionary<BigInteger, byte[]> snapshot = new Dictionary<BigInteger, byte[]>();
            foreach (KeyValuePair<BigInteger, byte[]> keyValuePair in storage)
            {
                snapshot[keyValuePair.Key] = keyValuePair.Value; // no need to clone, immutable
            }

            return snapshot;
        }

        private void ApplyStateChanges(EvmState state, IStorageProvider storageProvider)
        {
            if (state.GasAvailable < 0)
            {
                return;
            }

            throw new NotImplementedException();
        }

        public static BigInteger CalculateMemoryCost(BigInteger initial, BigInteger final)
        {
            return
                final * GasCostOf.Memory + BigInteger.Divide(BigInteger.Pow(final, 2), 512)
                - initial * GasCostOf.Memory - BigInteger.Divide(BigInteger.Pow(initial, 2), 512);
        }
    }
}