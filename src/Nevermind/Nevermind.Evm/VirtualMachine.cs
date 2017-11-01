using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Evm.Precompiles;
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

        private static readonly Dictionary<BigInteger, IPrecompiledContract> PrecompiledContracts;

        public static readonly BigInteger DaoExploitFixBlockNumber = 10
            ; // have not found this yet, setting to a random value for tests to pass

        static VirtualMachine()
        {
            PrecompiledContracts = new Dictionary<BigInteger, IPrecompiledContract>
            {
                [ECRecoverPrecompiledContract.Instance.Address] = ECRecoverPrecompiledContract.Instance,
                [Sha256PrecompiledContract.Instance.Address] = Sha256PrecompiledContract.Instance,
                [Ripemd160PrecompiledContract.Instance.Address] = Ripemd160PrecompiledContract.Instance,
                [IdentityPrecompiledContract.Instance.Address] = IdentityPrecompiledContract.Instance
            };
        }

        private static Address ToAddress(byte[] word)
        {
            if (word.Length < 20)
            {
                word = word.PadLeft(20);
            }

            return word.Length == 20 ? new Address(word) : new Address(word.Slice(word.Length - 20, 20));
        }

        public (byte[] output, TransactionSubstate) Run(
            ExecutionEnvironment env,
            EvmState state,
            IBlockhashProvider blockhashProvider,
            IWorldStateProvider worldStateProvider,
            IStorageProvider storageProvider)
        {
            EvmStack stack = state.Stack;
            EvmMemory memory = state.Memory;
            byte[] code = env.MachineCode;
            BitArray jumpDestinations = new BitArray(code.Length);
            HashSet<Address> destroyList = new HashSet<Address>();
            List<LogEntry> logs = new List<LogEntry>();

            // TODO: outside and inline?
            BigInteger PopUInt()
            {
                return stack.PopInt(false);
            }

            // TODO: outside and inline?
            BigInteger PopInt()
            {
                return stack.PopInt(true);
            }

            // TODO: outside and inline?
            byte[] PopBytes()
            {
                return stack.PopBytes();
            }

            // TODO: outside and inline?
            Address PopAddress()
            {
                return ToAddress(stack.PopBytes());
            }

            // TODO: outside and inline?
            void UpdateGas(BigInteger gasCost)
            {
                // TODO: can get the bas instruction cost here
                if(state.GasAvailable < gasCost)
                {
                    throw new OutOfGasException();
                }

                state.GasAvailable -= gasCost;
            }

            void UpdateMemoryCost(BigInteger newActiveWords)
            {
                UpdateGas(CalculateMemoryCost(state.ActiveWordsInMemory, newActiveWords));
                state.ActiveWordsInMemory = newActiveWords;
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

            BigInteger[] i256Reg = new BigInteger[17]; // TODO: can remove now after writing dup / swap
            byte[][] bytesReg = new byte[17][];

            BigInteger refund = BigInteger.Zero;

            BitArray bits1 = new BitArray(256); // TODO: reuse object
            BitArray bits2 = new BitArray(256); // TODO: reuse object

            void ValidateJump(int destination)
            {
                if (destination < 0 || destination >= jumpDestinations.Length)
                {
                    // || !jumpDestinations[destination]
                    throw new InvalidJumpDestinationException();
                }
            }

            int[] calls = new int[256];
            Stopwatch[] stopwatches = new Stopwatch[256];
            for (int i = 0; i < 256; i++)
            {
                stopwatches[i] = new Stopwatch();
            }

            Stopwatch totalTime = new Stopwatch();
            totalTime.Start();
            BigInteger gasBefore = state.GasAvailable;
            while (true)
            {
                if (state.ProgramCounter >= code.Length)
                {
                    break;
                }

                Instruction instruction = (Instruction)code[(int)state.ProgramCounter];
                state.ProgramCounter++;
                stopwatches[(int)instruction].Start();
                calls[(int)instruction]++;

                if (ShouldLog.Evm)
                {
                    Console.WriteLine($"{instruction} (0x{instruction:X})");
                }

                int intReg;
                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
                    }
                    case Instruction.ADD:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        i256Reg[2] = i256Reg[0] + i256Reg[1];
                        stack.Push(
                            i256Reg[2] >= P256Int
                                ? i256Reg[2] - P256Int
                                : i256Reg[2]);

                        break;
                    }
                    case Instruction.MUL:
                    {
                        UpdateGas(GasCostOf.Low);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        stack.Push(BigInteger.Remainder(i256Reg[0] * i256Reg[1], P256Int)
                        );
                        break;
                    }
                    case Instruction.SUB:
                    {
                        UpdateGas(GasCostOf.VeryLow);
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
                        UpdateGas(GasCostOf.Low);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        stack.Push(i256Reg[1] == BigInteger.Zero
                            ? BigInteger.Zero
                            : BigInteger.Divide(i256Reg[0], i256Reg[1]));
                        break;
                    }
                    case Instruction.SDIV:
                    {
                        UpdateGas(GasCostOf.Low);
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
                        UpdateGas(GasCostOf.Low);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        stack.Push(i256Reg[1] == BigInteger.Zero
                            ? BigInteger.Zero
                            : BigInteger.Remainder(i256Reg[0], i256Reg[1]));
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        UpdateGas(GasCostOf.Low);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        stack.Push(
                            i256Reg[1] == BigInteger.Zero
                                ? Zero
                                : (i256Reg[0].Sign * BigInteger.Remainder(i256Reg[0].Abs(), i256Reg[1].Abs()))
                                .ToBigEndianByteArray(true, 32));

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        UpdateGas(GasCostOf.Mid);
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
                        UpdateGas(GasCostOf.Mid);
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
                        UpdateGas(GasCostOf.Exp);
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

                            UpdateGas(GasCostOf.ExpByte * (1 + expSize));
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
                        UpdateGas(GasCostOf.Low);
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bytesReg[1].ToBigEndianBitArray256(ref bits1);
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
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        stack.Push(i256Reg[0] < i256Reg[1] ? One : Zero);
                        break;
                    }
                    case Instruction.GT:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        stack.Push(i256Reg[0] > i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SLT:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        stack.Push(BigInteger.Compare(i256Reg[0], i256Reg[1]) < 0 ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SGT:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        stack.Push(i256Reg[0] > i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.EQ:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        stack.Push(i256Reg[0] == i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopInt();
                        stack.Push(i256Reg[0].IsZero ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.AND:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bytesReg[0].ToBigEndianBitArray256(ref bits1);
                        bytesReg[1].ToBigEndianBitArray256(ref bits2);
                        stack.Push(bits1.And(bits2).ToBytes());
                        break;
                    }
                    case Instruction.OR:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bytesReg[0].ToBigEndianBitArray256(ref bits1);
                        bytesReg[1].ToBigEndianBitArray256(ref bits2);
                        stack.Push(bits1.Or(bits2).ToBytes());
                        break;
                    }
                    case Instruction.XOR:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bytesReg[0].ToBigEndianBitArray256(ref bits1);
                        bytesReg[1].ToBigEndianBitArray256(ref bits2);
                        stack.Push(bits1.Xor(bits2).ToBytes());
                        break;
                    }
                    case Instruction.NOT:
                    {
                        UpdateGas(GasCostOf.VeryLow);
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
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopUInt();
                        bytesReg[0] = PopBytes();

                        if (i256Reg[0] >= BigInt32)
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
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        
                        (bytesReg[0], i256Reg[2]) = memory.Load(i256Reg[0], i256Reg[1]);
                        BigInteger memCost = CalculateMemoryCost(state.ActiveWordsInMemory, i256Reg[2]);
                        UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmMemory.Div32Ceiling(i256Reg[1]) + memCost);
                        state.ActiveWordsInMemory = i256Reg[2];
                        stack.Push(Keccak.Compute(bytesReg[0]).Bytes);
                        
                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.CodeOwner.Hex);
                        break;
                    }
                    case Instruction.BALANCE:
                    {
                        UpdateGas(GasCostOf.Balance);
                        Address address = PopAddress();
                        Account account = worldStateProvider.GetAccount(address);
                        stack.Push(account?.Balance ?? BigInteger.Zero);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.Caller.Hex);
                        break;
                    }
                    case Instruction.CALLVALUE:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.Value);
                        break;
                    }
                    case Instruction.ORIGIN:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.Originator.Hex);
                        break;
                    }
                    case Instruction.CALLDATALOAD:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopUInt();
                        stack.Push(GetPaddedSlice(env.InputData, i256Reg[0], 32));
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.InputData.Length);
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]));

                        byte[] callDataSlice = GetPaddedSlice(env.InputData, i256Reg[1], i256Reg[2]);
                        BigInteger newMemoryState = memory.Save(i256Reg[0], callDataSlice);
                        UpdateMemoryCost(newMemoryState);
                        break;
                    }
                    case Instruction.CODESIZE:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(code.Length);
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]));

                        byte[] callDataSlice = GetPaddedSlice(code, i256Reg[1], i256Reg[2]);
                        BigInteger newMemoryState = memory.Save(i256Reg[0], callDataSlice);
                        UpdateMemoryCost(newMemoryState);
                        break;
                    }
                    case Instruction.GASPRICE:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.GasPrice);
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        UpdateGas(GasCostOf.ExtCodeSize);
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
                        Address address = PopAddress();
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        UpdateGas(GasCostOf.ExtCode + GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]));

                        Account account = worldStateProvider.GetAccount(address);
                        byte[] externalCode = account == null ? new byte[] { 0 } : worldStateProvider.GetCode(account.CodeHash);
                        byte[] callDataSlice = GetPaddedSlice(externalCode, i256Reg[1], i256Reg[2]);
                        BigInteger newMemoryState = memory.Save(i256Reg[0], callDataSlice);
                        UpdateMemoryCost(newMemoryState);
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        UpdateGas(GasCostOf.BlockHash);
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
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.CurrentBlock.Header.Beneficiary.Hex);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.CurrentBlock.Header.Difficulty);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.CurrentBlock.Header.Timestamp);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.CurrentBlock.Header.Number);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(env.CurrentBlock.Header.GasLimit);
                        break;
                    }
                    case Instruction.POP:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.PopLimbo();
                        break;
                    }
                    case Instruction.MLOAD:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopUInt();
                        (bytesReg[0], i256Reg[1]) = memory.Load(i256Reg[0]);
                        UpdateMemoryCost(i256Reg[1]);
                        stack.Push(bytesReg[0]);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopUInt();
                        bytesReg[1] = PopBytes();
                        i256Reg[1] = memory.SaveWord(i256Reg[0], bytesReg[1]);
                        UpdateMemoryCost(i256Reg[1]);
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        UpdateGas(GasCostOf.VeryLow);
                        i256Reg[0] = PopUInt();
                        bytesReg[1] = PopBytes();
                        i256Reg[1] = memory.SaveByte(i256Reg[0], bytesReg[1]);
                        UpdateMemoryCost(i256Reg[1]);
                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        UpdateGas(GasCostOf.SLoad);
                        i256Reg[0] = PopUInt();
                        StorageTree storage = storageProvider.GetOrCreateStorage(env.CodeOwner);
                        byte[] value = storage.Get(i256Reg[0]);
                        stack.Push(value);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        i256Reg[0] = PopUInt();
                        bytesReg[0] = PopBytes();
                        StorageTree storage = storageProvider.GetOrCreateStorage(env.CodeOwner);
                        byte[] previousValue = storage.Get(i256Reg[0]);

                        bool isNewValueZero = bytesReg[0].IsZero();
                        bool isValueChanged = !(isNewValueZero && previousValue.IsZero()) ||
                                              !Bytes.UnsafeCompare(previousValue, bytesReg[0]);
                        if (isNewValueZero)
                        {
                            UpdateGas(GasCostOf.SReset);
                            if (isValueChanged)
                            {
                                refund += RefundOf.SClear;
                            }
                        }
                        else
                        {
                            UpdateGas(previousValue.IsZero() ? GasCostOf.SSet : GasCostOf.SReset);
                        }

                        if (isValueChanged)
                        {
                            storage.Set(i256Reg[0],
                                isNewValueZero ? new byte[] { 0 } : bytesReg[0].WithoutLeadingZeros());
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        UpdateGas(GasCostOf.Mid);
                        i256Reg[0] = PopUInt();
                        ValidateJump((int)i256Reg[0]);
                        
                        state.ProgramCounter = i256Reg[0];
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        UpdateGas(GasCostOf.High);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (i256Reg[1] > BigInteger.Zero)
                        {
                            ValidateJump((int)i256Reg[0]);
                            state.ProgramCounter = i256Reg[0];
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(state.ProgramCounter - 1);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        UpdateGas(GasCostOf.Base);
                        stack.Push(state.ActiveWordsInMemory * 32);
                        break;
                    }
                    case Instruction.GAS:
                    {
                        UpdateGas(GasCostOf.Base);                        
                        stack.Push(state.GasAvailable);
                        break;
                    }
                    case Instruction.JUMPDEST:
                    {
                        UpdateGas(GasCostOf.JumpDest);
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
                        UpdateGas(GasCostOf.VeryLow);
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
                        UpdateGas(GasCostOf.VeryLow);
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
                        UpdateGas(GasCostOf.VeryLow);
                        stack.Swap(instruction - Instruction.SWAP1 + 2);
                        break;
                    }
                    case Instruction.LOG0:
                    case Instruction.LOG1:
                    case Instruction.LOG2:
                    case Instruction.LOG3:
                    case Instruction.LOG4:
                    {
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        intReg = instruction - Instruction.LOG0;
                        bytesReg[0] = memory.Load(i256Reg[0], i256Reg[1], false).Item1;
                        UpdateGas(GasCostOf.Log + intReg * GasCostOf.LogTopic + bytesReg[0].Length * GasCostOf.LogData);

                        byte[][] topics = new byte[intReg][];
                        for (int i = 0; i < intReg; i++)
                        {
                            topics[i] = PopBytes().PadLeft(32);
                        }

                        LogEntry logEntry = new LogEntry(
                            env.CodeOwner,
                            bytesReg[0],
                            topics);
                        logs.Add(logEntry);
                        break;
                    }
                    case Instruction.CREATE:
                    {
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        UpdateGas(GasCostOf.Create + GasCostOf.CodeDeposit * i256Reg[2]);

                        Account codeOwner = worldStateProvider.GetAccount(env.CodeOwner);
                        (byte[] accountCode, BigInteger newMemoryAllocation) =
                            memory.Load(i256Reg[1], i256Reg[2], false);
                        UpdateMemoryCost(newMemoryAllocation);

                        Account account = new Account();
                        account.Balance = i256Reg[0];
                        if (i256Reg[0] > (codeOwner?.Balance ?? 0))
                        {
                            stack.Push(BigInteger.Zero);
                            break;
                        }

                        Keccak codeHash = worldStateProvider.UpdateCode(accountCode);
                        account.CodeHash = codeHash;
                        account.Nonce = 0;

                        Keccak newAddress = Keccak.Compute(Rlp.Encode(env.CodeOwner, codeOwner.Nonce));
                        Address address = new Address(newAddress);
                        worldStateProvider.UpdateAccount(address, account);
                        stack.Push(address.Hex);
                        break;
                    }
                    case Instruction.RETURN:
                    {
                        BigInteger gasCost = GasCostOf.Zero;;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        (byte[] returnValue, BigInteger newMemory) = memory.Load(i256Reg[0], i256Reg[1]);
                        gasCost += CalculateMemoryCost(state.ActiveWordsInMemory, newMemory);

                        UpdateGas(gasCost);

                        totalTime.Stop();
                        Console.WriteLine(totalTime.ElapsedMilliseconds);
                        state.ActiveWordsInMemory = newMemory;

                        return (returnValue, new TransactionSubstate(refund, destroyList, logs));
                    }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    {
                        if (env.CallDepth >= 1024)
                        {
                            throw new CallDepthException();
                        }

                        BigInteger failure = BigInteger.One; // seems that tests are incorrect here...
                        BigInteger success = BigInteger.One; //failure.IsZero ? BigInteger.One : BigInteger.Zero;

                        i256Reg[0] = PopUInt(); // gas
                        bytesReg[0] = PopBytes();
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

                        UpdateGas(gasExtra);

                        (byte[] callData, BigInteger newMemory) = memory.Load(i256Reg[2], i256Reg[3]);
                        UpdateMemoryCost(newMemory);

                        i256Reg[6] = bytesReg[0].ToUnsignedBigInteger();

                        Address target = ToAddress(bytesReg[0]);
                        if (target.Equals(env.CodeOwner))
                        {
                            stack.Push(failure);
                            break;
                        }

                        if (!i256Reg[1].IsZero)
                        {
                            Account codeOwnerAccount = worldStateProvider.GetAccount(env.CodeOwner);
                            if (codeOwnerAccount.Balance < i256Reg[1])
                            {
                                newMemory = memory.Save(i256Reg[4], new byte[(int)i256Reg[5]]);
                                UpdateMemoryCost(newMemory);
                                stack.Push(failure);
                                break;
                            }

                            codeOwnerAccount.Balance -= i256Reg[1]; // do not subtract if failed
                            worldStateProvider.UpdateAccount(env.CodeOwner, codeOwnerAccount);
                        }

                        Account targetAccount = worldStateProvider.GetAccount(target);
                        if (targetAccount == null)
                        {
                            gasExtra += GasCostOf.NewAccount;
                            UpdateGas(GasCostOf.NewAccount); // TODO: check this earlier?
                            targetAccount = new Account();
                            targetAccount.Balance = i256Reg[1];
                            worldStateProvider.UpdateAccount(target, targetAccount);
                        }
                        else
                        {
                            targetAccount.Balance += i256Reg[1];
                            worldStateProvider.UpdateAccount(target, targetAccount);
                        }

                        if (i256Reg[6] <= 4 && i256Reg[6] != 0)
                        {
                            BigInteger gasCost = PrecompiledContracts[i256Reg[6]].GasCost(env.InputData);
                            UpdateGas(gasCost); // TODO: check EIP-150
                            stack.Push(PrecompiledContracts[i256Reg[6]].Run(env.InputData));
                            stack.Push(success);
                            break;
                        }

                        BigInteger gasCap = i256Reg[0];

                        bool eip150 = false;
                        if (eip150)
                        {
                            gasCap = gasExtra < state.GasAvailable
                                ? BigInteger.Min(state.GasAvailable - gasExtra - (state.GasAvailable - gasExtra) / 64,
                                    i256Reg[0])
                                : i256Reg[0];
                        }
                        else if (state.GasAvailable < gasCap)
                        {
                            throw new OutOfGasException(); // no EIP-150
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

                        StateSnapshot stateSnapshot = worldStateProvider.TakeSnapshot();
                        StateSnapshot storageSnapshot = storageProvider.TakeSnapshot(callEnv.CodeOwner);

                        BigInteger callGas =
                            i256Reg[1].IsZero
                                ? gasCap
                                : gasCap + GasCostOf.CallStipend;

                        try
                        {
                            // stipend only with value
                            EvmState callState = new EvmState(callGas);
                            (byte[] callOutput, TransactionSubstate callResult) = Run(
                                callEnv,
                                callState,
                                blockhashProvider,
                                worldStateProvider,
                                storageProvider);

                            //state.GasAvailable -= callGas - callState.GasAvailable;
                            newMemory = memory.Save(i256Reg[4], GetPaddedSlice(callOutput, 0, i256Reg[5]));
                            UpdateMemoryCost(newMemory);
                            stack.Push(success);
                        }
                        catch (Exception ex)
                        {
                            if (ShouldLog.Evm)
                            {
                                Console.WriteLine($"FAIL {ex.GetType().Name}");
                            }

                            worldStateProvider.Restore(stateSnapshot);
                            storageProvider.Restore(callEnv.CodeOwner, storageSnapshot);

                            stack.Push(failure);
                        }

                        break;
                    }
                    case Instruction.INVALID:
                    {
                        break;
                    }
                    case Instruction.SELFDESTRUCT:
                    {
                        UpdateGas(GasCostOf.SelfDestruct);
                        Address inheritor = PopAddress();
                        if (!destroyList.Contains(env.CodeOwner))
                        {
                            destroyList.Add(env.CodeOwner);
                            refund += RefundOf.Destroy;
                        }

                        Account codeOwnerAccount = worldStateProvider.GetAccount(env.CodeOwner);
                        Account inheritorAccount = worldStateProvider.GetAccount(inheritor);
                        if (inheritorAccount == null)
                        {
                            inheritorAccount = new Account();
                            inheritorAccount.Balance = codeOwnerAccount.Balance;
                            if (env.CurrentBlock.Header.Number > DaoExploitFixBlockNumber)
                            {
                                UpdateGas(GasCostOf.NewAccount);
                            }
                        }
                        else
                        {
                            inheritorAccount.Balance += codeOwnerAccount.Balance;
                        }

                        worldStateProvider.UpdateAccount(inheritor, inheritorAccount);
                        codeOwnerAccount.Balance = BigInteger.Zero;
                        worldStateProvider.UpdateAccount(env.CodeOwner, codeOwnerAccount);

                        return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
                    }
                    default:
                    {
                        if (ShouldLog.Evm)
                        {
                            Console.WriteLine("UNKNOWN INSTRUCTION");
                        }

                        throw new InvalidOperationException();
                    }
                }

                stopwatches[(int)instruction].Stop();


                if (ShouldLog.Evm)
                {
                    string extraInfo = instruction == Instruction.CALL || instruction == Instruction.CALLCODE
                        ? " AFTER CALL "
                        : " ";
                    Console.WriteLine(
                        $"  GAS{extraInfo}{state.GasAvailable} ({gasBefore - state.GasAvailable}) ({instruction})");
                }
            }

            totalTime.Stop();
            Console.WriteLine(totalTime.ElapsedMilliseconds);

            return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
        }

        public static BigInteger CalculateMemoryCost(BigInteger initial, BigInteger final)
        {
            if (final == initial)
            {
                return BigInteger.Zero;
            }

            return (final - initial) * GasCostOf.Memory + BigInteger.Divide(BigInteger.Pow(final, 2), 512) -
                   BigInteger.Divide(BigInteger.Pow(initial, 2), 512);
        }
    }
}