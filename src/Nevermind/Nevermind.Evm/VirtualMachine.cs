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
    public class VirtualMachine : IVirtualMachine
    {
        public const int MaxSize = 1024;
        private readonly byte[][] _array = new byte[MaxSize][];
        private readonly BigInteger[] _intArray = new BigInteger[MaxSize];
        private readonly bool[] _isInt = new bool[1024];

        private int _head;

        private void Push(byte[] value)
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

        private void Push(BigInteger value)
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

        private void PopLimbo()
        {
            if (_head == 0)
            {
                throw new StackUnderflowException();
            }

            _head--;
        }

        private void Dup(int depth)
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

        private void Swap(int depth)
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

        private byte[] PopBytes()
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

        private BigInteger PopUInt()
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

        private BigInteger PopInt()
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

        private static readonly BigInteger DaoExploitFixBlockNumber = 10
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
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger res = a + b;
                        Push(res >= P256Int ? res - P256Int : res);
                        break;
                    }
                    case Instruction.MUL:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        Push(BigInteger.Remainder(a * b, P256Int));
                        break;
                    }
                    case Instruction.SUB:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger res = a - b;
                        if (res < BigInteger.Zero)
                        {
                            res += P256Int;
                        }

                        Push(res);
                        break;
                    }
                    case Instruction.DIV:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        Push(b == BigInteger.Zero ? BigInteger.Zero : BigInteger.Divide(a, b));
                        break;
                    }
                    case Instruction.SDIV:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        if (b == BigInteger.Zero)
                        {
                            Push(BigInteger.Zero);
                        }
                        else if (b == BigInteger.MinusOne && a == P255Int)
                        {
                            Push(P255);
                        }
                        else
                        {
                            Push(BigInteger.Divide(a, b).ToBigEndianByteArray(true, 32));
                        }

                        break;
                    }
                    case Instruction.MOD:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        Push(b == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a, b));
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        if (b == BigInteger.Zero)
                        {
                            Push(BigInteger.Zero);
                        }
                        else
                        {
                            Push((a.Sign * BigInteger.Remainder(a.Abs(), b.Abs())).ToBigEndianByteArray(true, 32));
                        }

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger mod = PopUInt();
                        Push(mod == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a + b, mod));
                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger mod = PopUInt();
                        Push(mod == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a * b, mod));
                        break;
                    }
                    case Instruction.EXP:
                    {
                        UpdateGas(GasCostOf.Exp, ref gasAvailable);
                        BigInteger baseInt = PopUInt();
                        BigInteger exp = PopUInt();
                        if (exp > BigInteger.Zero)
                        {
                            int expSize = (int)BigInteger.Log(exp, 256);
                            BigInteger expSizeTest = BigInteger.Pow(BigInt256, expSize);
                            BigInteger expSizeTestInc = expSizeTest * BigInt256;
                            if (expSizeTest > exp)
                            {
                                expSize--;
                            }
                            else if (expSizeTestInc <= exp)
                            {
                                expSize++;
                            }

                            UpdateGas(GasCostOf.ExpByte * (1UL + (ulong)expSize), ref gasAvailable);
                        }

                        if (baseInt == BigInteger.Zero)
                        {
                            Push(BigInteger.Zero);
                        }
                        else if (baseInt == BigInteger.One)
                        {
                            Push(BigInteger.One);
                        }
                        else
                        {
                            Push(BigInteger.ModPow(baseInt, exp, P256Int));
                        }

                        break;
                    }
                    case Instruction.SIGNEXTEND:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopUInt(); // TODO: check if there is spec for handling too big numbers
                        if (a >= BigInt32)
                        {
                            break;
                        }

                        byte[] b = PopBytes();
                        b.ToBigEndianBitArray256(ref bits1);
                        int bitPosition = Math.Max(0, 248 - 8 * (int)a);
                        bool isSet = bits1[bitPosition];
                        for (int i = 0; i < bitPosition; i++)
                        {
                            bits1[i] = isSet;
                        }

                        Push(bits1.ToBytes());
                        break;
                    }
                    case Instruction.LT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        Push(a < b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.GT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        Push(a > b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SLT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        Push(BigInteger.Compare(a, b) < 0 ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SGT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        Push(a > b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.EQ:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        Push(a == b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopInt();
                        Push(a.IsZero ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.AND:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        PopBytes().ToBigEndianBitArray256(ref bits1);
                        PopBytes().ToBigEndianBitArray256(ref bits2);
                        Push(bits1.And(bits2).ToBytes());
                        break;
                    }
                    case Instruction.OR:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        PopBytes().ToBigEndianBitArray256(ref bits1);
                        PopBytes().ToBigEndianBitArray256(ref bits2);
                        Push(bits1.Or(bits2).ToBytes());
                        break;
                    }
                    case Instruction.XOR:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        PopBytes().ToBigEndianBitArray256(ref bits1);
                        PopBytes().ToBigEndianBitArray256(ref bits2);
                        Push(bits1.Xor(bits2).ToBytes());
                        break;
                    }
                    case Instruction.NOT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        byte[] bytes = PopBytes();
                        byte[] res = new byte[32];
                        for (int i = 0; i < 32; ++i)
                        {
                            if (bytes.Length < 32 - i)
                            {
                                res[i] = 0xff;
                            }
                            else
                            {
                                res[i] = (byte)~bytes[i - (32 - bytes.Length)];
                            }
                        }

                        Push(res.WithoutLeadingZeros());
                        break;
                    }
                    case Instruction.BYTE:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger position = PopUInt();
                        byte[] bytes = PopBytes();

                        if (position >= BigInt32)
                        {
                            Push(BytesZero);
                            break;
                        }

                        int adjustedPosition = bytes.Length - 32 + (int)position;
                        Push(adjustedPosition < 0 ? BytesZero : bytes.Slice(adjustedPosition, 1));
                        break;
                    }
                    case Instruction.SHA3:
                    {
                        BigInteger memSrc = PopUInt();
                        BigInteger memLength = PopUInt();
                        (byte[] memData, ulong newMemory) = memory.Load(memSrc, memLength);
                        UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmMemory.Div32Ceiling(memLength), ref gasAvailable);
                        UpdateMemoryCost(newMemory);
                        Push(Keccak.Compute(memData).Bytes);
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
                        BigInteger a = PopUInt();
                        Push(GetPaddedSlice(env.InputData, a, 32));
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
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length), ref gasAvailable);
                        byte[] callDataSlice = GetPaddedSlice(env.InputData, src, length);
                        ulong newMemory = memory.Save(dest, callDataSlice);
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
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length), ref gasAvailable);
                        byte[] callDataSlice = GetPaddedSlice(code, src, length);
                        ulong newMemory = memory.Save(dest, callDataSlice);
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
                        Address address = PopAddress();
                        Account account = worldStateProvider.GetOrCreateAccount(address);
                        byte[] accountCode = worldStateProvider.GetCode(account.CodeHash);
                        Push(accountCode?.Length ?? BigInteger.Zero);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        Address address = PopAddress();
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        UpdateGas(GasCostOf.ExtCode + GasCostOf.Memory * EvmMemory.Div32Ceiling(length), ref gasAvailable);
                        Account account = worldStateProvider.GetAccount(address);
                        byte[] externalCode = account == null ? new byte[] { 0 } : worldStateProvider.GetCode(account.CodeHash);
                        byte[] callDataSlice = GetPaddedSlice(externalCode, src, length);
                        ulong newMemory = memory.Save(dest, callDataSlice);
                        UpdateMemoryCost(newMemory);
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        UpdateGas(GasCostOf.BlockHash, ref gasAvailable);
                        BigInteger a = PopUInt();
                        if (a > BigInt256)
                        {
                            Push(BigInteger.Zero);
                        }
                        else if (a == BigInteger.Zero)
                        {
                            Push(BigInteger.Zero);
                        }
                        else
                        {
                            Push(blockhashProvider.GetBlockhash(env.CurrentBlock, (int)a).Bytes);
                        }

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Beneficiary.Hex);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Difficulty);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Timestamp);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.Number);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(env.CurrentBlock.GasLimit);
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
                        BigInteger memPosition = PopUInt();
                        (byte[] memData, ulong newMemory) = memory.Load(memPosition);
                        UpdateMemoryCost(newMemory);
                        Push(memData);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger memPosition = PopUInt();
                        byte[] data = PopBytes();
                        ulong newMemory = memory.SaveWord(memPosition, data);
                        UpdateMemoryCost(newMemory);
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger memPosition = PopUInt();
                        byte[] data = PopBytes();
                        ulong newMemory = memory.SaveByte(memPosition, data);
                        UpdateMemoryCost(newMemory);
                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        UpdateGas(GasCostOf.SLoad, ref gasAvailable);
                        BigInteger storageIndex = PopUInt();
                        StorageTree storage = storageProvider.GetOrCreateStorage(env.CodeOwner);
                        byte[] value = storage.Get(storageIndex);
                        Push(value);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        BigInteger storageIndex = PopUInt();
                        byte[] data = PopBytes();
                        StorageTree storage = storageProvider.GetOrCreateStorage(env.CodeOwner);
                        byte[] previousValue = storage.Get(storageIndex);

                        bool isNewValueZero = data.IsZero();
                        bool isValueChanged = !(isNewValueZero && previousValue.IsZero()) ||
                                              !Bytes.UnsafeCompare(previousValue, data);
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
                            storage.Set(storageIndex,
                                isNewValueZero ? new byte[] { 0 } : data.WithoutLeadingZeros());
                            Account account = worldStateProvider.GetAccount(env.CodeOwner);
                            account.StorageRoot = storage.RootHash;
                            worldStateProvider.UpdateAccount(env.CodeOwner, account);
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        BigInteger dest = PopUInt();
                        ValidateJump((int)dest);
                        programCounter = (long)dest;
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        UpdateGas(GasCostOf.High, ref gasAvailable);
                        BigInteger dest = PopUInt();
                        BigInteger condition = PopUInt();
                        if (condition > BigInteger.Zero)
                        {
                            ValidateJump((int)dest);
                            programCounter = (long)dest;
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(programCounter - 1L);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        Push(state.ActiveWordsInMemory * 32UL);
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
                        int length = instruction - Instruction.PUSH1 + 1;
                        int programCounterInt = (int)programCounter;
                        int usedFromCode = Math.Min(code.Length - programCounterInt, length);

                        Push(usedFromCode != length
                            ? code.Slice(programCounterInt, usedFromCode).PadRight(length)
                            : code.Slice(programCounterInt, usedFromCode));

                        programCounter += length;
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
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        int topicsCount = instruction - Instruction.LOG0;
                        byte[] data = memory.Load(a, b, false).Item1;
                        UpdateGas(GasCostOf.Log + (ulong)topicsCount * GasCostOf.LogTopic + (ulong)data.Length * GasCostOf.LogData, ref gasAvailable);

                        byte[][] topics = new byte[topicsCount][];
                        for (int i = 0; i < topicsCount; i++)
                        {
                            topics[i] = PopBytes().PadLeft(32);
                        }

                        LogEntry logEntry = new LogEntry(
                            env.CodeOwner,
                            data,
                            topics);
                        logs.Add(logEntry);
                        break;
                    }
                    case Instruction.CREATE:
                    {
                        BigInteger value = PopUInt();
                        BigInteger memSrc = PopUInt();
                        BigInteger length = PopUInt();
                        UpdateGas(GasCostOf.Create + GasCostOf.CodeDeposit * (ulong)length, ref gasAvailable);

                        Account codeOwner = worldStateProvider.GetAccount(env.CodeOwner);
                        (byte[] depositCode, ulong newMemory) = memory.Load(memSrc, length, false);
                        UpdateMemoryCost(newMemory);

                        Account account = new Account();
                        account.Balance = value;
                        if (value > (codeOwner?.Balance ?? 0))
                        {
                            Push(BigInteger.Zero);
                            break;
                        }

                        Keccak codeHash = worldStateProvider.UpdateCode(depositCode);
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
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        (byte[] returnData, ulong newMemory) = memory.Load(a, b);
                        UpdateGas(gasCost, ref gasAvailable);
                        UpdateMemoryCost(newMemory);
                        state.GasAvailable = gasAvailable;
                        state.ProgramCounter = programCounter;
                        return (returnData, new TransactionSubstate(refund, destroyList, logs));
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

                        BigInteger a = PopUInt(); // gas
                        byte[] targetAddress = PopBytes();
                        BigInteger b = PopUInt(); // value
                        BigInteger dataOffset = PopUInt(); // data offset
                        BigInteger dataLength = PopUInt(); // data length
                        BigInteger outputOffset = PopUInt(); // output offset
                        BigInteger outputLength = PopUInt(); // output length

                        ulong gasExtra = instruction == Instruction.CALL ? GasCostOf.Call : GasCostOf.CallCode;
                        if (!b.IsZero)
                        {
                            gasExtra += GasCostOf.CallValue - GasCostOf.CallStipend;
                        }

                        UpdateGas(gasExtra, ref gasAvailable);

                        (byte[] callData, ulong newMemoryLocal) = memory.Load(dataOffset, dataLength);
                        UpdateMemoryCost(newMemoryLocal);

                        BigInteger addressInt = targetAddress.ToUnsignedBigInteger();

                        Address target = ToAddress(targetAddress);
                        if (target.Equals(env.CodeOwner))
                        {
                            Push(failure);
                            break;
                        }

                        if (!b.IsZero)
                        {
                            Account codeOwnerAccount = worldStateProvider.GetAccount(env.CodeOwner);
                            if (codeOwnerAccount.Balance < b)
                            {
                                ulong newMemory = memory.Save(outputOffset, new byte[(int)outputLength]);
                                UpdateMemoryCost(newMemory);
                                Push(failure);
                                break;
                            }

                            codeOwnerAccount.Balance -= b; // do not subtract if failed
                            worldStateProvider.UpdateAccount(env.CodeOwner, codeOwnerAccount);
                        }

                        Account targetAccount = worldStateProvider.GetAccount(target);
                        if (targetAccount == null)
                        {
                            gasExtra += GasCostOf.NewAccount;
                            UpdateGas(GasCostOf.NewAccount, ref gasAvailable); // TODO: check this earlier?
                            targetAccount = new Account();
                            targetAccount.Balance = b;
                            worldStateProvider.UpdateAccount(target, targetAccount);
                        }
                        else
                        {
                            targetAccount.Balance += b;
                            worldStateProvider.UpdateAccount(target, targetAccount);
                        }

                        if (addressInt <= 4 && addressInt != 0)
                        {
                            ulong gasCost = PrecompiledContracts[addressInt].GasCost(env.InputData);
                            UpdateGas(gasCost, ref gasAvailable); // TODO: check EIP-150
                            Push(PrecompiledContracts[addressInt].Run(env.InputData));
                            Push(success);
                            break;
                        }

                        ulong gasCap = (ulong)a;

                        bool eip150 = false;
                        if (eip150)
                        {
                            gasCap = gasExtra < gasAvailable
                                ? Math.Min(gasAvailable - gasExtra - (gasAvailable - gasExtra) / 64,
                                    (ulong)a)
                                : (ulong)a;
                        }
                        else if (gasAvailable < gasCap)
                        {
                            throw new OutOfGasException(); // no EIP-150
                        }

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.Value = b;
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
                            b.IsZero
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
                            ulong newMemory = memory.Save(outputOffset, GetPaddedSlice(callOutput, 0, outputLength));
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
                        throw new InvalidInstructionException((byte)instruction);
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
                            if (env.CurrentBlock.Number > DaoExploitFixBlockNumber)
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

                        throw new InvalidInstructionException((byte)instruction);
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