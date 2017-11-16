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
        public const int MaxCallDepth = 1024;
        public const int MaxStackSize = 1025;

        private static readonly BigInteger P255Int = BigInteger.Pow(2, 255);
        private static readonly BigInteger P256Int = P255Int * 2;
        private static readonly BigInteger P255 = P255Int;
        private static readonly BigInteger BigInt256 = 256;
        private static readonly BigInteger BigInt32 = 32;
        private static readonly byte[] EmptyBytes = new byte[0];
        private static readonly byte[] BytesOne = { 1 };
        private static readonly byte[] BytesZero = { 0 };
        private static BitArray _bits1 = new BitArray(256);
        private static BitArray _bits2 = new BitArray(256);

        private static readonly Dictionary<BigInteger, IPrecompiledContract> PrecompiledContracts;
        private readonly IBlockhashProvider _blockhashProvider;
        private readonly ILogger _logger;
        private readonly IProtocolSpecification _protocolSpecification;

        private readonly Stack<EvmState> _stateStack = new Stack<EvmState>();
        private readonly IStorageProvider _storageProvider;
        private readonly IWorldStateProvider _worldStateProvider;

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

        public VirtualMachine(
            IBlockhashProvider blockhashProvider,
            IWorldStateProvider worldStateProvider,
            IStorageProvider storageProvider,
            IProtocolSpecification protocolSpecification,
            ILogger logger)
        {
            _blockhashProvider = blockhashProvider;
            _worldStateProvider = worldStateProvider;
            _storageProvider = storageProvider;
            _protocolSpecification = protocolSpecification;
            _logger = logger;
        }

        public (byte[] output, TransactionSubstate) Run(EvmState state)
        {
            EvmState currentState = state;
            byte[] previousCallResult = null;
            byte[] previousCallOutput = EmptyBytes;
            BigInteger previousCallOutputDestination = BigInteger.Zero;
            while (true)
            {
                try
                {
                    _logger?.Log($"BEGIN {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} (at {currentState.Env.CodeOwner})");

                    CallResult callResult;
                    if (currentState.ExecutionType == ExecutionType.Precompile)
                    {
                        callResult = ExecutePrecompile(currentState);
                    }
                    else
                    {
                        callResult = ExecuteCall(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination);
                        if (!callResult.IsReturn)
                        {
                            _stateStack.Push(currentState);
                            currentState = callResult.StateToExecute;
                            continue;
                        }
                    }

                    if (currentState.ExecutionType == ExecutionType.Transaction)
                    {
                        return (callResult.Output, new TransactionSubstate(currentState.Refund, currentState.DestroyList, currentState.Logs));
                    }

                    Address callCodeOwner = currentState.Env.CodeOwner;

                    EvmState previousState = currentState;
                    currentState = _stateStack.Pop();
                    currentState.GasAvailable += previousState.GasAvailable;
                    currentState.Refund += (ulong)previousState.Refund;

                    foreach (Address address in previousState.DestroyList)
                    {
                        currentState.DestroyList.Add(address);
                    }

                    foreach (LogEntry logEntry in previousState.Logs)
                    {
                        currentState.Logs.Add(logEntry);
                    }

                    if (previousState.ExecutionType == ExecutionType.Create)
                    {
                        previousCallResult = callCodeOwner.Hex;
                        previousCallOutput = EmptyBytes;
                        previousCallOutputDestination = BigInteger.Zero;

                        ulong codeDepositGasCost = GasCostOf.CodeDeposit * (ulong)callResult.Output.Length;
                        if (_protocolSpecification.IsEip2Enabled || currentState.GasAvailable > codeDepositGasCost)
                        {
                            Keccak codeHash = _worldStateProvider.UpdateCode(callResult.Output);
                            _worldStateProvider.UpdateCodeHash(callCodeOwner, codeHash);

                            currentState.GasAvailable -= codeDepositGasCost;
                        }
                    }
                    else
                    {
                        previousCallResult = BytesOne;
                        previousCallOutput = GetPaddedSlice(callResult.Output, BigInteger.Zero, BigInteger.Min(callResult.Output.Length, previousState.OutputLength));
                        previousCallOutputDestination = previousState.OutputDestination;
                    }

                    _logger?.Log($"END {previousState.ExecutionType} AT DEPTH {previousState.Env.CallDepth} (RESULT {Hex.FromBytes(previousCallResult ?? EmptyBytes, true)}) RETURNS ({previousCallOutputDestination} : {Hex.FromBytes(previousCallOutput, true)})");
                }
                catch (Exception ex) // TODO: catch EVM exceptions only
                {
                    _logger?.Log($"EXCEPTION ({ex.GetType().Name}) IN {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} - RESTORING SNAPSHOT");

                    if (currentState.StateSnapshot != -1) // TODO: temp check - handle transaction processor calls here as well or change up
                    {
                        _worldStateProvider.Restore(currentState.StateSnapshot);
                        _storageProvider.Restore(currentState.StorageSnapshot);
                    }

                    if (currentState.ExecutionType == ExecutionType.Transaction)
                    {
                        throw;
                    }

                    previousCallResult = BytesZero;
                    previousCallOutput = EmptyBytes;
                    previousCallOutputDestination = BigInteger.Zero;

                    bool removeStipend = currentState.ExecutionType == ExecutionType.Precompile && currentState.Env.Value > 0;
                    currentState = _stateStack.Pop();
                    if (removeStipend)
                    {
                        if (currentState.GasAvailable < GasCostOf.CallStipend)
                        {
                            throw new OutOfGasException();
                        }

                        currentState.GasAvailable -= GasCostOf.CallStipend;
                    }
                }
            }
        }

        private static byte[] GetPaddedSlice(byte[] data, BigInteger position, BigInteger length)
        {
            BigInteger bytesFromInput = BigInteger.Max(0, BigInteger.Min(data.Length - position, length));
            if (position > data.Length)
            {
                return new byte[(int)length];
            }

            return data.Slice((int)position, (int)bytesFromInput).PadRight((int)length);
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RefundGas(ulong refund, ref ulong gasAvailable)
        {
            gasAvailable += refund;
        }

        private CallResult ExecutePrecompile(EvmState state)
        {
            byte[] callData = state.Env.InputData;
            BigInteger value = state.Env.Value;
            ulong gasAvailable = state.GasAvailable;

            Address precompileAddress = state.Env.CodeOwner;
            BigInteger addressInt = precompileAddress.Hex.ToUnsignedBigInteger();
            ulong baseGasCost = PrecompiledContracts[addressInt].BaseGasCost();
            ulong dataGasCost = PrecompiledContracts[addressInt].DataGasCost(callData);
            if (gasAvailable < dataGasCost + baseGasCost)
            {
                throw new OutOfGasException();
            }

            // TODO: confirm this comes only after the gas checks which requires it to be handled slightly different than inside the call
            if (!_worldStateProvider.AccountExists(state.Env.Caller))
            {
                _worldStateProvider.CreateAccount(state.Env.Caller, value);
            }
            else
            {
                _worldStateProvider.UpdateBalance(state.Env.Caller, value);
            }

            UpdateGas(baseGasCost, ref gasAvailable);
            UpdateGas(dataGasCost, ref gasAvailable);
            state.GasAvailable = gasAvailable;

            try
            {
                byte[] output = PrecompiledContracts[addressInt].Run(callData);
                return new CallResult(output);
            }
            catch (Exception)
            {
                return new CallResult(EmptyBytes);
            }
        }

        private CallResult ExecuteCall(EvmState state, byte[] previousCallResult, byte[] previousCallOutput, BigInteger previousCallOutputDestination)
        {
            ExecutionEnvironment env;
            byte[][] bytesOnStack;
            BigInteger[] intsOnStack;
            bool[] intPositions;
            int stackHead;
            ulong gasAvailable;
            long programCounter;
            byte[] code;
            bool[] jumpDestinations;

            ApplyState();

            void ApplyState()
            {
                env = state.Env;
                bytesOnStack = state.BytesOnStack;
                intsOnStack = state.IntsOnStack;
                intPositions = state.IntPositions;
                stackHead = state.StackHead;
                gasAvailable = state.GasAvailable;
                programCounter = (long)state.ProgramCounter;
                code = env.MachineCode;
                jumpDestinations = new bool[code.Length];
                CalculateJumpDestinations();
            }

            void UpdateCurrentState()
            {
                state.ProgramCounter = programCounter;
                state.GasAvailable = gasAvailable;
                state.StackHead = stackHead;
            }

            void LogInstructionResult(Instruction instruction, ulong gasBefore)
            {
                _logger?.Log(
                    $"  END {env.CallDepth}_{instruction} GAS {gasAvailable} ({gasBefore - gasAvailable}) STACK {stackHead} MEMORY {state.ActiveWordsInMemory} PC {programCounter}");
            }

            void PushBytes(byte[] value)
            {
                _logger?.Log($"  PUSH {Hex.FromBytes(value, true)}");

                intPositions[stackHead] = false;
                bytesOnStack[stackHead] = value;
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    throw new EvmStackOverflowException();
                }
            }

            void PushInt(BigInteger value)
            {
                _logger?.Log($"  PUSH {value}");

                intPositions[stackHead] = true;
                intsOnStack[stackHead] = value;
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    throw new EvmStackOverflowException();
                }
            }

            void PopLimbo()
            {
                if (stackHead == 0)
                {
                    throw new StackUnderflowException();
                }

                stackHead--;
            }

            void Dup(int depth)
            {
                if (stackHead < depth)
                {
                    throw new StackUnderflowException();
                }

                if (intPositions[stackHead - depth])
                {
                    intsOnStack[stackHead] = intsOnStack[stackHead - depth];
                    intPositions[stackHead] = true;
                }
                else
                {
                    bytesOnStack[stackHead] = bytesOnStack[stackHead - depth];
                    intPositions[stackHead] = false;
                }

                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    throw new EvmStackOverflowException();
                }
            }

            void Swap(int depth)
            {
                if (stackHead < depth)
                {
                    throw new StackUnderflowException();
                }

                bool isIntBottom = intPositions[stackHead - depth];
                bool isIntUp = intPositions[stackHead - 1];

                if (isIntBottom)
                {
                    BigInteger intVal = intsOnStack[stackHead - depth];

                    if (isIntUp)
                    {
                        intsOnStack[stackHead - depth] = intsOnStack[stackHead - 1];
                    }
                    else
                    {
                        bytesOnStack[stackHead - depth] = bytesOnStack[stackHead - 1];
                    }

                    intsOnStack[stackHead - 1] = intVal;
                }
                else
                {
                    byte[] bytes = bytesOnStack[stackHead - depth];

                    if (isIntUp)
                    {
                        intsOnStack[stackHead - depth] = intsOnStack[stackHead - 1];
                    }
                    else
                    {
                        bytesOnStack[stackHead - depth] = bytesOnStack[stackHead - 1];
                    }

                    bytesOnStack[stackHead - 1] = bytes;
                }

                intPositions[stackHead - depth] = isIntUp;
                intPositions[stackHead - 1] = isIntBottom;
            }

            byte[] PopBytes()
            {
                if (stackHead == 0)
                {
                    throw new StackUnderflowException();
                }

                stackHead--;

                byte[] result = intPositions[stackHead]
                    ? intsOnStack[stackHead].ToBigEndianByteArray()
                    : bytesOnStack[stackHead];
                _logger?.Log($"  POP {Hex.FromBytes(result, true)}");

                return result;
            }

            BigInteger PopUInt()
            {
                if (stackHead == 0)
                {
                    throw new StackUnderflowException();
                }

                stackHead--;

                if (intPositions[stackHead])
                {
                    _logger?.Log($"  POP {intsOnStack[stackHead]}");

                    return intsOnStack[stackHead];
                }

                BigInteger res = bytesOnStack[stackHead].ToUnsignedBigInteger();
                _logger?.Log($"  POP {res}");

                return res;
            }

            BigInteger PopInt()
            {
                if (stackHead == 0)
                {
                    throw new StackUnderflowException();
                }

                stackHead--;

                // TODO: can remember whether integer was signed or not so I do not have to convert
                if (intPositions[stackHead])
                {
                    _logger?.Log($"  POP {intsOnStack[stackHead]}");

                    return intsOnStack[stackHead].ToBigEndianByteArray().ToSignedBigInteger();
                }

                _logger?.Log($"  POP {bytesOnStack[stackHead]}");

                return bytesOnStack[stackHead].ToSignedBigInteger();
            }

            // TODO: outside and inline?
            Address PopAddress()
            {
                return ToAddress(PopBytes());
            }

            void UpdateMemoryCost(BigInteger position, BigInteger length)
            {
                ulong newMemory = CalculateMemoryRequirements(state.ActiveWordsInMemory, position, length);
                ulong newMemoryCost = CalculateMemoryCost(state.ActiveWordsInMemory, newMemory);
                UpdateGas(newMemoryCost, ref gasAvailable);
                state.ActiveWordsInMemory = newMemory;
            }

            void ValidateJump(int destination)
            {
                if (destination < 0 || destination > jumpDestinations.Length || !jumpDestinations[destination])
                {
                    throw new InvalidJumpDestinationException();
                }
            }

            void CalculateJumpDestinations()
            {
                int index = 0;
                while (index < code.Length)
                {
                    Instruction instruction = (Instruction)code[index];
                    jumpDestinations[index] = true;
                    if (instruction >= Instruction.PUSH1 && instruction <= Instruction.PUSH32)
                    {
                        index += instruction - Instruction.PUSH1 + 2;
                    }
                    else
                    {
                        index++;
                    }
                }
            }

            if (previousCallResult != null)
            {
                PushBytes(previousCallResult);
            }

            if (previousCallOutput.Length > 0)
            {
                UpdateMemoryCost(previousCallOutputDestination, previousCallOutput.Length);
                state.Memory.Save(previousCallOutputDestination, previousCallOutput);
            }

            while (programCounter < code.Length)
            {
                ulong gasBefore = gasAvailable;

                Instruction instruction = (Instruction)code[(int)programCounter];
                programCounter++;

                _logger?.Log($"{instruction} (0x{instruction:X})");

                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        UpdateCurrentState();
                        return CallResult.Empty;
                    }
                    case Instruction.ADD:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger res = a + b;
                        PushInt(res >= P256Int ? res - P256Int : res);
                        break;
                    }
                    case Instruction.MUL:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(BigInteger.Remainder(a * b, P256Int));
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

                        PushInt(res);
                        break;
                    }
                    case Instruction.DIV:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(b == BigInteger.Zero ? BigInteger.Zero : BigInteger.Divide(a, b));
                        break;
                    }
                    case Instruction.SDIV:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        if (b == BigInteger.Zero)
                        {
                            PushInt(BigInteger.Zero);
                        }
                        else if (b == BigInteger.MinusOne && a == P255Int)
                        {
                            PushInt(P255);
                        }
                        else
                        {
                            PushBytes(BigInteger.Divide(a, b).ToBigEndianByteArray(true, 32));
                        }

                        break;
                    }
                    case Instruction.MOD:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(b == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a, b));
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        UpdateGas(GasCostOf.Low, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        if (b == BigInteger.Zero)
                        {
                            PushInt(BigInteger.Zero);
                        }
                        else
                        {
                            PushBytes((a.Sign * BigInteger.Remainder(a.Abs(), b.Abs()))
                                .ToBigEndianByteArray(true, 32));
                        }

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger mod = PopUInt();
                        PushInt(mod == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a + b, mod));
                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger mod = PopUInt();
                        PushInt(mod == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a * b, mod));
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

                            UpdateGas((_protocolSpecification.IsEip160Enabled ? GasCostOf.ExpByteEip160 : GasCostOf.ExpByte) * (ulong)(1 + expSize), ref gasAvailable);
                        }

                        if (baseInt == BigInteger.Zero)
                        {
                            PushInt(BigInteger.Zero);
                        }
                        else if (baseInt == BigInteger.One)
                        {
                            PushInt(BigInteger.One);
                        }
                        else
                        {
                            PushInt(BigInteger.ModPow(baseInt, exp, P256Int));
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
                        b.ToBigEndianBitArray256(ref _bits1);
                        int bitPosition = Math.Max(0, 248 - 8 * (int)a);
                        bool isSet = _bits1[bitPosition];
                        for (int i = 0; i < bitPosition; i++)
                        {
                            _bits1[i] = isSet;
                        }

                        PushBytes(_bits1.ToBytes());
                        break;
                    }
                    case Instruction.LT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(a < b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.GT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(a > b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SLT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        PushInt(BigInteger.Compare(a, b) < 0 ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SGT:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        PushInt(a > b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.EQ:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        PushInt(a == b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopInt();
                        PushInt(a.IsZero ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.AND:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        PopBytes().ToBigEndianBitArray256(ref _bits1);
                        PopBytes().ToBigEndianBitArray256(ref _bits2);
                        PushBytes(_bits1.And(_bits2).ToBytes());
                        break;
                    }
                    case Instruction.OR:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        PopBytes().ToBigEndianBitArray256(ref _bits1);
                        PopBytes().ToBigEndianBitArray256(ref _bits2);
                        PushBytes(_bits1.Or(_bits2).ToBytes());
                        break;
                    }
                    case Instruction.XOR:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        PopBytes().ToBigEndianBitArray256(ref _bits1);
                        PopBytes().ToBigEndianBitArray256(ref _bits2);
                        PushBytes(_bits1.Xor(_bits2).ToBytes());
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

                        PushBytes(res.WithoutLeadingZeros());
                        break;
                    }
                    case Instruction.BYTE:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger position = PopUInt();
                        byte[] bytes = PopBytes();

                        if (position >= BigInt32)
                        {
                            PushBytes(BytesZero);
                            break;
                        }

                        int adjustedPosition = bytes.Length - 32 + (int)position;
                        PushBytes(adjustedPosition < 0 ? BytesZero : bytes.Slice(adjustedPosition, 1));
                        break;
                    }
                    case Instruction.SHA3:
                    {
                        BigInteger memSrc = PopUInt();
                        BigInteger memLength = PopUInt();
                        UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmMemory.Div32Ceiling(memLength),
                            ref gasAvailable);
                        UpdateMemoryCost(memSrc, memLength);

                        byte[] memData = state.Memory.Load(memSrc, memLength);
                        PushBytes(Keccak.Compute(memData).Bytes);
                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushBytes(env.CodeOwner.Hex);
                        break;
                    }
                    case Instruction.BALANCE:
                    {
                        UpdateGas(_protocolSpecification.IsEip150Enabled ? GasCostOf.BalanceEip150 : GasCostOf.Balance, ref gasAvailable);
                        Address address = PopAddress();
                        BigInteger balance = _worldStateProvider.GetBalance(address);
                        PushInt(balance);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushBytes(env.Caller.Hex);
                        break;
                    }
                    case Instruction.CALLVALUE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(env.Value);
                        break;
                    }
                    case Instruction.ORIGIN:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushBytes(env.Originator.Hex);
                        break;
                    }
                    case Instruction.CALLDATALOAD:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger a = PopUInt();
                        PushBytes(GetPaddedSlice(env.InputData, a, 32));
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(env.InputData.Length);
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length),
                            ref gasAvailable);
                        UpdateMemoryCost(dest, length);

                        byte[] callDataSlice = GetPaddedSlice(env.InputData, src, length);
                        state.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.CODESIZE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(code.Length);
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length),
                            ref gasAvailable);
                        UpdateMemoryCost(dest, length);
                        byte[] callDataSlice = GetPaddedSlice(code, src, length);
                        state.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.GASPRICE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(env.GasPrice);
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        UpdateGas(_protocolSpecification.IsEip150Enabled ? GasCostOf.ExtCodeSizeEip150 : GasCostOf.ExtCodeSize, ref gasAvailable);
                        Address address = PopAddress();
                        byte[] accountCode = _worldStateProvider.GetCode(address);
                        PushInt(accountCode?.Length ?? BigInteger.Zero);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        Address address = PopAddress();
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        UpdateGas((_protocolSpecification.IsEip150Enabled ? GasCostOf.ExtCodeEip150 : GasCostOf.ExtCode) + GasCostOf.Memory * EvmMemory.Div32Ceiling(length),
                            ref gasAvailable);
                        UpdateMemoryCost(dest, length);
                        byte[] externalCode = _worldStateProvider.GetCode(address);
                        byte[] callDataSlice = GetPaddedSlice(externalCode, src, length);
                        state.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        UpdateGas(GasCostOf.BlockHash, ref gasAvailable);
                        BigInteger a = PopUInt();
                        if (a > BigInt256)
                        {
                            PushInt(BigInteger.Zero);
                        }
                        else if (a == BigInteger.Zero)
                        {
                            PushInt(BigInteger.Zero);
                        }
                        else
                        {
                            PushBytes(_blockhashProvider.GetBlockhash(env.CurrentBlock, (int)a).Bytes);
                        }

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushBytes(env.CurrentBlock.Beneficiary.Hex);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(env.CurrentBlock.Difficulty);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(env.CurrentBlock.Timestamp);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(env.CurrentBlock.Number);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(env.CurrentBlock.GasLimit);
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
                        UpdateMemoryCost(memPosition, BigInt32);
                        byte[] memData = state.Memory.Load(memPosition);
                        PushBytes(memData);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger memPosition = PopUInt();
                        byte[] data = PopBytes();
                        UpdateMemoryCost(memPosition, BigInt32);
                        state.Memory.SaveWord(memPosition, data);
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger memPosition = PopUInt();
                        byte[] data = PopBytes();
                        UpdateMemoryCost(memPosition, BigInteger.One);
                        state.Memory.SaveByte(memPosition, data);
                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        UpdateGas(_protocolSpecification.IsEip150Enabled ? GasCostOf.SLoadEip150 : GasCostOf.SLoad, ref gasAvailable);
                        BigInteger storageIndex = PopUInt();
                        byte[] value = _storageProvider.Get(env.CodeOwner, storageIndex);
                        PushBytes(value);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        BigInteger storageIndex = PopUInt();
                        byte[] data = PopBytes().WithoutLeadingZeros();                      
                        byte[] previousValue = _storageProvider.Get(env.CodeOwner, storageIndex);

                        bool isNewValueZero = data.IsZero();
                        bool isValueChanged = !(isNewValueZero && previousValue.IsZero()) ||
                                              !Bytes.UnsafeCompare(previousValue, data);
                        if (isNewValueZero)
                        {
                            UpdateGas(GasCostOf.SReset, ref gasAvailable);
                            if (isValueChanged)
                            {
                                state.Refund += RefundOf.SClear;
                            }
                        }
                        else
                        {
                            UpdateGas(previousValue.IsZero() ? GasCostOf.SSet : GasCostOf.SReset,
                                ref gasAvailable);
                        }

                        if (isValueChanged)
                        {
                            byte[] newValue = isNewValueZero ? new byte[] { 0 } : data;
                            _storageProvider.Set(env.CodeOwner, storageIndex, newValue);
                            _logger?.Log($"  UPDATING STORAGE: {env.CodeOwner} {storageIndex} {Hex.FromBytes(newValue, true)}");
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        int dest = (int)PopUInt();
                        ValidateJump(dest);
                        programCounter = dest;
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        UpdateGas(GasCostOf.High, ref gasAvailable);
                        int dest = (int)PopUInt();
                        BigInteger condition = PopUInt();
                        if (condition > BigInteger.Zero)
                        {
                            ValidateJump(dest);
                            programCounter = dest;
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(programCounter - 1L);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(state.ActiveWordsInMemory * 32UL);
                        break;
                    }
                    case Instruction.GAS:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(gasAvailable);
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
                            PushBytes(EmptyBytes);
                        }
                        else
                        {
                            PushInt(code[programCounterInt]);
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

                        PushBytes(usedFromCode != length
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
                        BigInteger memoryPos = PopUInt();
                        BigInteger length = PopUInt();
                        int topicsCount = instruction - Instruction.LOG0;
                        UpdateMemoryCost(memoryPos, length);
                        UpdateGas(
                            GasCostOf.Log + (ulong)topicsCount * GasCostOf.LogTopic +
                            (ulong)length * GasCostOf.LogData, ref gasAvailable);

                        byte[] data = state.Memory.Load(memoryPos, length);
                        Keccak[] topics = new Keccak[topicsCount];
                        for (int i = 0; i < topicsCount; i++)
                        {
                            topics[i] = new Keccak(PopBytes().PadLeft(32));
                        }

                        LogEntry logEntry = new LogEntry(
                            env.CodeOwner,
                            data,
                            topics);
                        state.Logs.Add(logEntry);
                        break;
                    }
                    case Instruction.CREATE:
                    {
                        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
                        if (!_worldStateProvider.AccountExists(env.CodeOwner))
                        {
                            _worldStateProvider.CreateAccount(env.CodeOwner, BigInteger.Zero);
                        }

                        BigInteger value = PopUInt();
                        BigInteger memoryPositionOfInitCode = PopUInt();
                        BigInteger initCodeLength = PopUInt();

                        UpdateGas(GasCostOf.Create, ref gasAvailable);
                        UpdateMemoryCost(memoryPositionOfInitCode, initCodeLength);

                        byte[] initCode = state.Memory.Load(memoryPositionOfInitCode, initCodeLength);

                        Keccak contractAddressKeccak =
                            Keccak.Compute(Rlp.Encode(env.CodeOwner, _worldStateProvider.GetNonce(env.CodeOwner)));
                        Address contractAddress = new Address(contractAddressKeccak);

                        if (value > _worldStateProvider.GetBalance(env.CodeOwner))
                        {
                            PushInt(BigInteger.Zero);
                            break;
                        }

                        _worldStateProvider.IncrementNonce(env.CodeOwner);

                        bool accountExists = _worldStateProvider.AccountExists(contractAddress);
                        if (accountExists && !_worldStateProvider.IsEmptyAccount(contractAddress))
                        {
                            throw new TransactionCollisionException();
                        }

                        int stateSnapshot = _worldStateProvider.TakeSnapshot();
                        int storageSnapshot = _storageProvider.TakeSnapshot();

                        ulong callGas = gasAvailable;
                        UpdateGas(callGas, ref gasAvailable);

                        _worldStateProvider.UpdateBalance(env.CodeOwner, -value);
                        if (!accountExists)
                        {
                            _worldStateProvider.CreateAccount(contractAddress, value);
                        }
                        else
                        {
                            _worldStateProvider.UpdateBalance(contractAddress, value);
                        }

                        _logger?.Log("  INIT: " + contractAddress);

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.Value = value;
                        callEnv.Caller = env.CodeOwner;
                        callEnv.Originator = env.Originator;
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.InputData = initCode;
                        callEnv.CodeOwner = contractAddress;
                        callEnv.MachineCode = initCode;
                        EvmState callState = new EvmState(
                            callGas,
                            callEnv,
                            ExecutionType.Create,
                            stateSnapshot,
                            storageSnapshot,
                            BigInteger.Zero,
                            BigInteger.Zero);
                        UpdateCurrentState();
                        return new CallResult(callState);
                    }
                    case Instruction.RETURN:
                    {
                        ulong gasCost = GasCostOf.Zero;
                        BigInteger memoryPos = PopUInt();
                        BigInteger length = PopUInt();

                        UpdateGas(gasCost, ref gasAvailable);
                        UpdateMemoryCost(memoryPos, length);
                        byte[] returnData = state.Memory.Load(memoryPos, length);

                        LogInstructionResult(instruction, gasBefore);

                        UpdateCurrentState();
                        return new CallResult(returnData);
                    }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    {
                        if (instruction == Instruction.DELEGATECALL && !_protocolSpecification.IsEip7Enabled)
                        {
                            throw new InvalidInstructionException((byte)instruction);
                        }

                        BigInteger gasLimit = PopUInt();
                        byte[] toAddress = PopBytes();
                        BigInteger value = instruction == Instruction.DELEGATECALL ? env.Value : PopUInt();
                        BigInteger dataOffset = PopUInt();
                        BigInteger dataLength = PopUInt();
                        BigInteger outputOffset = PopUInt();
                        BigInteger outputLength = PopUInt();

                        // TODO: there is an inconsistency in naming below - target / code owner - will resolve it after adding DELEGATECALL
                        Address target = instruction == Instruction.CALL
                            ? ToAddress(toAddress)
                            : env.CodeOwner; // CALLCODE targets the current contract, CALL targets another contract
                        BigInteger addressInt = toAddress.ToUnsignedBigInteger();
                        bool isPrecompile = addressInt <= 4 && addressInt > 0;

                        Address codeSource = ToAddress(toAddress);
                        ulong gasExtra = _protocolSpecification.IsEip150Enabled ? GasCostOf.CallOrCallCodeEip150 : GasCostOf.CallOrCallCode;
                        if (!value.IsZero && instruction != Instruction.DELEGATECALL)
                        {
                            gasExtra += GasCostOf.CallValue - GasCostOf.CallStipend;
                        }

                        bool didAccountExist = _worldStateProvider.AccountExists(target);
                        if (!didAccountExist)
                        {
                            gasExtra += GasCostOf.NewAccount;
                        }

                        if (gasLimit > gasAvailable)
                        {
                            throw new OutOfGasException(); // important to avoid casting
                        }

                        UpdateGas(gasExtra, ref gasAvailable);
                        UpdateMemoryCost(dataOffset, dataLength);
                        UpdateMemoryCost(outputOffset, outputLength);

                        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
                        {
                            PushInt(BigInteger.Zero);
                            break;
                        }

                        byte[] callData = state.Memory.Load(dataOffset, dataLength);

                        int stateSnapshot = _worldStateProvider.TakeSnapshot();
                        int storageSnapshot = _storageProvider.TakeSnapshot();

                        if (!value.IsZero)
                        {
                            if (_worldStateProvider.GetBalance(env.CodeOwner) < value)
                            {
                                state.Memory.Save(outputOffset, new byte[(int)outputLength]);
                                PushInt(BigInteger.Zero);
                                _logger?.Log($"  {instruction} FAIL - NOT ENOUGH BALANCE");
                                break;
                            }

                            _worldStateProvider.UpdateBalance(env.CodeOwner, -value);
                        }

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.Originator = env.Originator;
                        callEnv.Caller = isPrecompile ? target : (instruction == Instruction.DELEGATECALL ? env.Caller : env.CodeOwner);
                        callEnv.CodeOwner = isPrecompile ? codeSource : target;
                        callEnv.Value = (instruction == Instruction.DELEGATECALL ? env.Value : value);
                        callEnv.InputData = callData;
                        callEnv.MachineCode = _worldStateProvider.GetCode(codeSource);

                        if (isPrecompile)
                        {
                            EvmState precompileState = new EvmState(
                                (ulong)gasLimit,
                                callEnv,
                                ExecutionType.Precompile,
                                stateSnapshot,
                                storageSnapshot,
                                outputOffset,
                                outputLength);
                            UpdateGas((ulong)gasLimit, ref gasAvailable);
                            UpdateCurrentState();
                            return new CallResult(precompileState);
                        }

                        if (!_worldStateProvider.AccountExists(target))
                        {
                            _worldStateProvider.CreateAccount(target, value);
                        }
                        else
                        {
                            _worldStateProvider.UpdateBalance(target, value);
                        }

                        ulong gasCap = (ulong)gasLimit;
                        if (_protocolSpecification.IsEip150Enabled)
                        {
                            gasCap = gasExtra < gasAvailable
                                ? Math.Min(gasAvailable - gasExtra - (gasAvailable - gasExtra) / 64, (ulong)gasLimit)
                                : (ulong)gasLimit;
                        }
                        else if (gasAvailable < gasCap)
                        {
                            throw new OutOfGasException();
                        }

                        ulong callGas = value.IsZero ? gasCap : gasCap + GasCostOf.CallStipend;
                        UpdateGas(callGas, ref gasAvailable);

                        EvmState callState = new EvmState(
                            callGas,
                            callEnv,
                            instruction == Instruction.CALL ? ExecutionType.Call : ExecutionType.Callcode,
                            stateSnapshot,
                            storageSnapshot,
                            outputOffset,
                            outputLength);
                        UpdateCurrentState();
                        return new CallResult(callState);
                    }
                    case Instruction.INVALID:
                    {
                        throw new InvalidInstructionException((byte)instruction);
                    }
                    case Instruction.SELFDESTRUCT:
                    {
                        UpdateGas(_protocolSpecification.IsEip150Enabled ? GasCostOf.SelfDestructEip150 : GasCostOf.SelfDestruct, ref gasAvailable);
                        Address inheritor = PopAddress();
                        if (!state.DestroyList.Contains(env.CodeOwner))
                        {
                            state.DestroyList.Add(env.CodeOwner);

                            BigInteger ownerBalance = _worldStateProvider.GetBalance(env.CodeOwner);
                            if (!_worldStateProvider.AccountExists(inheritor))
                            {
                                _worldStateProvider.CreateAccount(inheritor, ownerBalance);
                                if (_protocolSpecification.IsEip150Enabled)
                                {
                                    UpdateGas(GasCostOf.NewAccount, ref gasAvailable);
                                }
                            }
                            else
                            {
                                if (!inheritor.Equals(env.CodeOwner))
                                {
                                    _worldStateProvider.UpdateBalance(inheritor, ownerBalance);
                                }
                            }

                            _worldStateProvider.UpdateBalance(env.CodeOwner, -ownerBalance);

                            LogInstructionResult(instruction, gasBefore);
                        }

                        UpdateCurrentState();
                        return CallResult.Empty;
                    }
                    default:
                    {
                        _logger?.Log("UNKNOWN INSTRUCTION");

                        throw new InvalidInstructionException((byte)instruction);
                    }
                }

                LogInstructionResult(instruction, gasBefore);
            }

            UpdateCurrentState();
            return CallResult.Empty;
        }

        public static ulong CalculateMemoryRequirements(ulong initial, BigInteger position, BigInteger length)
        {
            if (length == 0)
            {
                return initial;
            }

            return Math.Max(initial, EvmMemory.Div32Ceiling(position + length));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong CalculateMemoryCost(ulong initial, ulong final)
        {
            if (final <= initial)
            {
                return 0UL;
            }

            return (ulong)((final - initial) * GasCostOf.Memory + BigInteger.Divide(BigInteger.Pow(final, 2), 512) -
                           BigInteger.Divide(BigInteger.Pow(initial, 2), 512));
        }

        private class CallResult
        {
            public static readonly CallResult Empty = new CallResult();

            public CallResult(EvmState stateToExecute)
            {
                StateToExecute = stateToExecute;
            }

            private CallResult()
            {
            }

            public CallResult(byte[] output)
            {
                Output = output;
            }

            public EvmState StateToExecute { get; }
            public byte[] Output { get; } = EmptyBytes;
            public bool IsReturn => StateToExecute == null;
        }
    }
}