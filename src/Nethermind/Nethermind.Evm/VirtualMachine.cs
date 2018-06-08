/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Store;

namespace Nethermind.Evm
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
        private static readonly byte[] BytesOne = {1};
        private static readonly byte[] BytesZero = {0};
        private readonly IBlockhashProvider _blockhashProvider;
        private readonly LruCache<Keccak, CodeInfo> _codeCache = new LruCache<Keccak, CodeInfo>(4 * 1024);
        private readonly ILogger _logger;
        private readonly IStateProvider _state;
        private readonly Stack<EvmState> _stateStack = new Stack<EvmState>();
        private readonly IStorageProvider _storage;
        private int _instructionCounter;
        private Address _parityTouchBugAccount;
        private Dictionary<BigInteger, IPrecompiledContract> _precompiles;
        private byte[] _returnDataBuffer = EmptyBytes;
        private TransactionTrace _trace;
        private TransactionTraceEntry _traceEntry;

        public VirtualMachine(IStateProvider stateProvider, IStorageProvider storageProvider, IBlockhashProvider blockhashProvider, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _state = stateProvider;
            _storage = storageProvider;
            _blockhashProvider = blockhashProvider;

            InitializePrecompiledContracts();
        }

        // can refactor and integrate the other call
        public (byte[] output, TransactionSubstate) Run(EvmState state, IReleaseSpec releaseSpec, TransactionTrace trace)
        {
            // TODO: review the concept commented below
            //if (state.Env.CodeInfo.MachineCode.Length == 0 && state.ExecutionType == ExecutionType.Transaction)
            //{
            //    return (Bytes.Empty, new TransactionSubstate(0, new Collection<Address>(), new Collection<LogEntry>(), false));
            //}

            _instructionCounter = 0;
            _traceEntry = null;
            _trace = trace;

            IReleaseSpec spec = releaseSpec;
            EvmState currentState = state;
            byte[] previousCallResult = null;
            byte[] previousCallOutput = Bytes.Empty;
            BigInteger previousCallOutputDestination = BigInteger.Zero;
            while (true)
            {
                if (!currentState.IsContinuation)
                {
                    _returnDataBuffer = Bytes.Empty;
                }

                try
                {
                    if (_logger.IsDebugEnabled)
                    {
                        string intro = (currentState.IsContinuation ? "CONTINUE" : "BEGIN") + (currentState.IsStatic ? " STATIC" : string.Empty);
                        _logger.Debug($"{intro} {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} (at {currentState.Env.ExecutingAccount})");
                    }

                    CallResult callResult;
                    if (currentState.ExecutionType == ExecutionType.Precompile || currentState.ExecutionType == ExecutionType.DirectPrecompile)
                    {
                        callResult = ExecutePrecompile(currentState, spec);
                        if (!callResult.PrecompileSuccess.Value)
                        {
                            if (currentState.ExecutionType == ExecutionType.DirectPrecompile)
                            {
                                Metrics.EvmExceptions++;
                                // TODO: when direct / calls are treated same we should not need such differentiation
                                throw new PrecompileExecutionFailureException();
                            }

                            // TODO: testing it as it seems the way to pass zkSNARKs tests
                            currentState.GasAvailable = 0;
                        }
                    }
                    else
                    {
                        callResult = ExecuteCall(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                        if (!callResult.IsReturn)
                        {
                            _stateStack.Push(currentState);
                            currentState = callResult.StateToExecute;
                            previousCallResult = null; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests (failing block 9411 on Ropsten https://ropsten.etherscan.io/vmtrace?txhash=0x666194d15c14c54fffafab1a04c08064af165870ef9a87f65711dcce7ed27fe1)
                            previousCallOutput = Bytes.Empty; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests
                            continue;
                        }

                        if (callResult.IsException)
                        {
                            //if (_logger.IsDebugEnabled)
                            //{
                            //    _logger.Debug($"EXCEPTION ({ex.GetType().Name}) IN {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} - RESTORING SNAPSHOT");
                            //}

                            _state.Restore(currentState.StateSnapshot);
                            _storage.Restore(currentState.StorageSnapshot);

                            if (_parityTouchBugAccount != null)
                            {
                                _state.UpdateBalance(_parityTouchBugAccount, BigInteger.Zero, spec);
                                _parityTouchBugAccount = null;
                            }

                            if (currentState.ExecutionType == ExecutionType.Transaction || currentState.ExecutionType == ExecutionType.DirectPrecompile || currentState.ExecutionType == ExecutionType.DirectCreate)
                            {
                                throw new EvmStackOverflowException();
                            }

                            previousCallResult = StatusCode.FailureBytes;
                            previousCallOutput = Bytes.Empty;
                            previousCallOutputDestination = BigInteger.Zero;
                            _returnDataBuffer = Bytes.Empty;

                            currentState.Dispose();
                            currentState = _stateStack.Pop();
                            currentState.IsContinuation = true;
                            continue;
                        }
                    }

                    if (currentState.ExecutionType == ExecutionType.Transaction || currentState.ExecutionType == ExecutionType.DirectCreate || currentState.ExecutionType == ExecutionType.DirectPrecompile)
                    {
                        // TODO: review refund logic as there was a quick change for Refunds for Ropsten 2005537
                        return (callResult.Output, new TransactionSubstate(currentState.Refund, currentState.DestroyList, currentState.Logs, callResult.ShouldRevert));
                    }

                    Address callCodeOwner = currentState.Env.ExecutingAccount;
                    EvmState previousState = currentState;
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                    currentState.GasAvailable += previousState.GasAvailable;

                    if (!callResult.ShouldRevert)
                    {
                        currentState.Refund += previousState.Refund;

                        foreach (Address address in previousState.DestroyList)
                        {
                            currentState.DestroyList.Add(address);
                        }

                        for (int i = 0; i < previousState.Logs.Count; i++)
                        {
                            LogEntry logEntry = previousState.Logs[i];
                            currentState.Logs.Add(logEntry);
                        }

                        long gasAvailableForCodeDeposit = previousState.GasAvailable; // TODO: refactor, this is to fix 61363 Ropsten
                        if (previousState.ExecutionType == ExecutionType.Create || previousState.ExecutionType == ExecutionType.DirectCreate)
                        {
                            long codeDepositGasCost = GasCostOf.CodeDeposit * callResult.Output.Length;
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"Code deposit cost is {codeDepositGasCost} ({GasCostOf.CodeDeposit} * {callResult.Output.Length})");
                            }

                            if (gasAvailableForCodeDeposit >= codeDepositGasCost)
                            {
                                Keccak codeHash = _state.UpdateCode(callResult.Output);

                                _state.UpdateCodeHash(callCodeOwner, codeHash, spec);
                                previousCallResult = callCodeOwner.Hex;

                                currentState.GasAvailable -= codeDepositGasCost;
                            }
                            else
                            {
                                // TODO: out of gas - try to handle as everywhere else - test with 61362 (7933dd) on Ropsten - second contract creation
                                previousCallResult = BytesZero;
                                if (releaseSpec.IsEip2Enabled)
                                {
                                    currentState.GasAvailable -= gasAvailableForCodeDeposit;
                                    // TODO: there should be an OutOfGasException here and a proper reversal of the account creation (and value transfer and all state changes called in the CREATE call)
                                    // TODO: instead just adding the simplest way to fix 552387 on Ropsten
                                    _state.DeleteAccount(callCodeOwner);
                                }
                                else
                                {
                                    previousCallResult = callCodeOwner.Hex;
                                }
                            }

                            previousCallOutput = Bytes.Empty;
                            previousCallOutputDestination = BigInteger.Zero;
                            _returnDataBuffer = Bytes.Empty;
                        }
                        else
                        {
                            previousCallResult = callResult.PrecompileSuccess.HasValue ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes) : StatusCode.SuccessBytes;
                            previousCallOutput = callResult.Output.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                            previousCallOutputDestination = previousState.OutputDestination;
                            _returnDataBuffer = callResult.Output;
                        }

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"END {previousState.ExecutionType} AT DEPTH {previousState.Env.CallDepth} (RESULT {Hex.FromBytes(previousCallResult ?? Bytes.Empty, true)}) RETURNS ({previousCallOutputDestination} : {Hex.FromBytes(previousCallOutput, true)})");
                        }
                    }
                    else
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"REVERT {previousState.ExecutionType} AT DEPTH {previousState.Env.CallDepth} (RESULT {Hex.FromBytes(previousCallResult ?? Bytes.Empty, true)}) RETURNS ({previousCallOutputDestination} : {Hex.FromBytes(previousCallOutput, true)})");
                        }

                        _state.Restore(previousState.StateSnapshot);
                        _storage.Restore(previousState.StorageSnapshot);
                        previousCallResult = StatusCode.FailureBytes;
                        previousCallOutput = callResult.Output.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                        previousCallOutputDestination = previousState.OutputDestination;
                        _returnDataBuffer = callResult.Output;
                    }

                    previousState.Dispose();
                }
                catch (Exception ex) when (ex is EvmException || ex is OverflowException)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"EXCEPTION ({ex.GetType().Name}) IN {currentState.ExecutionType} AT DEPTH {currentState.Env.CallDepth} - RESTORING SNAPSHOT");
                    }

                    _state.Restore(currentState.StateSnapshot);
                    _storage.Restore(currentState.StorageSnapshot);

                    if (_parityTouchBugAccount != null)
                    {
                        _state.UpdateBalance(_parityTouchBugAccount, BigInteger.Zero, spec);
                        _parityTouchBugAccount = null;
                    }

                    if (currentState.ExecutionType == ExecutionType.Transaction || currentState.ExecutionType == ExecutionType.DirectPrecompile || currentState.ExecutionType == ExecutionType.DirectCreate)
                    {
                        throw;
                    }

                    previousCallResult = StatusCode.FailureBytes;
                    previousCallOutput = Bytes.Empty;
                    previousCallOutputDestination = BigInteger.Zero;
                    _returnDataBuffer = Bytes.Empty;

                    currentState.Dispose();
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                }
            }
        }

        public CodeInfo GetCachedCodeInfo(Address codeSource)
        {
            Keccak codeHash = _state.GetCodeHash(codeSource);
            CodeInfo cachedCodeInfo = _codeCache.Get(codeHash);
            if (cachedCodeInfo == null)
            {
                byte[] code = _state.GetCode(codeHash);
                if (code == null)
                {
                    return null;
                }

                cachedCodeInfo = new CodeInfo(code);
                _codeCache.Set(codeHash, cachedCodeInfo);
            }

            return cachedCodeInfo;
        }

        private void InitializePrecompiledContracts()
        {
            _precompiles = new Dictionary<BigInteger, IPrecompiledContract>
            {
                [EcRecoverPrecompiledContract.Instance.Address] = EcRecoverPrecompiledContract.Instance,
                [Sha256PrecompiledContract.Instance.Address] = Sha256PrecompiledContract.Instance,
                [Ripemd160PrecompiledContract.Instance.Address] = Ripemd160PrecompiledContract.Instance,
                [IdentityPrecompiledContract.Instance.Address] = IdentityPrecompiledContract.Instance,
                [Bn128AddPrecompiledContract.Instance.Address] = Bn128AddPrecompiledContract.Instance,
                [Bn128MulPrecompiledContract.Instance.Address] = Bn128MulPrecompiledContract.Instance,
                [Bn128PairingPrecompiledContract.Instance.Address] = Bn128PairingPrecompiledContract.Instance,
                [ModExpPrecompiledContract.Instance.Address] = ModExpPrecompiledContract.Instance
            };
        }

        public bool UpdateGas(long gasCost, ref long gasAvailable)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  UPDATE GAS (-{gasCost})");
            }

            if (gasAvailable < gasCost)
            {
                Metrics.EvmExceptions++;
                return false;
            }

            gasAvailable -= gasCost;
            return true;
        }

        public void RefundGas(long refund, ref long gasAvailable)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"  UPDATE GAS (+{refund})");
            }

            gasAvailable += refund;
        }

        public CallResult ExecutePrecompile(EvmState state, IReleaseSpec spec)
        {
            byte[] callData = state.Env.InputData;
            BigInteger transferValue = state.Env.TransferValue;
            long gasAvailable = state.GasAvailable;

            BigInteger precompileId = state.Env.CodeInfo.PrecompileId;
            long baseGasCost = _precompiles[precompileId].BaseGasCost();
            long dataGasCost = _precompiles[precompileId].DataGasCost(callData);

            bool wasCreated = false;
            if (!_state.AccountExists(state.Env.ExecutingAccount))
            {
                wasCreated = true;
                _state.CreateAccount(state.Env.ExecutingAccount, transferValue);
            }
            else
            {
                _state.UpdateBalance(state.Env.ExecutingAccount, transferValue, spec);
            }

            if (gasAvailable < dataGasCost + baseGasCost)
            {
                // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-161.md
                // An additional issue was found in Parity,
                // where the Parity client incorrectly failed
                // to revert empty account deletions in a more limited set of contexts
                // involving out-of-gas calls to precompiled contracts;
                // the new Geth behavior matches Parity’s,
                // and empty accounts will cease to be a source of concern in general
                // in about one week once the state clearing process finishes.

                if (!wasCreated && transferValue.IsZero && spec.IsEip158Enabled)
                {
                    _parityTouchBugAccount = state.Env.ExecutingAccount;
                }

                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            //if(!UpdateGas(baseGasCost, ref gasAvailable)) return CallResult.Exception;
            //if(!UpdateGas(dataGasCost, ref gasAvailable)) return CallResult.Exception;
            if (!UpdateGas(baseGasCost, ref gasAvailable))
            {
                throw new OutOfGasException();
            }

            if (!UpdateGas(dataGasCost, ref gasAvailable))
            {
                throw new OutOfGasException();
            }

            state.GasAvailable = gasAvailable;

            try
            {
                (byte[] output, bool success) = _precompiles[precompileId].Run(callData);
                CallResult callResult = new CallResult(output);
                callResult.PrecompileSuccess = success;
                return callResult;
            }
            catch (Exception ex)
            {
                CallResult callResult = new CallResult(EmptyBytes);
                callResult.PrecompileSuccess = false;
                return callResult;
            }
        }

        public CallResult ExecuteCall(EvmState evmState, byte[] previousCallResult, byte[] previousCallOutput, BigInteger previousCallOutputDestination, IReleaseSpec spec)
        {
            ExecutionEnvironment env = evmState.Env;
            if (!evmState.IsContinuation)
            {
                if (!_state.AccountExists(env.ExecutingAccount))
                {
                    _state.CreateAccount(env.ExecutingAccount, env.TransferValue);
                }
                else
                {
                    _state.UpdateBalance(env.ExecutingAccount, env.TransferValue, spec);
                }

                if ((evmState.ExecutionType == ExecutionType.Create || evmState.ExecutionType == ExecutionType.DirectCreate) && spec.IsEip158Enabled)
                {
                    _state.IncrementNonce(env.ExecutingAccount);
                }
            }

            if (evmState.Env.CodeInfo.MachineCode == null || evmState.Env.CodeInfo.MachineCode.Length == 0)
            {
                return CallResult.Empty;
            }

            evmState.InitStacks();
            byte[][] bytesOnStack = evmState.BytesOnStack;
            BigInteger[] intsOnStack = evmState.IntsOnStack;
            bool[] intPositions = evmState.IntPositions;
            int stackHead = evmState.StackHead;
            long gasAvailable = evmState.GasAvailable;
            long programCounter = (long)evmState.ProgramCounter;
            byte[] code = env.CodeInfo.MachineCode;

            void UpdateCurrentState()
            {
                evmState.ProgramCounter = programCounter;
                evmState.GasAvailable = gasAvailable;
                evmState.StackHead = stackHead;
            }

            void StartInstructionTrace(Instruction instruction)
            {
                if (_trace == null)
                {
                    return;
                }

                Dictionary<string, string> previousStorage = _traceEntry?.Storage;
                _traceEntry = new TransactionTraceEntry();
                _traceEntry.Depth = env.CallDepth;
                _traceEntry.Gas = gasAvailable;
                _traceEntry.Operation = Enum.GetName(typeof(Instruction), instruction);
                _traceEntry.Memory = evmState.Memory.GetTrace();
                _traceEntry.Pc = programCounter;
                _traceEntry.Stack = GetStackTrace();
                if (previousStorage != null)
                {
                    foreach (KeyValuePair<string, string> storageEntry in previousStorage)
                    {
                        _traceEntry.Storage.Add(storageEntry.Key, storageEntry.Value);
                    }
                }
            }

            void EndInstructionTrace()
            {
                if (_trace != null)
                {
                    _traceEntry.GasCost = _traceEntry.Gas - gasAvailable;
                    _trace.Entries.Add(_traceEntry);
                }
            }

            void LogInstructionResult(Instruction instruction, long gasBefore)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug(
                        $"  END INST {_instructionCounter} {env.CallDepth}.{instruction} GAS {gasBefore} -> {gasAvailable} ({gasBefore - gasAvailable}) STACK {stackHead} MEMORY {evmState.Memory.Size / 32L} PC {programCounter}");

                    if (_logger.IsTraceEnabled)
                    {
                        for (int i = 0; i < Math.Min(stackHead, 7); i++)
                        {
                            if (intPositions[stackHead - i - 1])
                            {
                                _logger.Debug($"  STACK{i} -> {intsOnStack[stackHead - i - 1]}");
                            }
                            else
                            {
                                _logger.Debug($"  STACK{i} -> {Hex.FromBytes(bytesOnStack[stackHead - i - 1], true)}");
                            }
                        }
                    }
                }
            }

            void PushBytes(byte[] value)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  PUSH {Hex.FromBytes(value, true)}");
                }

                intPositions[stackHead] = false;
                bytesOnStack[stackHead] = value;
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushInt(BigInteger value)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  PUSH {value}");
                }

                intPositions[stackHead] = true;
                intsOnStack[stackHead] = value;
                stackHead++;
                if (stackHead >= MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PopLimbo()
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stackHead--;
            }

            void Dup(int depth)
            {
                if (stackHead < depth)
                {
                    Metrics.EvmExceptions++;
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
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void Swap(int depth)
            {
                if (stackHead < depth)
                {
                    Metrics.EvmExceptions++;
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
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stackHead--;

                byte[] result = intPositions[stackHead]
                    ? intsOnStack[stackHead].ToBigEndianByteArray()
                    : bytesOnStack[stackHead];

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  POP {Hex.FromBytes(result, true)}");
                }

                return result;
            }

            List<string> GetStackTrace()
            {
                List<string> stackTrace = new List<string>();
                for (int i = 0; i < stackHead; i++)
                {
                    byte[] stackItem = intPositions[i]
                        ? intsOnStack[i].ToBigEndianByteArray()
                        : bytesOnStack[i];

                    stackTrace.Add(new Hex(stackItem.PadLeft(32)));
                }

                return stackTrace;
            }

            BigInteger PopUInt()
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stackHead--;

                if (intPositions[stackHead])
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"  POP {intsOnStack[stackHead]}");
                    }

                    return intsOnStack[stackHead];
                }

                BigInteger res = bytesOnStack[stackHead].ToUnsignedBigInteger();
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  POP {res}");
                }

                return res;
            }

            BigInteger PopInt()
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new StackUnderflowException();
                }

                stackHead--;

                if (intPositions[stackHead])
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"  POP {intsOnStack[stackHead]}");
                    }

                    if (intsOnStack[stackHead] > P255Int)
                    {
                        return intsOnStack[stackHead] - P256Int;
                    }

                    return intsOnStack[stackHead];
                }

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  POP {Hex.FromBytes(bytesOnStack[stackHead], true)}");
                }

                return bytesOnStack[stackHead].ToSignedBigInteger(32);
            }

            Address PopAddress()
            {
                byte[] bytes = PopBytes();
                if (bytes.Length < 20)
                {
                    bytes = bytes.PadLeft(20);
                }

                return bytes.Length == 20 ? new Address(bytes) : new Address(bytes.Slice(bytes.Length - 20, 20));
            }

            void UpdateMemoryCost(BigInteger position, BigInteger length)
            {
                long memoryCost = evmState.Memory.CalculateMemoryCost(position, length);
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"  MEMORY COST {memoryCost}");
                }

                if (!UpdateGas(memoryCost, ref gasAvailable))
                {
                    throw new OutOfGasException();
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

            while (programCounter < code.Length)
            {
                long gasBefore = gasAvailable;

                Instruction instruction = (Instruction)code[(int)programCounter];
                if (_trace != null) // TODO: review local method and move them to separate classes where needed and better
                {
                    StartInstructionTrace(instruction);
                }

                _instructionCounter++;
                programCounter++;

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"{instruction} (0x{instruction:X})");
                }

                BigInteger bigReg;
                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        UpdateCurrentState();
                        EndInstructionTrace();
                        return CallResult.Empty;
                    }
                    case Instruction.ADD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger res = a + b;
                        PushInt(res >= P256Int ? res - P256Int : res);
                        break;
                    }
                    case Instruction.MUL:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(BigInteger.Remainder(a * b, P256Int));
                        break;
                    }
                    case Instruction.SUB:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(b == BigInteger.Zero ? BigInteger.Zero : BigInteger.Divide(a, b));
                        break;
                    }
                    case Instruction.SDIV:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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
                            PushBytes(BigInteger.Divide(a, b).ToBigEndianByteArray(32));
                        }

                        break;
                    }
                    case Instruction.MOD:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(b == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a, b));
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        if (b == BigInteger.Zero)
                        {
                            PushInt(BigInteger.Zero);
                        }
                        else
                        {
                            PushBytes((a.Sign * BigInteger.Remainder(a.Abs(), b.Abs()))
                                .ToBigEndianByteArray(32));
                        }

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger mod = PopUInt();
                        PushInt(mod == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a + b, mod));
                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        BigInteger mod = PopUInt();
                        PushInt(mod == BigInteger.Zero ? BigInteger.Zero : BigInteger.Remainder(a * b, mod));
                        break;
                    }
                    case Instruction.EXP:
                    {
                        if (!UpdateGas(GasCostOf.Exp, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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

                            if (!UpdateGas((spec.IsEip160Enabled ? GasCostOf.ExpByteEip160 : GasCostOf.ExpByte) * (1L + expSize), ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }
                        }
                        else
                        {
                            PushInt(BigInteger.One);
                            break;
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
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        if (a >= BigInt32)
                        {
                            break;
                        }

                        byte[] b = PopBytes();
                        BitArray bits1 = b.ToBigEndianBitArray256();
                        int bitPosition = Math.Max(0, 248 - 8 * (int)a);
                        bool isSet = bits1[bitPosition];
                        for (int i = 0; i < bitPosition; i++)
                        {
                            bits1[i] = isSet;
                        }

                        PushBytes(bits1.ToBytes());
                        break;
                    }
                    case Instruction.LT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(a < b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.GT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(a > b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SLT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        PushInt(BigInteger.Compare(a, b) < 0 ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.SGT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopInt();
                        BigInteger b = PopInt();
                        PushInt(a > b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.EQ:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        BigInteger b = PopUInt();
                        PushInt(a == b ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopInt();
                        PushInt(a.IsZero ? BigInteger.One : BigInteger.Zero);
                        break;
                    }
                    case Instruction.AND:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BitArray bits1 = PopBytes().ToBigEndianBitArray256();
                        BitArray bits2 = PopBytes().ToBigEndianBitArray256();
                        PushBytes(bits1.And(bits2).ToBytes());
                        break;
                    }
                    case Instruction.OR:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BitArray bits1 = PopBytes().ToBigEndianBitArray256();
                        BitArray bits2 = PopBytes().ToBigEndianBitArray256();
                        PushBytes(bits1.Or(bits2).ToBytes());
                        break;
                    }
                    case Instruction.XOR:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BitArray bits1 = PopBytes().ToBigEndianBitArray256();
                        BitArray bits2 = PopBytes().ToBigEndianBitArray256();
                        PushBytes(bits1.Xor(bits2).ToBytes());
                        break;
                    }
                    case Instruction.NOT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger position = PopUInt();
                        byte[] bytes = PopBytes();

                        if (position >= BigInt32)
                        {
                            PushBytes(BytesZero);
                            break;
                        }

                        int adjustedPosition = bytes.Length - 32 + (int)position;
                        PushInt(adjustedPosition < 0 ? BigInteger.Zero : new BigInteger(bytes[adjustedPosition]));
                        //PushBytes(adjustedPosition < 0 ? BytesZero : bytes.Slice(adjustedPosition, 1));
                        break;
                    }
                    case Instruction.SHA3:
                    {
                        BigInteger memSrc = PopUInt();
                        BigInteger memLength = PopUInt();
                        if (!UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmMemory.Div32Ceiling(memLength),
                            ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(memSrc, memLength);

                        byte[] memData = evmState.Memory.Load(memSrc, memLength);
                        PushBytes(Keccak.Compute(memData).Bytes);
                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(env.ExecutingAccount.Hex);
                        break;
                    }
                    case Instruction.BALANCE:
                    {
                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.BalanceEip150 : GasCostOf.Balance, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Address address = PopAddress();
                        BigInteger balance = _state.GetBalance(address);
                        PushInt(balance);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(env.Sender.Hex);
                        break;
                    }
                    case Instruction.CALLVALUE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.Value);
                        break;
                    }
                    case Instruction.ORIGIN:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(env.Originator.Hex);
                        break;
                    }
                    case Instruction.CALLDATALOAD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger src = PopUInt();
                        PushBytes(env.InputData.SliceWithZeroPadding(src, 32));
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.InputData.Length);
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length),
                            ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dest, length);

                        byte[] callDataSlice = env.InputData.SliceWithZeroPadding(src, (int)length);
                        evmState.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.CODESIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(code.Length);
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length), ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dest, length);
                        byte[] callDataSlice = code.SliceWithZeroPadding(src, (int)length);
                        evmState.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.GASPRICE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.GasPrice);
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.ExtCodeSizeEip150 : GasCostOf.ExtCodeSize, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Address address = PopAddress();
                        byte[] accountCode = GetCachedCodeInfo(address)?.MachineCode;
                        PushInt(accountCode?.Length ?? BigInteger.Zero);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        Address address = PopAddress();
                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        if (!UpdateGas((spec.IsEip150Enabled ? GasCostOf.ExtCodeEip150 : GasCostOf.ExtCode) + GasCostOf.Memory * EvmMemory.Div32Ceiling(length),
                            ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dest, length);
                        byte[] externalCode = GetCachedCodeInfo(address)?.MachineCode;
                        byte[] callDataSlice = externalCode.SliceWithZeroPadding(src, (int)length);
                        evmState.Memory.Save(dest, callDataSlice);
                        break;
                    }
                    case Instruction.RETURNDATASIZE:
                    {
                        if (!spec.IsEip211Enabled)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(_returnDataBuffer.Length);
                        break;
                    }
                    case Instruction.RETURNDATACOPY:
                    {
                        if (!spec.IsEip211Enabled)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidInstructionException;
                        }

                        BigInteger dest = PopUInt();
                        BigInteger src = PopUInt();
                        BigInteger length = PopUInt();
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmMemory.Div32Ceiling(length), ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dest, length);

                        if (src + length > _returnDataBuffer.Length)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.AccessViolationException;
                        }

                        byte[] returnDataSlice = _returnDataBuffer.SliceWithZeroPadding(src, (int)length);
                        evmState.Memory.Save(dest, returnDataSlice);
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        if (!UpdateGas(GasCostOf.BlockHash, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger a = PopUInt();
                        PushBytes(_blockhashProvider.GetBlockhash(env.CurrentBlock, a)?.Bytes ?? BytesZero);

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(env.CurrentBlock.Beneficiary.Hex);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.CurrentBlock.Difficulty);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.CurrentBlock.Timestamp);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.CurrentBlock.Number);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(env.CurrentBlock.GasLimit);
                        break;
                    }
                    case Instruction.POP:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PopLimbo();
                        break;
                    }
                    case Instruction.MLOAD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger memPosition = PopUInt();
                        UpdateMemoryCost(memPosition, BigInt32);
                        byte[] memData = evmState.Memory.Load(memPosition);
                        PushBytes(memData);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger memPosition = PopUInt();
                        byte[] data = PopBytes();
                        UpdateMemoryCost(memPosition, BigInt32);
                        evmState.Memory.SaveWord(memPosition, data);
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger memPosition = PopUInt();
                        byte[] data = PopBytes();
                        UpdateMemoryCost(memPosition, BigInteger.One);
                        evmState.Memory.SaveByte(memPosition, data);
                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.SLoadEip150 : GasCostOf.SLoad, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        BigInteger storageIndex = PopUInt();
                        byte[] value = _storage.Get(new StorageAddress(env.ExecutingAccount, storageIndex));
                        PushBytes(value);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        if (evmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        BigInteger storageIndex = PopUInt();
                        byte[] data = PopBytes().WithoutLeadingZeros();

                        bool isNewValueZero = data.IsZero();

                        StorageAddress storageAddress = new StorageAddress(env.ExecutingAccount, storageIndex);
                        byte[] previousValue = _storage.Get(storageAddress);

                        bool isValueChanged = !(isNewValueZero && previousValue.IsZero()) ||
                                              !Bytes.UnsafeCompare(previousValue, data);


                        if (isNewValueZero)
                        {
                            if (!UpdateGas(GasCostOf.SReset, ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }

                            if (isValueChanged)
                            {
                                evmState.Refund += RefundOf.SClear;
                            }
                        }
                        else
                        {
                            if (!UpdateGas(previousValue.IsZero() ? GasCostOf.SSet : GasCostOf.SReset, ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }
                        }

                        if (isValueChanged)
                        {
                            byte[] newValue = isNewValueZero ? BytesZero : data;
                            _storage.Set(storageAddress, newValue);
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"  UPDATING STORAGE: {env.ExecutingAccount} {storageIndex} {Hex.FromBytes(newValue, true)}");
                            }
                        }

                        if (_trace != null)
                        {
                            _traceEntry.Storage[new Hex(storageIndex.ToBigEndianByteArray().PadLeft(32))] = new Hex(data.PadLeft(32));
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        bigReg = PopUInt();
                        if (bigReg > BigIntMaxInt)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidJumpDestination;
                        }

                        int dest = (int)bigReg;
                        env.CodeInfo.ValidateJump(dest);

                        programCounter = dest;
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        bigReg = PopUInt();
                        if (bigReg > BigIntMaxInt)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidJumpDestination;
                        }

                        int dest = (int)bigReg;
                        BigInteger condition = PopUInt();
                        if (condition > BigInteger.Zero)
                        {
                            if (!env.CodeInfo.ValidateJump(dest))
                            {
                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
                            }

                            programCounter = dest;
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(programCounter - 1L);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(evmState.Memory.Size);
                        break;
                    }
                    case Instruction.GAS:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        PushInt(gasAvailable);
                        break;
                    }
                    case Instruction.JUMPDEST:
                    {
                        if (!UpdateGas(GasCostOf.JumpDest, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        break;
                    }
                    case Instruction.PUSH1:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        BigInteger memoryPos = PopUInt();
                        BigInteger length = PopUInt();
                        long topicsCount = instruction - Instruction.LOG0;
                        UpdateMemoryCost(memoryPos, length);
                        if (!UpdateGas(
                            GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                            (long)length * GasCostOf.LogData, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

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
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
                        if (!_state.AccountExists(env.ExecutingAccount))
                        {
                            _state.CreateAccount(env.ExecutingAccount, BigInteger.Zero);
                        }

                        BigInteger value = PopUInt();
                        BigInteger memoryPositionOfInitCode = PopUInt();
                        BigInteger initCodeLength = PopUInt();

                        if (!UpdateGas(GasCostOf.Create, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(memoryPositionOfInitCode, initCodeLength);

                        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
                        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
                        {
                            // TODO: need a test for this
                            _returnDataBuffer = EmptyBytes;
                            PushInt(BigInteger.Zero);
                            break;
                        }

                        byte[] initCode = evmState.Memory.Load(memoryPositionOfInitCode, initCodeLength);
                        Keccak contractAddressKeccak =
                            Keccak.Compute(
                                Rlp.Encode(
                                    Rlp.Encode(env.ExecutingAccount),
                                    Rlp.Encode(_state.GetNonce(env.ExecutingAccount))));
                        Address contractAddress = new Address(contractAddressKeccak);

                        BigInteger balance = _state.GetBalance(env.ExecutingAccount);
                        if (value > _state.GetBalance(env.ExecutingAccount))
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"Insufficient balance when calling create - value = {value} > {balance} = balance");
                            }

                            PushInt(BigInteger.Zero);
                            break;
                        }

                        _state.IncrementNonce(env.ExecutingAccount);

                        long callGas = spec.IsEip150Enabled ? gasAvailable - gasAvailable / 64L : gasAvailable;
                        if (!UpdateGas(callGas, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        bool accountExists = _state.AccountExists(contractAddress);
                        if (accountExists && ((GetCachedCodeInfo(contractAddress)?.MachineCode?.Length ?? 0) != 0 || _state.GetNonce(contractAddress) != 0))
                        {
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug($"Contract collision at {contractAddress}"); // the account already owns the contract with the code
                            }

                            PushInt(BigInteger.Zero); // TODO: this push 0 approach should be replaced with some proper approach to call result
                            break;
                        }

                        int stateSnapshot = _state.TakeSnapshot();
                        int storageSnapshot = _storage.TakeSnapshot();

                        _state.UpdateBalance(env.ExecutingAccount, -value, spec);
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug("  INIT: " + contractAddress);
                        }

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.TransferValue = value;
                        callEnv.Value = value;
                        callEnv.Sender = env.ExecutingAccount;
                        callEnv.Originator = env.Originator;
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.ExecutingAccount = contractAddress;
                        callEnv.CodeInfo = new CodeInfo(initCode);
                        callEnv.InputData = Bytes.Empty;
                        EvmState callState = new EvmState(
                            callGas,
                            callEnv,
                            ExecutionType.Create,
                            stateSnapshot,
                            storageSnapshot,
                            0L,
                            0L,
                            evmState.IsStatic,
                            false);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        if (_logger.IsDebugEnabled)
                        {
                            LogInstructionResult(instruction, gasBefore);
                        }

                        return new CallResult(callState);
                    }
                    case Instruction.RETURN:
                    {
                        BigInteger memoryPos = PopUInt();
                        BigInteger length = PopUInt();

                        UpdateMemoryCost(memoryPos, length);
                        byte[] returnData = evmState.Memory.Load(memoryPos, length);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        if (_logger.IsDebugEnabled)
                        {
                            LogInstructionResult(instruction, gasBefore);
                        }

                        return new CallResult(returnData);
                    }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    case Instruction.STATICCALL:
                    {
                        if (instruction == Instruction.DELEGATECALL && !spec.IsEip7Enabled ||
                            instruction == Instruction.STATICCALL && !spec.IsEip214Enabled)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidInstructionException;
                        }

                        BigInteger gasLimit = PopUInt();
                        Address codeSource = PopAddress();
                        BigInteger callValue;
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
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        bool isPrecompile = codeSource.IsPrecompiled(spec);
                        Address sender = instruction == Instruction.DELEGATECALL ? env.Sender : env.ExecutingAccount;
                        Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL ? codeSource : env.ExecutingAccount;

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"  SENDER {sender}");
                            _logger.Debug($"  CODE SOURCE {codeSource}");
                            _logger.Debug($"  TARGET {target}");
                            _logger.Debug($"  VALUE {callValue}");
                            _logger.Debug($"  TRANSFER_VALUE {transferValue}");
                        }

                        long gasExtra = 0L;

                        if (!transferValue.IsZero)
                        {
                            gasExtra += GasCostOf.CallValue;
                        }

                        if (!spec.IsEip158Enabled && !_state.AccountExists(target))
                        {
                            gasExtra += GasCostOf.NewAccount;
                        }

                        if (spec.IsEip158Enabled && transferValue != 0 && _state.IsDeadAccount(target))
                        {
                            gasExtra += GasCostOf.NewAccount;
                        }

                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.CallOrCallCodeEip150 : GasCostOf.CallOrCallCode, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(dataOffset, dataLength);
                        UpdateMemoryCost(outputOffset, outputLength);
                        if (!UpdateGas(gasExtra, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        if (spec.IsEip150Enabled)
                        {
                            gasLimit = BigInteger.Min(gasAvailable - gasAvailable / 64L, gasLimit);
                        }

                        long gasLimitUl = (long)gasLimit;
                        if (!UpdateGas(gasLimitUl, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        if (!transferValue.IsZero)
                        {
                            gasLimitUl += GasCostOf.CallStipend;
                        }

                        if (env.CallDepth >= MaxCallDepth || !transferValue.IsZero && _state.GetBalance(env.ExecutingAccount) < transferValue)
                        {
                            RefundGas(gasLimitUl, ref gasAvailable);
                            //evmState.Memory.Save(outputOffset, new byte[(int)outputLength]); // TODO: probably should not save memory here
                            _returnDataBuffer = EmptyBytes;
                            PushInt(BigInteger.Zero);
                            if (_logger.IsDebugEnabled)
                            {
                                _logger.Debug("  FAIL - CALL DEPTH");
                            }

                            break;
                        }

                        byte[] callData = evmState.Memory.Load(dataOffset, dataLength);
                        int stateSnapshot = _state.TakeSnapshot();
                        int storageSnapshot = _storage.TakeSnapshot();
                        _state.UpdateBalance(sender, -transferValue, spec);

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
                        callEnv.CodeInfo = isPrecompile ? new CodeInfo(codeSource) : GetCachedCodeInfo(codeSource);

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"  CALL_GAS {gasLimitUl}");
                        }

                        EvmState callState = new EvmState(
                            gasLimitUl,
                            callEnv,
                            isPrecompile ? ExecutionType.Precompile : (instruction == Instruction.CALL || instruction == Instruction.STATICCALL ? ExecutionType.Call : ExecutionType.Callcode),
                            stateSnapshot,
                            storageSnapshot,
                            (long)outputOffset,
                            (long)outputLength,
                            instruction == Instruction.STATICCALL || evmState.IsStatic,
                            false);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        if (_logger.IsDebugEnabled)
                        {
                            LogInstructionResult(instruction, gasBefore);
                        }

                        return new CallResult(callState);
                    }
                    case Instruction.REVERT:
                    {
                        if (!spec.IsEip140Enabled)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.InvalidInstructionException;
                        }

                        long gasCost = GasCostOf.Zero;
                        BigInteger memoryPos = PopUInt();
                        BigInteger length = PopUInt();

                        if (!UpdateGas(gasCost, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(memoryPos, length);
                        byte[] errorDetails = evmState.Memory.Load(memoryPos, length);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        if (_logger.IsDebugEnabled)
                        {
                            LogInstructionResult(instruction, gasBefore);
                        }

                        return new CallResult(errorDetails, true);
                    }
                    case Instruction.INVALID:
                    {
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        EndInstructionTrace();
                        Metrics.EvmExceptions++;
                        return CallResult.InvalidInstructionException;
                    }
                    case Instruction.SELFDESTRUCT:
                    {
                        if (evmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.SelfDestructEip150 : GasCostOf.SelfDestruct, ref gasAvailable))
                        {
                            return CallResult.OutOfGasException;
                        }

                        Address inheritor = PopAddress();
                        if (!evmState.DestroyList.Contains(env.ExecutingAccount))
                        {
                            evmState.DestroyList.Add(env.ExecutingAccount);
                        }

                        // TODO: review the change for Ropsten 468194 (lots of reciprocated selfdestruct calls)

                        BigInteger ownerBalance = _state.GetBalance(env.ExecutingAccount);
                        bool inheritorAccountExists = _state.AccountExists(inheritor);

                        if (!spec.IsEip158Enabled && !inheritorAccountExists && spec.IsEip150Enabled)
                        {
                            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }
                        }

                        if (spec.IsEip158Enabled && ownerBalance != 0 && _state.IsDeadAccount(inheritor))
                        {
                            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                            {
                                return CallResult.OutOfGasException;
                            }
                        }

                        if (!inheritorAccountExists)
                        {
                            _state.CreateAccount(inheritor, ownerBalance);
                        }
                        else if (!inheritor.Equals(env.ExecutingAccount))
                        {
                            _state.UpdateBalance(inheritor, ownerBalance, spec);
                        }

                        _state.UpdateBalance(env.ExecutingAccount, -ownerBalance, spec);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        if (_logger.IsDebugEnabled)
                        {
                            LogInstructionResult(instruction, gasBefore);
                        }

                        return CallResult.Empty;
                    }
                    default:
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug("UNKNOWN INSTRUCTION");
                        }

                        EndInstructionTrace();
                        Metrics.EvmExceptions++;
                        return CallResult.InvalidInstructionException;
                    }
                }

                EndInstructionTrace();
                if (_logger.IsDebugEnabled)
                {
                    LogInstructionResult(instruction, gasBefore);
                }
            }

            UpdateCurrentState();
            return CallResult.Empty;
        }
    }

    public class CallResult
    {
        public static CallResult Exception = new CallResult(StatusCode.FailureBytes) {IsException = true};
        public static CallResult OutOfGasException = Exception;
        public static CallResult AccessViolationException = Exception;
        public static CallResult InvalidJumpDestination = Exception;
        public static CallResult InvalidInstructionException = Exception;
        public static CallResult StaticCallViolationException = Exception;
        public static CallResult StackOverflowException = Exception; // TODO: use these to avoid CALL POP attacks
        public static CallResult StackUnderflowException = Exception; // TODO: use these to avoid CALL POP attacks
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

        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess { get; set; } // TODO: check this behaviour as it seems it is required and previously that was not the case

        public EvmState StateToExecute { get; }
        public byte[] Output { get; } = Bytes.Empty;
        public bool IsReturn => StateToExecute == null;
        public bool IsException { get; set; }
    }
}