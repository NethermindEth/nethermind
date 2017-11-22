using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Nevermind.Evm.Precompiles;

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
        public static readonly BigInteger BigInt32 = 32;
        public static readonly BigInteger BigIntMaxInt = int.MaxValue;
        private static readonly byte[] EmptyBytes = new byte[0];
        private static readonly byte[] BytesOne = { 1 };
        private static readonly byte[] BytesZero = { 0 };
        private static BitArray _bits1 = new BitArray(256);
        private static BitArray _bits2 = new BitArray(256);
        private readonly IBlockhashProvider _blockhashProvider;
        private readonly ILogger _logger;
        private readonly IProtocolSpecification _protocolSpecification;
        private readonly IStateProvider _stateProvider;
        private readonly Stack<EvmState> _stateStack = new Stack<EvmState>();
        private readonly IStorageProvider _storageProvider;
        private Dictionary<BigInteger, IPrecompiledContract> _precompiledContracts;
        private byte[] _returnDataBuffer = EmptyBytes;

        public VirtualMachine(IProtocolSpecification protocolSpecification, IStateProvider stateProvider, IStorageProvider storageProvider, IBlockhashProvider blockhashProvider, ILogger logger)
        {
            _logger = logger;
            _protocolSpecification = protocolSpecification;
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _blockhashProvider = blockhashProvider;

            InitializePrecompiledContracts();
        }

        // TODO: can refactor now after all tests are passing?
        public (byte[] output, TransactionSubstate) Run(EvmState state)
        {
            EvmState currentState = state;
            byte[] previousCallResult = null;
            byte[] previousCallOutput = EmptyBytes;
            BigInteger previousCallOutputDestination = BigInteger.Zero;
            while (true)
            {
                if (!currentState.IsContinuation)
                {
                    _returnDataBuffer = EmptyBytes;
                }

                try
                {
                    if (_logger != null)
                    {
                        string intro = (currentState.IsContinuation ? "CONTINUE" : "BEGIN") + (currentState.IsStatic ? " STATIC" : string.Empty);
                        _logger?.Log($"{intro} {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} (at {currentState.Env.ExecutingAccount})");
                    }

                    CallResult callResult;
                    if (currentState.ExecutionType == ExecutionType.Precompile || currentState.ExecutionType == ExecutionType.DirectPrecompile)
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

                    if (currentState.ExecutionType == ExecutionType.Transaction || currentState.ExecutionType == ExecutionType.DirectCreate || currentState.ExecutionType == ExecutionType.DirectPrecompile)
                    {
                        return (callResult.Output, new TransactionSubstate(currentState.Refund, currentState.DestroyList, currentState.Logs, callResult.ShouldRevert));
                    }

                    Address callCodeOwner = currentState.Env.ExecutingAccount;
                    EvmState previousState = currentState;
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                    currentState.GasAvailable += previousState.GasAvailable;
                    currentState.Refund += previousState.Refund;

                    if (!callResult.ShouldRevert)
                    {
                        foreach (Address address in previousState.DestroyList)
                        {
                            currentState.DestroyList.Add(address);
                        }

                        foreach (LogEntry logEntry in previousState.Logs)
                        {
                            currentState.Logs.Add(logEntry);
                        }

                        if (previousState.ExecutionType == ExecutionType.Create || previousState.ExecutionType == ExecutionType.DirectCreate)
                        {
                            ulong codeDepositGasCost = GasCostOf.CodeDeposit * (ulong)callResult.Output.Length; // TODO: should EIP-170 apply here
                            if (_protocolSpecification.IsEip2Enabled || currentState.GasAvailable > codeDepositGasCost)
                            {
                                Keccak codeHash = _stateProvider.UpdateCode(callResult.Output);
                                _stateProvider.UpdateCodeHash(callCodeOwner, codeHash);

                                currentState.GasAvailable -= codeDepositGasCost;
                            }

                            previousCallResult = callCodeOwner.Hex;
                            previousCallOutput = EmptyBytes;
                            previousCallOutputDestination = BigInteger.Zero;
                            _returnDataBuffer = EmptyBytes;
                        }
                        else
                        {
                            previousCallResult = StatusCode.SuccessBytes;
                            // TODO: can remove the line below now after have the buffer
                            previousCallOutput = GetPaddedSlice(callResult.Output, BigInteger.Zero, BigInteger.Min(callResult.Output.Length, previousState.OutputLength));
                            previousCallOutputDestination = previousState.OutputDestination;
                            _returnDataBuffer = callResult.Output;
                        }

                        _logger?.Log($"END {previousState.ExecutionType} AT DEPTH {previousState.Env.CallDepth} (RESULT {Hex.FromBytes(previousCallResult ?? EmptyBytes, true)}) RETURNS ({previousCallOutputDestination} : {Hex.FromBytes(previousCallOutput, true)})");
                    }
                    else
                    {
                        _logger?.Log($"REVERT {previousState.ExecutionType} AT DEPTH {previousState.Env.CallDepth} (RESULT {Hex.FromBytes(previousCallResult ?? EmptyBytes, true)}) RETURNS ({previousCallOutputDestination} : {Hex.FromBytes(previousCallOutput, true)})");
                        _stateProvider.Restore(previousState.StateSnapshot);
                        _storageProvider.Restore(previousState.StorageSnapshot);
                        previousCallResult = StatusCode.FailureBytes;
                        previousCallOutput = GetPaddedSlice(callResult.Output, BigInteger.Zero, BigInteger.Min(callResult.Output.Length, previousState.OutputLength));
                        previousCallOutputDestination = previousState.OutputDestination;
                        _returnDataBuffer = callResult.Output;
                    }
                }
                catch (EvmException ex)
                {
                    _logger?.Log($"EXCEPTION ({ex.GetType().Name}) IN {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} - RESTORING SNAPSHOT");
                    _stateProvider.Restore(currentState.StateSnapshot);
                    _storageProvider.Restore(currentState.StorageSnapshot);

                    if (currentState.ExecutionType == ExecutionType.Transaction || currentState.ExecutionType == ExecutionType.DirectPrecompile || currentState.ExecutionType == ExecutionType.DirectCreate)
                    {
                        throw;
                    }

                    previousCallResult = StatusCode.FailureBytes;
                    previousCallOutput = EmptyBytes;
                    previousCallOutputDestination = BigInteger.Zero;
                    _returnDataBuffer = EmptyBytes;

                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                }
            }
        }

        private void InitializePrecompiledContracts()
        {
            _precompiledContracts = new Dictionary<BigInteger, IPrecompiledContract>
            {
                [ECRecoverPrecompiledContract.Instance.Address] = ECRecoverPrecompiledContract.Instance,
                [Sha256PrecompiledContract.Instance.Address] = Sha256PrecompiledContract.Instance,
                [Ripemd160PrecompiledContract.Instance.Address] = Ripemd160PrecompiledContract.Instance,
                [IdentityPrecompiledContract.Instance.Address] = IdentityPrecompiledContract.Instance
            };

            if (_protocolSpecification.IsEip196Enabled)
            {
                _precompiledContracts[ModExpPrecompiledContract.Instance.Address] = ModExpPrecompiledContract.Instance;
            }

            if (_protocolSpecification.IsEip197Enabled)
            {
                _precompiledContracts[ModExpPrecompiledContract.Instance.Address] = ModExpPrecompiledContract.Instance;
            }

            if (_protocolSpecification.IsEip198Enabled)
            {
                _precompiledContracts[ModExpPrecompiledContract.Instance.Address] = ModExpPrecompiledContract.Instance;
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
        public void UpdateGas(ulong gasCost, ref ulong gasAvailable)
        {
            _logger?.Log($"  UPDATE GAS (-{gasCost})");
            if (gasAvailable < gasCost)
            {
                throw new OutOfGasException();
            }

            gasAvailable -= gasCost;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RefundGas(ulong refund, ref ulong gasAvailable)
        {
            _logger?.Log($"  UPDATE GAS (+{refund})");
            gasAvailable += refund;
        }

        private CallResult ExecutePrecompile(EvmState state)
        {
            byte[] callData = state.Env.InputData;
            BigInteger transferValue = state.Env.TransferValue;
            ulong gasAvailable = state.GasAvailable;

            BigInteger precompileId = state.Env.MachineCode.ToUnsignedBigInteger();
            ulong baseGasCost = _precompiledContracts[precompileId].BaseGasCost();
            ulong dataGasCost = _precompiledContracts[precompileId].DataGasCost(callData);
            if (gasAvailable < dataGasCost + baseGasCost)
            {
                throw new OutOfGasException();
            }

            if (!_stateProvider.AccountExists(state.Env.ExecutingAccount))
            {
                _stateProvider.CreateAccount(state.Env.ExecutingAccount, transferValue);
            }
            else
            {
                _stateProvider.UpdateBalance(state.Env.ExecutingAccount, transferValue);
            }

            UpdateGas(baseGasCost, ref gasAvailable);
            UpdateGas(dataGasCost, ref gasAvailable);
            state.GasAvailable = gasAvailable;

            try
            {
                byte[] output = _precompiledContracts[precompileId].Run(callData);
                return new CallResult(output);
            }
            catch (Exception)
            {
                return new CallResult(EmptyBytes);
            }
        }

        private CallResult ExecuteCall(EvmState evmState, byte[] previousCallResult, byte[] previousCallOutput, BigInteger previousCallOutputDestination)
        {
            ExecutionEnvironment env;
            byte[][] bytesOnStack;
            BigInteger[] intsOnStack;
            bool[] intPositions;
            int stackHead;
            ulong gasAvailable;
            long programCounter;
            byte[] code;
            bool[] jumpDestinations = null;

            ApplyState();

            if (!evmState.IsContinuation)
            {
                if (!_stateProvider.AccountExists(env.ExecutingAccount))
                {
                    _stateProvider.CreateAccount(env.ExecutingAccount, env.TransferValue);
                }
                else
                {
                    _stateProvider.UpdateBalance(env.ExecutingAccount, env.TransferValue);
                }

                if ((evmState.ExecutionType == ExecutionType.Create || evmState.ExecutionType == ExecutionType.DirectCreate) && _protocolSpecification.IsEip158Enabled)
                {
                    _stateProvider.IncrementNonce(env.ExecutingAccount);
                }
            }

            void ApplyState()
            {
                env = evmState.Env;
                bytesOnStack = evmState.BytesOnStack;
                intsOnStack = evmState.IntsOnStack;
                intPositions = evmState.IntPositions;
                stackHead = evmState.StackHead;
                gasAvailable = evmState.GasAvailable;
                programCounter = (long)evmState.ProgramCounter;
                code = env.MachineCode;
            }

            void UpdateCurrentState()
            {
                evmState.ProgramCounter = programCounter;
                evmState.GasAvailable = gasAvailable;
                evmState.StackHead = stackHead;
            }

            void LogInstructionResult(Instruction instruction, ulong gasBefore)
            {
                _logger?.Log(
                    $"  END {env.CallDepth}_{instruction} GAS {gasAvailable} ({gasBefore - gasAvailable}) STACK {stackHead} MEMORY {evmState.Memory.Size / 32L} PC {programCounter}");
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
                ulong memoryCost = evmState.Memory.CalculateMemoryCost(position, length);
                _logger?.Log($"  MEMORY COST {memoryCost}");

                UpdateGas(memoryCost, ref gasAvailable);
            }

            void ValidateJump(int destination)
            {
                if (jumpDestinations == null)
                {
                    CalculateJumpDestinations();
                }

                if (destination < 0 || destination > jumpDestinations.Length || !jumpDestinations[destination])
                {
                    throw new InvalidJumpDestinationException();
                }
            }

            void CalculateJumpDestinations()
            {
                jumpDestinations = new bool[code.Length];
                int index = 0;
                while (index < code.Length)
                {
                    Instruction instruction = (Instruction)code[index];
                    jumpDestinations[index] = !_protocolSpecification.AreJumpDestinationsUsed || instruction == Instruction.JUMPDEST;
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
                evmState.Memory.Save(previousCallOutputDestination, previousCallOutput);
            }

            // TODO: pool
            BigInteger bigReg;
            //BigInteger a;
            //BigInteger b;
            //BigInteger res;
            //Address target;
            //Address recipient;
            //Address codeSource;
            //BigInteger baseInt;
            //BigInteger exp;

            while (programCounter < code.Length)
            {
                ulong gasBefore = gasAvailable;

                Instruction instruction = (Instruction)code[(int)programCounter];
                programCounter++;

                _logger?.Log($"{instruction} (0x{instruction:X})");
                if (ShouldLog.EvmStack)
                {
                    for (int i = 0; i < Math.Min(stackHead, 7); i++)
                    {
                        if (intPositions[stackHead - i - 1])
                        {
                            _logger?.Log($"STACK{i} -> {intsOnStack[stackHead - i - 1]}");
                        }
                        else
                        {
                            _logger?.Log($"STACK{i} -> {Hex.FromBytes(bytesOnStack[stackHead - i - 1], true)}");
                        }
                    }
                }

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

                        byte[] memData = evmState.Memory.Load(memSrc, memLength);
                        PushBytes(Keccak.Compute(memData).Bytes);
                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushBytes(env.ExecutingAccount.Hex);
                        break;
                    }
                    case Instruction.BALANCE:
                    {
                        UpdateGas(_protocolSpecification.IsEip150Enabled ? GasCostOf.BalanceEip150 : GasCostOf.Balance, ref gasAvailable);
                        Address address = PopAddress();
                        BigInteger balance = _stateProvider.GetBalance(address);
                        PushInt(balance);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushBytes(env.Sender.Hex);
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
                        evmState.Memory.Save(dest, callDataSlice);
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
                        evmState.Memory.Save(dest, callDataSlice);
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
                        byte[] accountCode = _stateProvider.GetCode(address);
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
                        byte[] externalCode = _stateProvider.GetCode(address);
                        byte[] callDataSlice = GetPaddedSlice(externalCode, src, length);
                        evmState.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.RETURNDATASIZE:
                    {
                        if (!_protocolSpecification.IsEip211Enabled)
                        {
                            throw new InvalidInstructionException((byte)instruction);
                        }

                        UpdateGas(GasCostOf.Base, ref gasAvailable);
                        PushInt(_returnDataBuffer.Length);
                        break;
                    }
                    case Instruction.RETURNDATACOPY:
                    {
                        if (!_protocolSpecification.IsEip211Enabled)
                        {
                            throw new InvalidInstructionException((byte)instruction);
                        }

                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length), ref gasAvailable);
                        UpdateMemoryCost(dest, length);

                        if (src + length > _returnDataBuffer.Length)
                        {
                            throw new EvmAccessViolationException();
                        }

                        byte[] returnDataSlice = GetPaddedSlice(_returnDataBuffer, src, length);
                        evmState.Memory.Save(dest, returnDataSlice);
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
                        byte[] memData = evmState.Memory.Load(memPosition);
                        PushBytes(memData);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger memPosition = PopUInt();
                        byte[] data = PopBytes();
                        UpdateMemoryCost(memPosition, BigInt32);
                        evmState.Memory.SaveWord(memPosition, data);
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        UpdateGas(GasCostOf.VeryLow, ref gasAvailable);
                        BigInteger memPosition = PopUInt();
                        byte[] data = PopBytes();
                        UpdateMemoryCost(memPosition, BigInteger.One);
                        evmState.Memory.SaveByte(memPosition, data);
                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        UpdateGas(_protocolSpecification.IsEip150Enabled ? GasCostOf.SLoadEip150 : GasCostOf.SLoad, ref gasAvailable);
                        BigInteger storageIndex = PopUInt();
                        byte[] value = _storageProvider.Get(env.ExecutingAccount, storageIndex);
                        PushBytes(value);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        if (evmState.IsStatic)
                        {
                            throw new StaticCallViolationException();
                        }

                        BigInteger storageIndex = PopUInt();
                        byte[] data = PopBytes().WithoutLeadingZeros();
                        byte[] previousValue = _storageProvider.Get(env.ExecutingAccount, storageIndex);

                        bool isNewValueZero = data.IsZero();
                        bool isValueChanged = !(isNewValueZero && previousValue.IsZero()) ||
                                              !Bytes.UnsafeCompare(previousValue, data);
                        if (isNewValueZero)
                        {
                            UpdateGas(GasCostOf.SReset, ref gasAvailable);
                            if (isValueChanged)
                            {
                                evmState.Refund += RefundOf.SClear;
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
                            _storageProvider.Set(env.ExecutingAccount, storageIndex, newValue);
                            _logger?.Log($"  UPDATING STORAGE: {env.ExecutingAccount} {storageIndex} {Hex.FromBytes(newValue, true)}");
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        UpdateGas(GasCostOf.Mid, ref gasAvailable);
                        bigReg = PopUInt();
                        if (bigReg > BigIntMaxInt)
                        {
                            throw new InvalidJumpDestinationException();
                        }

                        int dest = (int)bigReg;
                        ValidateJump(dest);
                        programCounter = dest;
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        UpdateGas(GasCostOf.High, ref gasAvailable);
                        bigReg = PopUInt();
                        if (bigReg > BigIntMaxInt)
                        {
                            throw new InvalidJumpDestinationException();
                        }

                        int dest = (int)bigReg;
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
                        PushInt(evmState.Memory.Size);
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
                        if (evmState.IsStatic)
                        {
                            throw new StaticCallViolationException();
                        }

                        BigInteger memoryPos = PopUInt();
                        BigInteger length = PopUInt();
                        int topicsCount = instruction - Instruction.LOG0;
                        UpdateMemoryCost(memoryPos, length);
                        UpdateGas(
                            GasCostOf.Log + (ulong)topicsCount * GasCostOf.LogTopic +
                            (ulong)length * GasCostOf.LogData, ref gasAvailable);

                        byte[] data = evmState.Memory.Load(memoryPos, length);
                        Keccak[] topics = new Keccak[topicsCount];
                        for (int i = 0; i < topicsCount; i++)
                        {
                            topics[i] = new Keccak(PopBytes().PadLeft(32));
                        }

                        LogEntry logEntry = new LogEntry(
                            env.ExecutingAccount,
                            data,
                            topics);
                        evmState.Logs.Add(logEntry);
                        break;
                    }
                    case Instruction.CREATE:
                    {
                        if (evmState.IsStatic)
                        {
                            throw new StaticCallViolationException();
                        }

                        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
                        if (!_stateProvider.AccountExists(env.ExecutingAccount))
                        {
                            _stateProvider.CreateAccount(env.ExecutingAccount, BigInteger.Zero);
                        }

                        BigInteger value = PopUInt();
                        BigInteger memoryPositionOfInitCode = PopUInt();
                        BigInteger initCodeLength = PopUInt();

                        UpdateGas(GasCostOf.Create, ref gasAvailable);
                        UpdateMemoryCost(memoryPositionOfInitCode, initCodeLength);

                        byte[] initCode = evmState.Memory.Load(memoryPositionOfInitCode, initCodeLength);

                        Keccak contractAddressKeccak =
                            Keccak.Compute(Rlp.Encode(env.ExecutingAccount, _stateProvider.GetNonce(env.ExecutingAccount)));
                        Address contractAddress = new Address(contractAddressKeccak);

                        if (value > _stateProvider.GetBalance(env.ExecutingAccount))
                        {
                            PushInt(BigInteger.Zero);
                            break;
                        }

                        _stateProvider.IncrementNonce(env.ExecutingAccount);

                        ulong callGas = _protocolSpecification.IsEip150Enabled ? gasAvailable - gasAvailable / 64UL : gasAvailable;
                        UpdateGas(callGas, ref gasAvailable);

                        bool accountExists = _stateProvider.AccountExists(contractAddress);
                        if (accountExists && !_stateProvider.IsEmptyAccount(contractAddress))
                        {
                            // TODO: clients are not consistent here - following tests
                            PushInt(BigInteger.Zero);
                            break;
                        }

                        int stateSnapshot = _stateProvider.TakeSnapshot();
                        int storageSnapshot = _storageProvider.TakeSnapshot();

                        _stateProvider.UpdateBalance(env.ExecutingAccount, -value);
                        _logger?.Log("  INIT: " + contractAddress);

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.TransferValue = value;
                        callEnv.Value = value;
                        callEnv.Sender = env.ExecutingAccount;
                        callEnv.Originator = env.Originator;
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.InputData = initCode;
                        callEnv.ExecutingAccount = contractAddress;
                        callEnv.MachineCode = initCode;
                        EvmState callState = new EvmState(
                            callGas,
                            callEnv,
                            ExecutionType.Create,
                            stateSnapshot,
                            storageSnapshot,
                            BigInteger.Zero,
                            BigInteger.Zero,
                            evmState.IsStatic,
                            false);
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
                        byte[] returnData = evmState.Memory.Load(memoryPos, length);

                        LogInstructionResult(instruction, gasBefore);

                        UpdateCurrentState();
                        return new CallResult(returnData);
                    }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    case Instruction.STATICCALL:
                    {
                        if (instruction == Instruction.DELEGATECALL && !_protocolSpecification.IsEip7Enabled ||
                            instruction == Instruction.STATICCALL && !_protocolSpecification.IsEip214Enabled)
                        {
                            throw new InvalidInstructionException((byte)instruction);
                        }

                        BigInteger gasLimit = PopUInt();
                        BigInteger callValue;
                        byte[] codeSource = PopBytes();
                        switch (instruction)
                        {
                            case Instruction.STATICCALL:
                                callValue = BigInteger.Zero;
                                break;
                            case Instruction.DELEGATECALL:
                                callValue = env.Value;
                                break;
                            default:
                                callValue = PopUInt();
                                break;
                        }

                        BigInteger transferValue = instruction == Instruction.DELEGATECALL ? BigInteger.Zero : callValue;
                        BigInteger dataOffset = PopUInt();
                        BigInteger dataLength = PopUInt();
                        BigInteger outputOffset = PopUInt();
                        BigInteger outputLength = PopUInt();

                        if (evmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE)
                        {
                            throw new StaticCallViolationException();
                        }

                        BigInteger addressInt = codeSource.ToUnsignedBigInteger();
                        bool isPrecompile = _precompiledContracts.ContainsKey(addressInt);
                        Address sender = instruction == Instruction.DELEGATECALL ? env.Sender : env.ExecutingAccount;
                        Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL ? ToAddress(codeSource) : env.ExecutingAccount;

                        _logger?.Log($"  SENDER {sender}");
                        _logger?.Log($"  CODE SOURCE {ToAddress(codeSource)}");
                        _logger?.Log($"  TARGET {target}");
                        _logger?.Log($"  VALUE {callValue}");
                        _logger?.Log($"  TRANSFER_VALUE {transferValue}");

                        ulong gasExtra = 0UL;
                        if (!transferValue.IsZero)
                        {
                            gasExtra += GasCostOf.CallValue;
                        }

                        if (!_protocolSpecification.IsEip158Enabled && !_stateProvider.AccountExists(target))
                        {
                            gasExtra += GasCostOf.NewAccount;
                        }

                        if (_protocolSpecification.IsEip158Enabled && transferValue != 0 && _stateProvider.IsDeadAccount(target))
                        {
                            gasExtra += GasCostOf.NewAccount;
                        }

                        if (!_protocolSpecification.IsEip150Enabled && gasLimit > gasAvailable)
                        {
                            throw new OutOfGasException(); // important to avoid casting
                        }

                        UpdateGas(_protocolSpecification.IsEip150Enabled ? GasCostOf.CallOrCallCodeEip150 : GasCostOf.CallOrCallCode, ref gasAvailable);
                        UpdateMemoryCost(dataOffset, dataLength);
                        byte[] callData = evmState.Memory.Load(dataOffset, dataLength);
                        UpdateMemoryCost(outputOffset, outputLength);

                        UpdateGas(gasExtra, ref gasAvailable);
                        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
                        {
                            if (!transferValue.IsZero)
                            {
                                RefundGas(GasCostOf.CallStipend, ref gasAvailable);
                            }

                            // TODO: need a test for this
                            _returnDataBuffer = EmptyBytes;
                            PushInt(BigInteger.Zero);
                            break;
                        }

                        if (!callValue.IsZero)
                        {
                            if (_stateProvider.GetBalance(env.ExecutingAccount) < transferValue)
                            {
                                RefundGas(GasCostOf.CallStipend, ref gasAvailable);
                                evmState.Memory.Save(outputOffset, new byte[(int)outputLength]);
                                _returnDataBuffer = EmptyBytes;
                                PushInt(BigInteger.Zero);
                                _logger?.Log($"  {instruction} FAIL - NOT ENOUGH BALANCE");
                                break;
                            }
                        }

                        int stateSnapshot = _stateProvider.TakeSnapshot();
                        int storageSnapshot = _storageProvider.TakeSnapshot();
                        _stateProvider.UpdateBalance(sender, -transferValue);

                        if (_protocolSpecification.IsEip150Enabled)
                        {
                            gasLimit = BigInteger.Min(gasAvailable - gasAvailable / 64UL, gasLimit);
                        }

                        ulong gasLimitUl = (ulong)gasLimit;
                        UpdateGas(gasLimitUl, ref gasAvailable);
                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.Originator = env.Originator;
                        callEnv.Sender = sender;
                        callEnv.ExecutingAccount = target;
                        callEnv.TransferValue = transferValue;
                        callEnv.Value = callValue;
                        callEnv.InputData = callData;
                        callEnv.MachineCode = isPrecompile ? addressInt.ToBigEndianByteArray() : _stateProvider.GetCode(ToAddress(codeSource));

                        BigInteger callGas = transferValue.IsZero ? gasLimitUl : gasLimitUl + GasCostOf.CallStipend;
                        _logger?.Log($"  CALL_GAS {callGas}");

                        EvmState callState = new EvmState(
                            (ulong)callGas,
                            callEnv,
                            isPrecompile ? ExecutionType.Precompile : (instruction == Instruction.CALL || instruction == Instruction.STATICCALL ? ExecutionType.Call : ExecutionType.Callcode),
                            stateSnapshot,
                            storageSnapshot,
                            outputOffset,
                            outputLength,
                            instruction == Instruction.STATICCALL || evmState.IsStatic,
                            false);
                        UpdateCurrentState();
                        return new CallResult(callState);
                    }
                    case Instruction.REVERT:
                    {
                        if (!_protocolSpecification.IsEip140Enabled)
                        {
                            throw new InvalidInstructionException((byte)instruction);
                        }

                        ulong gasCost = GasCostOf.Zero;
                        BigInteger memoryPos = PopUInt();
                        BigInteger length = PopUInt();

                        UpdateGas(gasCost, ref gasAvailable);
                        UpdateMemoryCost(memoryPos, length);
                        byte[] errorDetails = evmState.Memory.Load(memoryPos, length);

                        LogInstructionResult(instruction, gasBefore);

                        UpdateCurrentState();
                        return new CallResult(errorDetails, true);
                    }
                    case Instruction.INVALID:
                    {
                        throw new InvalidInstructionException((byte)instruction);
                    }
                    case Instruction.SELFDESTRUCT:
                    {
                        if (evmState.IsStatic)
                        {
                            throw new StaticCallViolationException();
                        }

                        UpdateGas(_protocolSpecification.IsEip150Enabled ? GasCostOf.SelfDestructEip150 : GasCostOf.SelfDestruct, ref gasAvailable);
                        Address inheritor = PopAddress();
                        if (!evmState.DestroyList.Contains(env.ExecutingAccount))
                        {
                            evmState.DestroyList.Add(env.ExecutingAccount);

                            BigInteger ownerBalance = _stateProvider.GetBalance(env.ExecutingAccount);
                            bool inheritorAccountExists = _stateProvider.AccountExists(inheritor);

                            if (!_protocolSpecification.IsEip158Enabled && !inheritorAccountExists && _protocolSpecification.IsEip150Enabled)
                            {
                                UpdateGas(GasCostOf.NewAccount, ref gasAvailable);
                            }

                            if (_protocolSpecification.IsEip158Enabled && ownerBalance != 0 && _stateProvider.IsDeadAccount(inheritor))
                            {
                                UpdateGas(GasCostOf.NewAccount, ref gasAvailable);
                            }

                            if (!inheritorAccountExists)
                            {
                                _stateProvider.CreateAccount(inheritor, ownerBalance);
                            }
                            else if (!inheritor.Equals(env.ExecutingAccount))
                            {
                                _stateProvider.UpdateBalance(inheritor, ownerBalance);
                            }

                            _stateProvider.UpdateBalance(env.ExecutingAccount, -ownerBalance);

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

        private class CallResult
        {
            public bool ShouldRevert { get; }
            public static readonly CallResult Empty = new CallResult();

            public CallResult(EvmState stateToExecute)
            {
                StateToExecute = stateToExecute;
            }

            private CallResult()
            {
            }

            public CallResult(byte[] output, bool shouldRevert = false)
            {
                ShouldRevert = shouldRevert;
                Output = output;
            }

            public EvmState StateToExecute { get; }
            public byte[] Output { get; } = EmptyBytes;
            public bool IsReturn => StateToExecute == null;
        }
    }
}