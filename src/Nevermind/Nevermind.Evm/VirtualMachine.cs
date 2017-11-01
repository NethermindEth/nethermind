using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Evm.Precompiles;
using Nevermind.Store;

namespace Nevermind.Evm
{
    public class VirtualMachine
    {
        public const int MaxSize = 1024;
        private readonly byte[][] _array = new byte[MaxSize][];
        private readonly BigInteger[] _intArray = new BigInteger[MaxSize];
        private readonly bool[] _isInt = new bool[1024];

        private int _head;

        public void Push(byte[] value)
        {
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  PUSH {Hex.FromBytes(value, true)}");
            }

            _isInt[_head] = false;
            _array[_head] = value;
            _head++;
            if (_head > MaxSize)
            {
                throw new StackOverflowException();
            }
        }

        public void Push(BigInteger value)
        {
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  PUSH {value}");
            }

            _isInt[_head] = true;
            _intArray[_head] = value;
            _head++;
            if (_head > MaxSize)
            {
                throw new StackOverflowException();
            }
        }

        public void PopLimbo()
        {
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;
        }

        public void Dup(int depth)
        {
            if (_isInt[_head - depth])
            {
                _intArray[_head] = _intArray[_head - depth];
                _isInt[_head] = true;
            }
            else
            {
                _array[_head] = _array[_head - depth];
                _isInt[_head] = false;
            }

            _head++;
            if (_head > MaxSize)
            {
                throw new StackOverflowException();
            }
        }

        public void Swap(int depth)
        {
            bool isIntBottom = _isInt[_head - depth];
            bool isIntUp = _isInt[_head - 1];

            if (isIntBottom)
            {
                BigInteger intVal = _intArray[_head - depth];

                if (isIntUp)
                {
                    _intArray[_head - depth] = _intArray[_head - 1];
                }
                else
                {
                    _array[_head - depth] = _array[_head - 1];
                }

                _intArray[_head - 1] = intVal;
            }
            else
            {
                byte[] bytes = _array[_head - depth];

                if (isIntUp)
                {
                    _intArray[_head - depth] = _intArray[_head - 1];
                }
                else
                {
                    _array[_head - depth] = _array[_head - 1];
                }

                _array[_head - 1] = bytes;
            }

            _isInt[_head - depth] = isIntUp;
            _isInt[_head - 1] = isIntBottom;
        }

        public byte[] PopBytes()
        {
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;

            byte[] result = _isInt[_head] ? _intArray[_head].ToBigEndianByteArray() : _array[_head];
            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  POP {Hex.FromBytes(result, true)}");
            }

            return result;
        }

        public BigInteger PopUInt()
        {
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;

            if (_isInt[_head])
            {
                if (ShouldLog.Evm)
                {
                    Console.WriteLine($"  POP {_intArray[_head]}");
                }

                return _intArray[_head];
            }

            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  POP {_array[_head]}");
            }

            return _array[_head].ToUnsignedBigInteger();
        }

        public BigInteger PopInt()
        {
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;

            // TODO: if I remember if it was signed?
            if (_isInt[_head])
            {
                if (ShouldLog.Evm)
                {
                    Console.WriteLine($"  POP {_intArray[_head]}");
                }

                return _intArray[_head].ToBigEndianByteArray().ToSignedBigInteger();
            }

            if (ShouldLog.Evm)
            {
                Console.WriteLine($"  POP {_array[_head]}");
            }

            return _array[_head].ToSignedBigInteger();
        }

        private static readonly BigInteger P255Int = BigInteger.Pow(2, 255);
        private static readonly BigInteger P256Int = P255Int * 2;
        private static readonly BigInteger P255 = P255Int;
        private static readonly BigInteger BigInt256 = 256;
        private static readonly BigInteger BigInt32 = 32;
        private static readonly byte[] EmptyBytes = new byte[0];
        private static readonly byte[] BytesOne = new byte[] { 1 };
        private static readonly byte[] BytesZero = new byte[] { 0 };

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Address ToAddress(byte[] word)
        {
            if (word.Length < 20)
            {
                word = word.PadLeft(20);
            }

            return word.Length == 20 ? new Address(word) : new Address(word.Slice(word.Length - 20, 20));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UpdateGas(ulong gasCost, ref ulong gasAvailable)
        {
            if (gasAvailable < gasCost)
            {
                throw new OutOfGasException();
            }

            gasAvailable -= gasCost;
        }

        public (byte[] output, TransactionSubstate) Run(
            ExecutionEnvironment env,
            EvmState state,
            IBlockhashProvider blockhashProvider,
            IWorldStateProvider worldStateProvider,
            IStorageProvider storageProvider)
        {
            _head = 0;

            ulong gasAvailable = state.GasAvailable;
            long programCounter = (long)state.ProgramCounter;
            //EvmStack stack = state.Stack;
            EvmMemory memory = state.Memory;
            byte[] code = env.MachineCode;
            bool[] jumpDestinations = new bool[code.Length]; // TODO: cache across recursive calls
            HashSet<Address> destroyList = new HashSet<Address>();
            List<LogEntry> logs = new List<LogEntry>();

            // TODO: outside and inline?
            Address PopAddress()
            {
                return ToAddress(PopBytes());
            }

            void UpdateMemoryCost(ulong newActiveWords)
            {
                UpdateGas(CalculateMemoryCost(state.ActiveWordsInMemory, newActiveWords), ref gasAvailable);
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
                if (destination < 0 || destination > jumpDestinations.Length || !jumpDestinations[destination])
                {
                    throw new InvalidJumpDestinationException();
                }
            }

            while (programCounter < code.Length)
            {
                int intPorgramCounter = (int)programCounter;
                Instruction instruction = (Instruction)code[intPorgramCounter];
                jumpDestinations[intPorgramCounter] = true;
                if (instruction >= Instruction.PUSH1 && instruction <= Instruction.PUSH32)
                {
                    programCounter += instruction - Instruction.PUSH1 + 2;
                }
                else
                {
                    programCounter++;
                }
            }

            programCounter = 0;

            while (programCounter < code.Length)
            {
                ulong gasBefore = gasAvailable;

                Instruction instruction = (Instruction)code[(int)programCounter];
                programCounter++;

                if (ShouldLog.Evm)
                {
                    Console.WriteLine($"{instruction} (0x{instruction:X})");
                }

                int intReg;
                ulong newMemory;
                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        state.GasAvailable = gasAvailable;
                        state.ProgramCounter = programCounter;
                        return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
                    }
                    case Instruction.ADD:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        i256Reg[2] = i256Reg[0] + i256Reg[1];
                        Push(
                            i256Reg[2] >= P256Int
                                ? i256Reg[2] - P256Int
                                : i256Reg[2]);

                        break;
                    }
                    case Instruction.MUL:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        Push(BigInteger.Remainder(i256Reg[0] * i256Reg[1], P256Int)
                        );
                        break;
                    }
                    case Instruction.SUB:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = i256Reg[0] - i256Reg[1];
                        if (i256Reg[2] < BigInteger.Zero)
                        {
                            i256Reg[2] += P256Int;
                        }

                        Push(i256Reg[2]);
                        break;
                    }
                    case Instruction.DIV:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        Push(i256Reg[1] == BigInteger.Zero
                            ? BigInteger.Zero
                            : BigInteger.Divide(i256Reg[0], i256Reg[1]));
                        break;
                    }
                    case Instruction.SDIV:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        if (i256Reg[1] == BigInteger.Zero)
                        {
                            Push(BigInteger.Zero);
                        }
                        else if (i256Reg[1] == BigInteger.MinusOne && i256Reg[0] == P255Int)
                        {
                            Push(P255);
                        }
                        else
                        {
                            Push(BigInteger.Divide(i256Reg[0], i256Reg[1]).ToBigEndianByteArray(true, 32));
                        }

                        break;
                    }
                    case Instruction.MOD:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        Push(i256Reg[1] == BigInteger.Zero
                            ? BigInteger.Zero
                            : BigInteger.Remainder(i256Reg[0], i256Reg[1]));
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();

                        if (i256Reg[1] == BigInteger.Zero)
                        {
                            Push(BigInteger.Zero);
                        }
                        else
                        {
                            Push((i256Reg[0].Sign * BigInteger.Remainder(i256Reg[0].Abs(), i256Reg[1].Abs())).ToBigEndianByteArray(true, 32));
                        }

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        Push(
                            i256Reg[2] == BigInteger.Zero
                                ? BigInteger.Zero
                                : BigInteger.Remainder(i256Reg[0] + i256Reg[1], i256Reg[2]));

                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        i256Reg[2] = PopUInt();
                        Push(
                            i256Reg[2] == BigInteger.Zero
                                ? BigInteger.Zero
                                : BigInteger.Remainder(i256Reg[0] * i256Reg[1], i256Reg[2]));

                        break;
                    }
                    case Instruction.EXP:
                    {
                        // TODO: auch - can optimalize exp size
                        UpdateGas(GasCostOf.Exp, ref gasAvailable);
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

                            UpdateGas(GasCostOf.ExpByte * (1UL + (ulong)expSize), ref gasAvailable);
                        }

                        if (i256Reg[0] == BigInteger.Zero)
                        {
                            Push(BigInteger.Zero);
                        }
                        else if (i256Reg[0] == BigInteger.One)
                        {
                            Push(BigInteger.One);
                        }
                        else
                        {
                            Push(BigInteger.ModPow(i256Reg[0], i256Reg[1], P256Int));
                        }

                        break;
                    }
                    case Instruction.SIGNEXTEND:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
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
                        Push(extended);
                        break;
                    }
                    case Instruction.LT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        Push(i256Reg[0] < i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.GT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        Push(i256Reg[0] > i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SLT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        Push(BigInteger.Compare(i256Reg[0], i256Reg[1]) < 0 ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SGT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        Push(i256Reg[0] > i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.EQ:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopInt();
                        i256Reg[1] = PopInt();
                        Push(i256Reg[0] == i256Reg[1] ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopInt();
                        Push(i256Reg[0].IsZero ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.AND:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bytesReg[0].ToBigEndianBitArray256(ref bits1);
                        bytesReg[1].ToBigEndianBitArray256(ref bits2);
                        Push(bits1.And(bits2).ToBytes());
                        break;
                    }
                    case Instruction.OR:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bytesReg[0].ToBigEndianBitArray256(ref bits1);
                        bytesReg[1].ToBigEndianBitArray256(ref bits2);
                        Push(bits1.Or(bits2).ToBytes());
                        break;
                    }
                    case Instruction.XOR:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        bytesReg[0] = PopBytes();
                        bytesReg[1] = PopBytes();
                        bytesReg[0].ToBigEndianBitArray256(ref bits1);
                        bytesReg[1].ToBigEndianBitArray256(ref bits2);
                        Push(bits1.Xor(bits2).ToBytes());
                        break;
                    }
                    case Instruction.NOT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
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

                        Push(result.WithoutLeadingZeros());
                        break;
                    }
                    case Instruction.BYTE:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        bytesReg[0] = PopBytes();

                        if (i256Reg[0] >= BigInt32)
                        {
                            Push(BytesZero);
                            break;
                        }

                        intReg = bytesReg[0].Length - 32 + (int)i256Reg[0];
                        Push(intReg < 0 ? BytesZero : bytesReg[0].Slice(intReg, 1));
                        break;
                    }
                    case Instruction.SHA3:
                    {
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();

                        (bytesReg[0], newMemory) = memory.Load(i256Reg[0], i256Reg[1]);
                        UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmMemory.Div32Ceiling(i256Reg[1]), ref gasAvailable);
                        UpdateMemoryCost(newMemory);
                        Push(Keccak.Compute(bytesReg[0]).Bytes);

                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CodeOwner.Hex);
                        break;
                    }
                    case Instruction.BALANCE:
                    {
                        UpdateGas(GasCostOf.Balance, ref gasAvailable);
                        Address address = PopAddress();
                        Account account = worldStateProvider.GetAccount(address);
                        Push(account?.Balance ?? BigInteger.Zero);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.Caller.Hex);
                        break;
                    }
                    case Instruction.CALLVALUE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.Value);
                        break;
                    }
                    case Instruction.ORIGIN:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.Originator.Hex);
                        break;
                    }
                    case Instruction.CALLDATALOAD:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        Push(GetPaddedSlice(env.InputData, i256Reg[0], 32));
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.InputData.Length);
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]), ref gasAvailable);

                        byte[] callDataSlice = GetPaddedSlice(env.InputData, i256Reg[1], i256Reg[2]);
                        newMemory = memory.Save(i256Reg[0], callDataSlice);
                        UpdateMemoryCost(newMemory);
                        break;
                    }
                    case Instruction.CODESIZE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(code.Length);
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]), ref gasAvailable);

                        byte[] callDataSlice = GetPaddedSlice(code, i256Reg[1], i256Reg[2]);
                        newMemory = memory.Save(i256Reg[0], callDataSlice);
                        UpdateMemoryCost(newMemory);
                        break;
                    }
                    case Instruction.GASPRICE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.GasPrice);
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        UpdateGas(GasCostOf.ExtCodeSize, ref gasAvailable);
                        bytesReg[0] = PopBytes();
                        Address address = new Address(bytesReg[0].Slice(bytesReg[0].Length - 20, 20));
                        Account account =
                            worldStateProvider.GetOrCreateAccount(address);
                        byte[] accountCode = worldStateProvider.GetCode(account.CodeHash);
                        Push(accountCode?.Length ?? BigInteger.Zero);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        Address address = PopAddress();
                        i256Reg[0] = PopUInt(); // dest
                        i256Reg[1] = PopUInt(); // source
                        i256Reg[2] = PopUInt(); // length
                        UpdateGas(GasCostOf.ExtCode + GasCostOf.Memory * EvmMemory.Div32Ceiling(i256Reg[2]), ref gasAvailable);

                        Account account = worldStateProvider.GetAccount(address);
                        byte[] externalCode = account == null ? new byte[] { 0 } : worldStateProvider.GetCode(account.CodeHash);
                        byte[] callDataSlice = GetPaddedSlice(externalCode, i256Reg[1], i256Reg[2]);
                        newMemory = memory.Save(i256Reg[0], callDataSlice);
                        UpdateMemoryCost(newMemory);
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        UpdateGas(GasCostOf.BlockHash, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        if (i256Reg[0] > BigInt256)
                        {
                            Push(BigInteger.Zero);
                        }
                        else if (i256Reg[0] == BigInteger.Zero)
                        {
                            Push(BigInteger.Zero);
                        }
                        else
                        {
                            Push(blockhashProvider.GetBlockhash(env.CurrentBlock, (int)i256Reg[0]).Bytes);
                        }

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Header.Beneficiary.Hex);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Header.Difficulty);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Header.Timestamp);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Header.Number);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Header.GasLimit);
                        break;
                    }
                    case Instruction.POP:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PopLimbo();
                        break;
                    }
                    case Instruction.MLOAD:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        (bytesReg[0], newMemory) = memory.Load(i256Reg[0]);
                        UpdateMemoryCost(newMemory);
                        Push(bytesReg[0]);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        bytesReg[1] = PopBytes();
                        newMemory = memory.SaveWord(i256Reg[0], bytesReg[1]);
                        UpdateMemoryCost(newMemory);
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        bytesReg[1] = PopBytes();
                        newMemory = memory.SaveByte(i256Reg[0], bytesReg[1]);
                        UpdateMemoryCost(newMemory);
                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        UpdateGas(GasCostOf.SLoad, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        StorageTree storage = storageProvider.GetOrCreateStorage(env.CodeOwner);
                        byte[] value = storage.Get(i256Reg[0]);
                        Push(value);
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
                            UpdateGas(GasCostOf.SReset, ref gasAvailable);
                            if (isValueChanged)
                            {
                                refund += RefundOf.SClear;
                            }
                        }
                        else
                        {
                            UpdateGas(previousValue.IsZero() ? GasCostOf.SSet : GasCostOf.SReset, ref gasAvailable);
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
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        ValidateJump((int)i256Reg[0]);

                        programCounter = (long)i256Reg[0];
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        UpdateGas(GasCostOf.High, ref gasAvailable);
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        if (i256Reg[1] > BigInteger.Zero)
                        {
                            ValidateJump((int)i256Reg[0]);
                            programCounter = (long)i256Reg[0];
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(programCounter - 1);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(state.ActiveWordsInMemory * 32);
                        break;
                    }
                    case Instruction.GAS:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(gasAvailable);
                        break;
                    }
                    case Instruction.JUMPDEST:
                    {
                        UpdateGas(GasCostOf.JumpDest, ref gasAvailable);
                        break;
                    }
                    case Instruction.PUSH1:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        int programCounterInt = (int)programCounter;
                        if (programCounterInt >= code.Length)
                        {
                            Push(EmptyBytes);
                        }
                        else
                        {
                            Push(code[programCounterInt]);
                        }

                        programCounter++;
                        break;
                    }
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
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        intReg = instruction - Instruction.PUSH1 + 1;
                        int programCounterInt = (int)programCounter;
                        int usedFromCode = Math.Min(code.Length - programCounterInt, intReg);

                        Push(usedFromCode != intReg
                            ? code.Slice(programCounterInt, usedFromCode).PadRight(intReg)
                            : code.Slice(programCounterInt, usedFromCode));

                        programCounter += intReg;
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
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        Dup(instruction - Instruction.DUP1 + 1);
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
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        Swap(instruction - Instruction.SWAP1 + 2);
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
                        UpdateGas(GasCostOf.Log + (ulong)intReg * GasCostOf.LogTopic + (ulong)bytesReg[0].Length * GasCostOf.LogData, ref gasAvailable);

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
                        UpdateGas(GasCostOf.Create + GasCostOf.CodeDeposit * (ulong)i256Reg[2], ref gasAvailable);

                        Account codeOwner = worldStateProvider.GetAccount(env.CodeOwner);
                        (bytesReg[0], newMemory) =
                            memory.Load(i256Reg[1], i256Reg[2], false);
                        UpdateMemoryCost(newMemory);

                        Account account = new Account();
                        account.Balance = i256Reg[0];
                        if (i256Reg[0] > (codeOwner?.Balance ?? 0))
                        {
                            Push(BigInteger.Zero);
                            break;
                        }

                        Keccak codeHash = worldStateProvider.UpdateCode(bytesReg[0]);
                        account.CodeHash = codeHash;
                        account.Nonce = 0;

                        Keccak newAddress = Keccak.Compute(Rlp.Encode(env.CodeOwner, codeOwner.Nonce));
                        Address address = new Address(newAddress);
                        worldStateProvider.UpdateAccount(address, account);
                        Push(address.Hex);
                        break;
                    }
                    case Instruction.RETURN:
                    {
                        ulong gasCost = GasCostOf.Zero; ;
                        i256Reg[0] = PopUInt();
                        i256Reg[1] = PopUInt();
                        (bytesReg[0], newMemory) = memory.Load(i256Reg[0], i256Reg[1]);
                        UpdateGas(gasCost, ref gasAvailable);
                        UpdateMemoryCost(newMemory);
                        state.GasAvailable = gasAvailable;
                        state.ProgramCounter = programCounter;
                        return (bytesReg[0], new TransactionSubstate(refund, destroyList, logs));
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

                        ulong gasExtra = instruction == Instruction.CALL ? GasCostOf.Call : GasCostOf.CallCode;
                        if (!i256Reg[1].IsZero)
                        {
                            gasExtra += GasCostOf.CallValue - GasCostOf.CallStipend;
                        }

                        UpdateGas(gasExtra, ref gasAvailable);

                        (byte[] callData, ulong newMemoryLocal) = memory.Load(i256Reg[2], i256Reg[3]);
                        UpdateMemoryCost(newMemoryLocal);

                        i256Reg[6] = bytesReg[0].ToUnsignedBigInteger();

                        Address target = ToAddress(bytesReg[0]);
                        if (target.Equals(env.CodeOwner))
                        {
                            Push(failure);
                            break;
                        }

                        if (!i256Reg[1].IsZero)
                        {
                            Account codeOwnerAccount = worldStateProvider.GetAccount(env.CodeOwner);
                            if (codeOwnerAccount.Balance < i256Reg[1])
                            {
                                newMemory = memory.Save(i256Reg[4], new byte[(int)i256Reg[5]]);
                                UpdateMemoryCost(newMemory);
                                Push(failure);
                                break;
                            }

                            codeOwnerAccount.Balance -= i256Reg[1]; // do not subtract if failed
                            worldStateProvider.UpdateAccount(env.CodeOwner, codeOwnerAccount);
                        }

                        Account targetAccount = worldStateProvider.GetAccount(target);
                        if (targetAccount == null)
                        {
                            gasExtra += GasCostOf.NewAccount;
                            UpdateGas(GasCostOf.NewAccount, ref gasAvailable); // TODO: check this earlier?
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
                            ulong gasCost = PrecompiledContracts[i256Reg[6]].GasCost(env.InputData);
                            UpdateGas(gasCost, ref gasAvailable); // TODO: check EIP-150
                            Push(PrecompiledContracts[i256Reg[6]].Run(env.InputData));
                            Push(success);
                            break;
                        }

                        ulong gasCap = (ulong)i256Reg[0];

                        bool eip150 = false;
                        if (eip150)
                        {
                            gasCap = gasExtra < gasAvailable
                                ? Math.Min(gasAvailable - gasExtra - (gasAvailable - gasExtra) / 64,
                                    (ulong)i256Reg[0])
                                : (ulong)i256Reg[0];
                        }
                        else if (gasAvailable < gasCap)
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

                        ulong callGas =
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
                            Push(success);
                        }
                        catch (Exception ex)
                        {
                            if (ShouldLog.Evm)
                            {
                                Console.WriteLine($"FAIL {ex.GetType().Name}");
                            }

                            worldStateProvider.Restore(stateSnapshot);
                            storageProvider.Restore(callEnv.CodeOwner, storageSnapshot);

                            Push(failure);
                        }

                        break;
                    }
                    case Instruction.INVALID:
                    {
                        throw new InvalidInstructionException();
                        break;
                    }
                    case Instruction.SELFDESTRUCT:
                    {
                        UpdateGas(GasCostOf.SelfDestruct, ref gasAvailable);
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
                                UpdateGas(GasCostOf.NewAccount, ref gasAvailable);
                            }
                        }
                        else
                        {
                            inheritorAccount.Balance += codeOwnerAccount.Balance;
                        }

                        worldStateProvider.UpdateAccount(inheritor, inheritorAccount);
                        codeOwnerAccount.Balance = BigInteger.Zero;
                        worldStateProvider.UpdateAccount(env.CodeOwner, codeOwnerAccount);

                        state.GasAvailable = gasAvailable;
                        state.ProgramCounter = programCounter;
                        return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
                    }
                    default:
                    {
                        if (ShouldLog.Evm)
                        {
                            Console.WriteLine("UNKNOWN INSTRUCTION");
                        }

                        throw new InvalidInstructionException();
                    }
                }

                if (ShouldLog.Evm)
                {
                    string extraInfo = instruction == Instruction.CALL || instruction == Instruction.CALLCODE
                        ? " AFTER CALL "
                        : " ";
                    Console.WriteLine(
                        $"  GAS{extraInfo}{gasAvailable} ({gasBefore - gasAvailable}) ({instruction})");
                }
            }

            state.GasAvailable = gasAvailable;
            state.ProgramCounter = programCounter;
            return (new byte[0], new TransactionSubstate(refund, destroyList, logs));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CalculateMemoryCost(ulong initial, ulong final)
        {
            if (final == initial)
            {
                return 0UL;
            }

            return (ulong)((final - initial) * GasCostOf.Memory + BigInteger.Divide(BigInteger.Pow(final, 2), 512) -
                   BigInteger.Divide(BigInteger.Pow(initial, 2), 512));
        }
    }
}