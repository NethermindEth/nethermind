﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.Evm
{
    public class VirtualMachine : IVirtualMachine
    {
        public const int MaxCallDepth = 1024;

        private bool _simdOperationsEnabled = Vector<byte>.Count == 32;
        private BigInteger P255Int = BigInteger.Pow(2, 255);
        private BigInteger P256Int = BigInteger.Pow(2, 256);
        private BigInteger P255 => P255Int;
        private BigInteger BigInt256 = 256;
        public BigInteger BigInt32 = 32;

        internal byte[] BytesZero = {0};

        internal byte[] BytesZero32 =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        };

        internal byte[] BytesMax32 =
        {
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255
        };

        private byte[] _chainId;

        private readonly IBlockhashProvider _blockhashProvider;
        private readonly ISpecProvider _specProvider;
        private static readonly ICache<Keccak, CodeInfo> _codeCache = new LruCacheWithRecycling<Keccak, CodeInfo>(MemoryAllowance.CodeCacheSize, MemoryAllowance.CodeCacheSize, "VM bytecodes");
        private readonly ILogger _logger;
        private readonly IStateProvider _state;
        private readonly Stack<EvmState> _stateStack = new Stack<EvmState>();
        private readonly IStorageProvider _storage;
        private Address _parityTouchBugAccount;
        private Dictionary<Address, CodeInfo> _precompiles { get; set; }
        private byte[] _returnDataBuffer = new byte[0];
        private ITxTracer _txTracer;

        public VirtualMachine(IStateProvider stateProvider, IStorageProvider storageProvider,
            IBlockhashProvider blockhashProvider, ISpecProvider specProvider, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _state = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _storage = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
            _blockhashProvider = blockhashProvider ?? throw new ArgumentNullException(nameof(blockhashProvider));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _chainId = specProvider.ChainId.ToBigEndianByteArray();
            InitializePrecompiledContracts();
        }

        // can refactor and integrate the other call
        public TransactionSubstate Run(EvmState state, ITxTracer txTracer)
        {
            _txTracer = txTracer;

            IReleaseSpec spec = _specProvider.GetSpec(state.Env.CurrentBlock.Number);
            EvmState currentState = state;
            byte[] previousCallResult = null;
            ZeroPaddedSpan previousCallOutput = ZeroPaddedSpan.Empty;
            UInt256 previousCallOutputDestination = UInt256.Zero;
            while (true)
            {
                if (!currentState.IsContinuation)
                {
                    _returnDataBuffer = Bytes.Empty;
                }

                try
                {
                    CallResult callResult;
                    if (currentState.IsPrecompile)
                    {
                        if (_txTracer.IsTracingActions)
                        {
                            _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.Env.InputData, currentState.ExecutionType, true);
                        }

                        callResult = ExecutePrecompile(currentState, spec);

                        if (!callResult.PrecompileSuccess.Value)
                        {
                            if (currentState.IsPrecompile && currentState.IsTopLevel)
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
                        
                        if (_txTracer.IsTracingActions && !currentState.IsContinuation)
                        {
                            _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.ExecutionType.IsAnyCreate() ? currentState.Env.CodeInfo.MachineCode : currentState.Env.InputData, currentState.ExecutionType);
                            if (_txTracer.IsTracingCode) _txTracer.ReportByteCode(currentState.Env.CodeInfo.MachineCode);
                        }

                        callResult = ExecuteCall(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                        if (!callResult.IsReturn)
                        {
                            _stateStack.Push(currentState);
                            currentState = callResult.StateToExecute;
                            previousCallResult = null; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests (failing block 9411 on Ropsten https://ropsten.etherscan.io/vmtrace?txhash=0x666194d15c14c54fffafab1a04c08064af165870ef9a87f65711dcce7ed27fe1)
                            _returnDataBuffer = Bytes.Empty;
                            previousCallOutput = ZeroPaddedSpan.Empty;
                            continue;
                        }

                        if (callResult.IsException)
                        {
                            if (_txTracer.IsTracingActions) _txTracer.ReportActionError(callResult.ExceptionType);
                            _state.Restore(currentState.StateSnapshot);
                            _storage.Restore(currentState.StorageSnapshot);

                            if (_parityTouchBugAccount != null)
                            {
                                _state.AddToBalance(_parityTouchBugAccount, UInt256.Zero, spec);
                                _parityTouchBugAccount = null;
                            }

                            if (currentState.IsTopLevel)
                            {
                                return new TransactionSubstate(callResult.ExceptionType);
                            }

                            previousCallResult = StatusCode.FailureBytes;
                            previousCallOutputDestination = UInt256.Zero;
                            _returnDataBuffer = Bytes.Empty;
                            previousCallOutput = ZeroPaddedSpan.Empty;

                            currentState.Dispose();
                            currentState = _stateStack.Pop();
                            currentState.IsContinuation = true;
                            continue;
                        }
                    }

                    if (currentState.IsTopLevel)
                    {
                        if (_txTracer.IsTracingActions)
                        {
                            if (callResult.IsException)
                            {
                                _txTracer.ReportActionError(callResult.ExceptionType);
                            }
                            else if (callResult.ShouldRevert)
                            {
                                _txTracer.ReportActionError(EvmExceptionType.Revert);
                            }
                            else
                            {
                                long codeDepositGasCost = CodeDepositHandler.CalculateCost(callResult.Output.Length, spec);
                                if (currentState.ExecutionType.IsAnyCreate() && currentState.GasAvailable < codeDepositGasCost)
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                                }
                                else
                                {
                                    if (currentState.ExecutionType.IsAnyCreate())
                                    {
                                        _txTracer.ReportActionEnd(currentState.GasAvailable - codeDepositGasCost, currentState.To, callResult.Output);
                                    }
                                    else
                                    {
                                        _txTracer.ReportActionEnd(currentState.GasAvailable, _returnDataBuffer);
                                    }
                                }
                            }
                        }

                        return new TransactionSubstate(callResult.Output, currentState.Refund, currentState.DestroyList, currentState.Logs, callResult.ShouldRevert);
                    }

                    Address callCodeOwner = currentState.Env.ExecutingAccount;
                    EvmState previousState = currentState;
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                    currentState.GasAvailable += previousState.GasAvailable;
                    bool previousStateSucceeded = true;

                    if (!callResult.ShouldRevert)
                    {
                        long gasAvailableForCodeDeposit = previousState.GasAvailable; // TODO: refactor, this is to fix 61363 Ropsten
                        if (previousState.ExecutionType.IsAnyCreate())
                        {
                            previousCallResult = callCodeOwner.Bytes;
                            previousCallOutputDestination = UInt256.Zero;
                            _returnDataBuffer = Bytes.Empty;
                            previousCallOutput = ZeroPaddedSpan.Empty;

                            long codeDepositGasCost = CodeDepositHandler.CalculateCost(callResult.Output.Length, spec);
                            if (gasAvailableForCodeDeposit >= codeDepositGasCost)
                            {
                                Keccak codeHash = _state.UpdateCode(callResult.Output);
                                _state.UpdateCodeHash(callCodeOwner, codeHash, spec);
                                currentState.GasAvailable -= codeDepositGasCost;

                                if (_txTracer.IsTracingActions)
                                {
                                    _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output);
                                }
                            }
                            else
                            {
                                if (spec.IsEip2Enabled)
                                {
                                    currentState.GasAvailable -= gasAvailableForCodeDeposit;
                                    _state.Restore(previousState.StateSnapshot);
                                    _storage.Restore(previousState.StorageSnapshot);
                                    if (!previousState.IsCreateOnPreExistingAccount)
                                    {
                                        _state.DeleteAccount(callCodeOwner);
                                    }

                                    previousCallResult = BytesZero;
                                    previousStateSucceeded = false;

                                    if (_txTracer.IsTracingActions)
                                    {
                                        _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _returnDataBuffer = callResult.Output;
                            previousCallResult = callResult.PrecompileSuccess.HasValue ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes) : StatusCode.SuccessBytes;
                            previousCallOutput = callResult.Output.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int) previousState.OutputLength));
                            previousCallOutputDestination = (ulong) previousState.OutputDestination;
                            if (previousState.IsPrecompile)
                            {
                                // parity induced if else for vmtrace
                                if (_txTracer.IsTracingInstructions)
                                {
                                    _txTracer.ReportMemoryChange((long) previousCallOutputDestination, previousCallOutput.ToArray());
                                }
                            }

                            if (_txTracer.IsTracingActions)
                            {
                                _txTracer.ReportActionEnd(previousState.GasAvailable, _returnDataBuffer);
                            }
                        }

                        if (previousStateSucceeded)
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
                        }
                    }
                    else
                    {
                        _state.Restore(previousState.StateSnapshot);
                        _storage.Restore(previousState.StorageSnapshot);
                        _returnDataBuffer = callResult.Output;
                        previousCallResult = StatusCode.FailureBytes;
                        previousCallOutput = callResult.Output.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int) previousState.OutputLength));
                        previousCallOutputDestination = (ulong) previousState.OutputDestination;


                        if (_txTracer.IsTracingActions)
                        {
                            _txTracer.ReportActionError(EvmExceptionType.Revert);
                        }
                    }

                    previousState.Dispose();
                }
                catch (Exception ex) when (ex is EvmException || ex is OverflowException)
                {
                    if (_logger.IsTrace) _logger.Trace($"exception ({ex.GetType().Name}) in {currentState.ExecutionType} at depth {currentState.Env.CallDepth} - restoring snapshot");

                    _state.Restore(currentState.StateSnapshot);
                    _storage.Restore(currentState.StorageSnapshot);

                    if (_parityTouchBugAccount != null)
                    {
                        _state.AddToBalance(_parityTouchBugAccount, UInt256.Zero, spec);
                        _parityTouchBugAccount = null;
                    }

                    if (txTracer.IsTracingInstructions)
                    {
                        txTracer.ReportOperationError((ex as EvmException)?.ExceptionType ?? EvmExceptionType.Other);
                        txTracer.ReportOperationRemainingGas(0);
                    }

                    if (_txTracer.IsTracingActions)
                    {
                        EvmException evmException = ex as EvmException;
                        _txTracer.ReportActionError(evmException?.ExceptionType ?? EvmExceptionType.Other);
                    }

                    if (currentState.IsTopLevel)
                    {
                        return new TransactionSubstate(ex is OverflowException ? EvmExceptionType.Other : (ex as EvmException).ExceptionType);
                    }

                    previousCallResult = StatusCode.FailureBytes;
                    previousCallOutputDestination = UInt256.Zero;
                    _returnDataBuffer = Bytes.Empty;
                    previousCallOutput = new ZeroPaddedSpan(Span<byte>.Empty, 0);

                    currentState.Dispose();
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                }
            }
        }

        public CodeInfo GetCachedCodeInfo(Address codeSource, IReleaseSpec releaseSpec)
        {
            if (codeSource.IsPrecompiled(releaseSpec))
            {
                return _precompiles[codeSource];
            }
            
            Keccak codeHash = _state.GetCodeHash(codeSource);
            CodeInfo cachedCodeInfo = _codeCache.Get(codeHash);
            if (cachedCodeInfo == null)
            {
                byte[] code = _state.GetCode(codeHash);
                if (code == null)
                {
                    throw new NullReferenceException($"Code {codeHash} missing in the state for address {codeSource}");
                }

                cachedCodeInfo = new CodeInfo(code);
                _codeCache.Set(codeHash, cachedCodeInfo);
            }

            return cachedCodeInfo;
        }

        public void DisableSimdInstructions()
        {
            _simdOperationsEnabled = false;
        }

        private void InitializePrecompiledContracts()
        {
            _precompiles = new Dictionary<Address, CodeInfo>
            {
                [EcRecoverPrecompiledContract.Instance.Address] = new CodeInfo(EcRecoverPrecompiledContract.Instance),
                [Sha256PrecompiledContract.Instance.Address] = new CodeInfo(Sha256PrecompiledContract.Instance),
                [Ripemd160PrecompiledContract.Instance.Address] = new CodeInfo(Ripemd160PrecompiledContract.Instance),
                [IdentityPrecompiledContract.Instance.Address] = new CodeInfo(IdentityPrecompiledContract.Instance),
                [Bn128AddPrecompiledContract.Instance.Address] = new CodeInfo(Bn128AddPrecompiledContract.Instance),
                [Bn128MulPrecompiledContract.Instance.Address] = new CodeInfo(Bn128MulPrecompiledContract.Instance),
                [Bn128PairingPrecompiledContract.Instance.Address] = new CodeInfo(Bn128PairingPrecompiledContract.Instance),
                [ModExpPrecompiledContract.Instance.Address] = new CodeInfo(ModExpPrecompiledContract.Instance),
                [Blake2BPrecompiledContract.Instance.Address] = new CodeInfo(Blake2BPrecompiledContract.Instance),
            };
        }

        private bool UpdateGas(long gasCost, ref long gasAvailable)
        {
            if (gasAvailable < gasCost)
            {
                return false;
            }

            gasAvailable -= gasCost;
            return true;
        }

        private void UpdateGasUp(long refund, ref long gasAvailable)
        {
            gasAvailable += refund;
        }

        private CallResult ExecutePrecompile(EvmState state, IReleaseSpec spec)
        {
            byte[] callData = state.Env.InputData;
            UInt256 transferValue = state.Env.TransferValue;
            long gasAvailable = state.GasAvailable;

            IPrecompiledContract precompile = state.Env.CodeInfo.PrecompiledContract;
            long baseGasCost = precompile.BaseGasCost(spec);
            long dataGasCost = precompile.DataGasCost(callData, spec);

            bool wasCreated = false;
            if (!_state.AccountExists(state.Env.ExecutingAccount))
            {
                wasCreated = true;
                _state.CreateAccount(state.Env.ExecutingAccount, transferValue);
            }
            else
            {
                _state.AddToBalance(state.Env.ExecutingAccount, transferValue, spec);
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

            //if(!UpdateGas(dataGasCost, ref gasAvailable)) return CallResult.Exception;
            if (!UpdateGas(baseGasCost, ref gasAvailable))
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            if (!UpdateGas(dataGasCost, ref gasAvailable))
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            state.GasAvailable = gasAvailable;

            try
            {
                (byte[] output, bool success) = precompile.Run(callData);
                CallResult callResult = new CallResult(output, success);
                return callResult;
            }
            catch (Exception)
            {
                CallResult callResult = new CallResult(new byte[0], false);
                return callResult;
            }
        }

        private CallResult ExecuteCall(EvmState vmState, byte[] previousCallResult, ZeroPaddedSpan previousCallOutput, in UInt256 previousCallOutputDestination, IReleaseSpec spec)
        {
            bool isTrace = _logger.IsTrace;
            bool traceOpcodes = _txTracer.IsTracingInstructions;
            ExecutionEnvironment env = vmState.Env;
            if (!vmState.IsContinuation)
            {
                if (!_state.AccountExists(env.ExecutingAccount))
                {
                    _state.CreateAccount(env.ExecutingAccount, env.TransferValue);
                }
                else
                {
                    _state.AddToBalance(env.ExecutingAccount, env.TransferValue, spec);
                }

                if (vmState.ExecutionType.IsAnyCreate() && spec.IsEip158Enabled)
                {
                    _state.IncrementNonce(env.ExecutingAccount);
                }
            }

            if (vmState.Env.CodeInfo.MachineCode.Length == 0)
            {
                return CallResult.Empty;
            }

            vmState.InitStacks();
            EvmStack stack = new EvmStack(vmState.DataStack.AsSpan(), vmState.DataStackHead, _txTracer);
            int stackHead = vmState.DataStackHead;
            long gasAvailable = vmState.GasAvailable;
            int programCounter = vmState.ProgramCounter;
            Span<byte> code = env.CodeInfo.MachineCode.AsSpan();

            static void UpdateCurrentState(EvmState state, in int pc, in long gas, in int stackHead)
            {
                state.ProgramCounter = pc;
                state.GasAvailable = gas;
                state.DataStackHead = stackHead;
            }

            void StartInstructionTrace(Instruction instruction, EvmStack stackValue)
            {
                _txTracer.StartOperation(env.CallDepth + 1, gasAvailable, instruction, programCounter);
                if (_txTracer.IsTracingMemory)
                {
                    _txTracer.SetOperationMemory(vmState.Memory.GetTrace());
                }

                if (_txTracer.IsTracingStack)
                {
                    _txTracer.SetOperationStack(stackValue.GetStackTrace());
                }
            }

            void EndInstructionTrace()
            {
                if (traceOpcodes)
                {
                    if (_txTracer.IsTracingMemory)
                    {
                        _txTracer.SetOperationMemorySize(vmState.Memory.Size);
                    }

                    _txTracer.ReportOperationRemainingGas(gasAvailable);
                }
            }
            
            void EndInstructionTraceError(EvmExceptionType evmExceptionType)
            {
                if (traceOpcodes)
                {
                    _txTracer.ReportOperationError(evmExceptionType);
                    _txTracer.ReportOperationRemainingGas(gasAvailable);
                }
            }

            void Jump(in UInt256 jumpDest, bool isSubroutine = false)
            {
                if (jumpDest > int.MaxValue)
                {
                    Metrics.EvmExceptions++;
                    EndInstructionTraceError(EvmExceptionType.InvalidJumpDestination);
                    // https://github.com/NethermindEth/nethermind/issues/140
                    throw new InvalidJumpDestinationException();
//                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
                }

                int jumpDestInt = (int) jumpDest;

                if (!env.CodeInfo.ValidateJump(jumpDestInt, isSubroutine))
                {
                    EndInstructionTraceError(EvmExceptionType.InvalidJumpDestination);
                    // https://github.com/NethermindEth/nethermind/issues/140
                    throw new InvalidJumpDestinationException();
//                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
                }

                programCounter = jumpDestInt;
            }

            void UpdateMemoryCost(ref UInt256 position, in UInt256 length)
            {
                long memoryCost = vmState.Memory.CalculateMemoryCost(ref position, length);
                if (memoryCost != 0L)
                {
                    if (!UpdateGas(memoryCost, ref gasAvailable))
                    {
                        Metrics.EvmExceptions++;
                        EndInstructionTraceError(EvmExceptionType.OutOfGas);
                        throw new OutOfGasException();
                    }
                }
            }

            if (previousCallResult != null)
            {
                stack.PushBytes(previousCallResult);
                if (_txTracer.IsTracingInstructions) _txTracer.ReportOperationRemainingGas(vmState.GasAvailable);
            }

            if (previousCallOutput.Length > 0)
            {
                UInt256 localPreviousDest = previousCallOutputDestination;
                UpdateMemoryCost(ref localPreviousDest, (ulong) previousCallOutput.Length);
                vmState.Memory.Save(ref localPreviousDest, previousCallOutput);
//                if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)localPreviousDest, previousCallOutput);
            }

            while (programCounter < code.Length)
            {
                Instruction instruction = (Instruction) code[programCounter];
                if (traceOpcodes)
                {
                    StartInstructionTrace(instruction, stack);
                }

                programCounter++;
                switch (instruction)
                {
                    case Instruction.STOP:
                    {
                        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                        EndInstructionTrace();
                        return CallResult.Empty;
                    }
                    case Instruction.ADD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 b);
                        stack.PopUInt256(out UInt256 a);
                        UInt256.Add(out UInt256 c, ref a, ref b, false);
                        stack.PushUInt256(ref c);

                        break;
                    }
                    case Instruction.MUL:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        stack.PopUInt(out BigInteger b);
                        BigInteger res = BigInteger.Remainder(a * b, P256Int);
                        stack.PushUInt(ref res);
                        break;
                    }
                    case Instruction.SUB:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 a);
                        stack.PopUInt256(out UInt256 b);
                        UInt256 result = a - b;

                        stack.PushUInt256(ref result);
                        break;
                    }
                    case Instruction.DIV:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        stack.PopUInt(out BigInteger b);
                        if (b.IsZero)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            BigInteger res = BigInteger.Divide(a, b);
                            stack.PushUInt(ref res);
                        }

                        break;
                    }
                    case Instruction.SDIV:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopInt(out BigInteger a);
                        stack.PopInt(out BigInteger b);
                        if (b.IsZero)
                        {
                            stack.PushZero();
                        }
                        else if (b == BigInteger.MinusOne && a == P255Int)
                        {
                            BigInteger res = P255;
                            stack.PushUInt(ref res);
                        }
                        else
                        {
                            BigInteger res = BigInteger.Divide(a, b);
                            stack.PushSignedInt(in res);
                        }

                        break;
                    }
                    case Instruction.MOD:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        stack.PopUInt(out BigInteger b);
                        BigInteger res = b.IsZero ? BigInteger.Zero : BigInteger.Remainder(a, b);
                        stack.PushUInt(ref res);
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopInt(out BigInteger a);
                        stack.PopInt(out BigInteger b);
                        if (b.IsZero)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            BigInteger res = a.Sign * BigInteger.Remainder(BigInteger.Abs(a), BigInteger.Abs(b));
                            stack.PushSignedInt(in res);
                        }

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        stack.PopUInt(out BigInteger b);
                        stack.PopUInt(out BigInteger mod);

                        if (mod.IsZero)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            BigInteger res = BigInteger.Remainder(a + b, mod);
                            stack.PushUInt(ref res);
                        }

                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        stack.PopUInt(out BigInteger b);
                        stack.PopUInt(out BigInteger mod);

                        if (mod.IsZero)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            BigInteger res = BigInteger.Remainder(a * b, mod);
                            stack.PushUInt(ref res);
                        }

                        break;
                    }
                    case Instruction.EXP:
                    {
                        if (!UpdateGas(GasCostOf.Exp, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Metrics.ModExpOpcode++;

                        stack.PopUInt(out BigInteger baseInt);
                        Span<byte> exp = stack.PopBytes();

                        int leadingZeros = exp.LeadingZerosCount();
                        if (leadingZeros != 32)
                        {
                            int expSize = 32 - leadingZeros;
                            if (!UpdateGas((spec.IsEip160Enabled ? GasCostOf.ExpByteEip160 : GasCostOf.ExpByte) * expSize, ref gasAvailable))
                            {
                                EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                return CallResult.OutOfGasException;
                            }
                        }
                        else
                        {
                            stack.PushOne();
                            break;
                        }

                        if (baseInt.IsZero)
                        {
                            stack.PushZero();
                        }
                        else if (baseInt.IsOne)
                        {
                            stack.PushOne();
                        }
                        else
                        {
                            BigInteger res = BigInteger.ModPow(baseInt, exp.ToUnsignedBigInteger(), P256Int);
                            stack.PushUInt(ref res);
                        }

                        break;
                    }
                    case Instruction.SIGNEXTEND:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        if (a >= BigInt32)
                        {
                            break;
                        }

                        int position = 31 - (int) a;

                        Span<byte> b = stack.PopBytes();
                        sbyte sign = (sbyte) b[position];

                        if (sign >= 0)
                        {
                            BytesZero32.AsSpan(0, position).CopyTo(b.Slice(0, position));
                        }
                        else
                        {
                            BytesMax32.AsSpan(0, position).CopyTo(b.Slice(0, position));
                        }

                        stack.PushBytes(b);
                        break;
                    }
                    case Instruction.LT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        stack.PopUInt(out BigInteger b);
                        if (a < b)
                        {
                            stack.PushOne();
                        }
                        else
                        {
                            stack.PushZero();
                        }

                        break;
                    }
                    case Instruction.GT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        stack.PopUInt(out BigInteger b);
                        if (a > b)
                        {
                            stack.PushOne();
                        }
                        else
                        {
                            stack.PushZero();
                        }

                        break;
                    }
                    case Instruction.SLT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopInt(out BigInteger a);
                        stack.PopInt(out BigInteger b);

                        if (BigInteger.Compare(a, b) < 0)
                        {
                            stack.PushOne();
                        }
                        else
                        {
                            stack.PushZero();
                        }

                        break;
                    }
                    case Instruction.SGT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopInt(out BigInteger a);
                        stack.PopInt(out BigInteger b);
                        if (BigInteger.Compare(a, b) > 0)
                        {
                            stack.PushOne();
                        }
                        else
                        {
                            stack.PushZero();
                        }

                        break;
                    }
                    case Instruction.EQ:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = stack.PopBytes();
                        Span<byte> b = stack.PopBytes();
                        if (a.SequenceEqual(b))
                        {
                            stack.PushOne();
                        }
                        else
                        {
                            stack.PushZero();
                        }

                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = stack.PopBytes();
                        if (a.SequenceEqual(BytesZero32))
                        {
                            stack.PushOne();
                        }
                        else
                        {
                            stack.PushZero();
                        }

                        break;
                    }
                    case Instruction.AND:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = stack.PopBytes();
                        Span<byte> b = stack.PopBytes();

                        if (_simdOperationsEnabled)
                        {
                            Vector<byte> aVec = new Vector<byte>(a);
                            Vector<byte> bVec = new Vector<byte>(b);

                            Vector.BitwiseAnd(aVec, bVec).CopyTo(stack.Register);
                        }
                        else
                        {
                            ref var refA = ref MemoryMarshal.AsRef<ulong>(a);
                            ref var refB = ref MemoryMarshal.AsRef<ulong>(b);
                            ref var refBuffer = ref MemoryMarshal.AsRef<ulong>(stack.Register);

                            refBuffer = refA & refB;
                            Unsafe.Add(ref refBuffer, 1) = Unsafe.Add(ref refA, 1) & Unsafe.Add(ref refB, 1);
                            Unsafe.Add(ref refBuffer, 2) = Unsafe.Add(ref refA, 2) & Unsafe.Add(ref refB, 2);
                            Unsafe.Add(ref refBuffer, 3) = Unsafe.Add(ref refA, 3) & Unsafe.Add(ref refB, 3);
                        }

                        stack.PushBytes(stack.Register);
                        break;
                    }
                    case Instruction.OR:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = stack.PopBytes();
                        Span<byte> b = stack.PopBytes();

                        if (_simdOperationsEnabled)
                        {
                            Vector<byte> aVec = new Vector<byte>(a);
                            Vector<byte> bVec = new Vector<byte>(b);

                            Vector.BitwiseOr(aVec, bVec).CopyTo(stack.Register);
                        }
                        else
                        {
                            ref var refA = ref MemoryMarshal.AsRef<ulong>(a);
                            ref var refB = ref MemoryMarshal.AsRef<ulong>(b);
                            ref var refBuffer = ref MemoryMarshal.AsRef<ulong>(stack.Register);

                            refBuffer = refA | refB;
                            Unsafe.Add(ref refBuffer, 1) = Unsafe.Add(ref refA, 1) | Unsafe.Add(ref refB, 1);
                            Unsafe.Add(ref refBuffer, 2) = Unsafe.Add(ref refA, 2) | Unsafe.Add(ref refB, 2);
                            Unsafe.Add(ref refBuffer, 3) = Unsafe.Add(ref refA, 3) | Unsafe.Add(ref refB, 3);
                        }

                        stack.PushBytes(stack.Register);
                        break;
                    }
                    case Instruction.XOR:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = stack.PopBytes();
                        Span<byte> b = stack.PopBytes();

                        if (_simdOperationsEnabled)
                        {
                            Vector<byte> aVec = new Vector<byte>(a);
                            Vector<byte> bVec = new Vector<byte>(b);

                            Vector.Xor(aVec, bVec).CopyTo(stack.Register);
                        }
                        else
                        {
                            ref var refA = ref MemoryMarshal.AsRef<ulong>(a);
                            ref var refB = ref MemoryMarshal.AsRef<ulong>(b);
                            ref var refBuffer = ref MemoryMarshal.AsRef<ulong>(stack.Register);

                            refBuffer = refA ^ refB;
                            Unsafe.Add(ref refBuffer, 1) = Unsafe.Add(ref refA, 1) ^ Unsafe.Add(ref refB, 1);
                            Unsafe.Add(ref refBuffer, 2) = Unsafe.Add(ref refA, 2) ^ Unsafe.Add(ref refB, 2);
                            Unsafe.Add(ref refBuffer, 3) = Unsafe.Add(ref refA, 3) ^ Unsafe.Add(ref refB, 3);
                        }

                        stack.PushBytes(stack.Register);
                        break;
                    }
                    case Instruction.NOT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = stack.PopBytes();

                        if (_simdOperationsEnabled)
                        {
                            Vector<byte> aVec = new Vector<byte>(a);
                            Vector<byte> negVec = Vector.Xor(aVec, new Vector<byte>(BytesMax32));

                            negVec.CopyTo(stack.Register);
                        }
                        else
                        {
                            ref var refA = ref MemoryMarshal.AsRef<ulong>(a);
                            ref var refBuffer = ref MemoryMarshal.AsRef<ulong>(stack.Register);

                            refBuffer = ~refA;
                            Unsafe.Add(ref refBuffer, 1) = ~Unsafe.Add(ref refA, 1);
                            Unsafe.Add(ref refBuffer, 2) = ~Unsafe.Add(ref refA, 2);
                            Unsafe.Add(ref refBuffer, 3) = ~Unsafe.Add(ref refA, 3);
                        }

                        stack.PushBytes(stack.Register);
                        break;
                    }
                    case Instruction.BYTE:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger position);
                        Span<byte> bytes = stack.PopBytes();

                        if (position >= BigInt32)
                        {
                            stack.PushZero();
                            break;
                        }

                        int adjustedPosition = bytes.Length - 32 + (int) position;
                        if (adjustedPosition < 0)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            stack.PushByte(bytes[adjustedPosition]);
                        }

                        break;
                    }
                    case Instruction.SHA3:
                    {
                        stack.PopUInt256(out UInt256 memSrc);
                        stack.PopUInt256(out UInt256 memLength);
                        if (!UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(memLength),
                            ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref memSrc, memLength);

                        Span<byte> memData = vmState.Memory.LoadSpan(ref memSrc, memLength);
                        stack.PushBytes(ValueKeccak.Compute(memData).BytesAsSpan);
                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PushBytes(env.ExecutingAccount.Bytes);
                        break;
                    }
                    case Instruction.BALANCE:
                    {
                        var gasCost = spec.IsEip1884Enabled
                            ? GasCostOf.BalanceEip1884
                            : spec.IsEip150Enabled
                                ? GasCostOf.BalanceEip150
                                : GasCostOf.Balance;

                        if (!UpdateGas(gasCost, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Address address = stack.PopAddress();
                        UInt256 balance = _state.GetBalance(address);
                        stack.PushUInt256(ref balance);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PushBytes(env.Sender.Bytes);
                        break;
                    }
                    case Instruction.CALLVALUE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 callValue = env.Value;
                        stack.PushUInt256(ref callValue);
                        break;
                    }
                    case Instruction.ORIGIN:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PushBytes(env.Originator.Bytes);
                        break;
                    }
                    case Instruction.CALLDATALOAD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 src);
                        stack.PushBytes(env.InputData.SliceWithZeroPadding(src, 32));
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        BigInteger callDataSize = env.InputData.Length;
                        stack.PushUInt(ref callDataSize);
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        stack.PopUInt256(out UInt256 dest);
                        stack.PopUInt256(out UInt256 src);
                        stack.PopUInt256(out UInt256 length);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length),
                            ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dest, length);

                        ZeroPaddedSpan callDataSlice = env.InputData.SliceWithZeroPadding(src, (int) length);
                        vmState.Memory.Save(ref dest, callDataSlice);
                        if (_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long) dest, callDataSlice.ToArray());
                        break;
                    }
                    case Instruction.CODESIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        BigInteger codeLength = code.Length;
                        stack.PushUInt(ref codeLength);
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        stack.PopUInt256(out UInt256 dest);
                        stack.PopUInt256(out UInt256 src);
                        stack.PopUInt256(out UInt256 length);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length), ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dest, length);
                        ZeroPaddedSpan codeSlice = code.SliceWithZeroPadding(src, (int) length);
                        vmState.Memory.Save(ref dest, codeSlice);
                        if (_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long) dest, codeSlice.ToArray());
                        break;
                    }
                    case Instruction.GASPRICE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 gasPrice = env.GasPrice;
                        stack.PushUInt256(ref gasPrice);
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.ExtCodeSizeEip150 : GasCostOf.ExtCodeSize, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Address address = stack.PopAddress();
                        byte[] accountCode = GetCachedCodeInfo(address, spec).MachineCode;
                        UInt256 codeSize = (UInt256)accountCode.Length;
                        stack.PushUInt256(ref codeSize);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        Address address = stack.PopAddress();
                        stack.PopUInt256(out UInt256 dest);
                        stack.PopUInt256(out UInt256 src);
                        stack.PopUInt256(out UInt256 length);
                        if (!UpdateGas((spec.IsEip150Enabled ? GasCostOf.ExtCodeEip150 : GasCostOf.ExtCode) + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length),
                            ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dest, length);
                        byte[] externalCode = GetCachedCodeInfo(address, spec).MachineCode;
                        ZeroPaddedSpan callDataSlice = externalCode.SliceWithZeroPadding(src, (int) length);
                        vmState.Memory.Save(ref dest, callDataSlice);
                        if (_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long) dest, callDataSlice.ToArray());
                        break;
                    }
                    case Instruction.RETURNDATASIZE:
                    {
                        if (!spec.IsEip211Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        BigInteger res = _returnDataBuffer.Length;
                        stack.PushUInt(ref res);
                        break;
                    }
                    case Instruction.RETURNDATACOPY:
                    {
                        if (!spec.IsEip211Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        stack.PopUInt256(out UInt256 dest);
                        stack.PopUInt256(out UInt256 src);
                        stack.PopUInt256(out UInt256 length);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length), ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dest, length);

                        if (UInt256.AddWouldOverflow(ref length, ref src) || length + src > _returnDataBuffer.Length)
                        {
                            return CallResult.AccessViolationException;
                        }

                        ZeroPaddedSpan returnDataSlice = _returnDataBuffer.SliceWithZeroPadding(src, (int) length);
                        vmState.Memory.Save(ref dest, returnDataSlice);
                        if (_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long) dest, returnDataSlice.ToArray());
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        Metrics.BlockhashOpcode++;

                        if (!UpdateGas(GasCostOf.BlockHash, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 a);
                        long number = a > long.MaxValue ? long.MaxValue : (long) a;
                        Keccak blockHash = _blockhashProvider.GetBlockhash(env.CurrentBlock, number);
                        stack.PushBytes(blockHash?.Bytes ?? BytesZero32);

                        if (isTrace)
                        {
                            if (_txTracer.IsTracingBlockHash && blockHash != null)
                            {
                                _txTracer.ReportBlockHash(blockHash);
                            }
                        }

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PushBytes(env.CurrentBlock.GasBeneficiary.Bytes);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 diff = env.CurrentBlock.Difficulty;
                        stack.PushUInt256(ref diff);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 timestamp = env.CurrentBlock.Timestamp;
                        stack.PushUInt256(ref timestamp);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 blockNumber = (UInt256) env.CurrentBlock.Number;
                        stack.PushUInt256(ref blockNumber);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 gasLimit = (UInt256) env.CurrentBlock.GasLimit;
                        stack.PushUInt256(ref gasLimit);
                        break;
                    }
                    case Instruction.CHAINID:
                    {
                        if (!spec.IsEip1344Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PushBytes(_chainId);
                        break;
                    }
                    case Instruction.SELFBALANCE:
                    {
                        if (!spec.IsEip1884Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.SelfBalance, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 balance = _state.GetBalance(env.ExecutingAccount);
                        stack.PushUInt256(ref balance);
                        break;
                    }
                    case Instruction.POP:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopLimbo();
                        break;
                    }
                    case Instruction.MLOAD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 memPosition);
                        UpdateMemoryCost(ref memPosition, 32);
                        Span<byte> memData = vmState.Memory.LoadSpan(ref memPosition);
                        if (_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long) memPosition, memData);

                        stack.PushBytes(memData);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 memPosition);
                        Span<byte> data = stack.PopBytes();
                        UpdateMemoryCost(ref memPosition, 32);
                        vmState.Memory.SaveWord(ref memPosition, data);
                        if (_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long) memPosition, data.PadLeft(32));

                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 memPosition);
                        byte data = stack.PopByte();
                        UpdateMemoryCost(ref memPosition, UInt256.One);
                        vmState.Memory.SaveByte(ref memPosition, data);
                        if (_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long) memPosition, new[] {data});

                        break;
                    }
                    case Instruction.SLOAD:
                    {
                        Metrics.SloadOpcode++;
                        var gasCost = spec.IsEip1884Enabled
                            ? GasCostOf.SLoadEip1884
                            : spec.IsEip150Enabled
                                ? GasCostOf.SLoadEip150
                                : GasCostOf.SLoad;

                        if (!UpdateGas(gasCost, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 storageIndex);
                        byte[] value = _storage.Get(new StorageCell(env.ExecutingAccount, storageIndex));
                        stack.PushBytes(value);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        Metrics.SstoreOpcode++;

                        if (vmState.IsStatic)
                        {
                            EndInstructionTraceError(EvmExceptionType.StaticCallViolation);
                            return CallResult.StaticCallViolationException;
                        }

                        bool useNetMetering = spec.IsEip1283Enabled | spec.IsEip2200Enabled;
                        // fail fast before the first storage read if gas is not enough even for reset
                        if (!useNetMetering && !UpdateGas(GasCostOf.SReset, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        if (spec.IsEip2200Enabled)
                        {
                            if (_txTracer.IsTracingRefunds) _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend - GasCostOf.SStoreNetMeteredEip2200 + 1);
                            if (gasAvailable <= GasCostOf.CallStipend)
                            {
                                EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                return CallResult.OutOfGasException;
                            }
                        }

                        stack.PopUInt256(out UInt256 storageIndex);
                        byte[] newValue = stack.PopBytes().WithoutLeadingZeros().ToArray();
                        bool newIsZero = newValue.IsZero();

                        StorageCell storageCell = new StorageCell(env.ExecutingAccount, storageIndex);
                        byte[] currentValue = _storage.Get(storageCell);
                        bool currentIsZero = currentValue.IsZero();

                        bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, newValue);

                        if (!useNetMetering) // note that for this case we already deducted 5000
                        {
                            if (newIsZero)
                            {
                                if (!newSameAsCurrent)
                                {
                                    vmState.Refund += RefundOf.SClear;
                                    if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(RefundOf.SClear);
                                }
                            }
                            else if (currentIsZero)
                            {
                                if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable))
                                {
                                    EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                    return CallResult.OutOfGasException;
                                }
                            }
                        }
                        else // net metered
                        {
                            if (newSameAsCurrent)
                            {
                                long netMeteredStoreCost = spec.IsEip2200Enabled ? GasCostOf.SStoreNetMeteredEip2200 : GasCostOf.SStoreNetMeteredEip1283;
                                if (!UpdateGas(netMeteredStoreCost, ref gasAvailable))
                                {
                                    EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                    return CallResult.OutOfGasException;
                                }
                            }
                            else // eip1283enabled, C != N
                            {
                                byte[] originalValue = _storage.GetOriginal(storageCell);
                                bool originalIsZero = originalValue.IsZero();

                                bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);
                                if (currentSameAsOriginal)
                                {
                                    if (currentIsZero)
                                    {
                                        if (!UpdateGas(GasCostOf.SSet, ref gasAvailable))
                                        {
                                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                            return CallResult.OutOfGasException;
                                        }
                                    }
                                    else // eip1283enabled, current == original != new, !currentIsZero
                                    {
                                        if (!UpdateGas(GasCostOf.SReset, ref gasAvailable))
                                        {
                                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                            return CallResult.OutOfGasException;
                                        }

                                        if (newIsZero)
                                        {
                                            vmState.Refund += RefundOf.SClear;
                                            if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(RefundOf.SClear);
                                        }
                                    }
                                }
                                else // net metered, new != current != original
                                {
                                    long netMeteredStoreCost = spec.IsEip2200Enabled ? GasCostOf.SStoreNetMeteredEip2200 : GasCostOf.SStoreNetMeteredEip1283;
                                    if (!UpdateGas(netMeteredStoreCost, ref gasAvailable))
                                    {
                                        EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                        return CallResult.OutOfGasException;
                                    }

                                    if (!originalIsZero) // net metered, new != current != original != 0
                                    {
                                        if (currentIsZero)
                                        {
                                            vmState.Refund -= RefundOf.SClear;
                                            if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(-RefundOf.SClear);
                                        }

                                        if (newIsZero)
                                        {
                                            vmState.Refund += RefundOf.SClear;
                                            if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(RefundOf.SClear);
                                        }
                                    }

                                    bool newSameAsOriginal = Bytes.AreEqual(originalValue, newValue);
                                    if (newSameAsOriginal)
                                    {
                                        long refundFromReversal;
                                        if (originalIsZero)
                                        {
                                            refundFromReversal = spec.IsEip2200Enabled ? RefundOf.SSetReversedEip2200 : RefundOf.SSetReversedEip1283;
                                        }
                                        else
                                        {
                                            refundFromReversal = spec.IsEip2200Enabled ? RefundOf.SClearReversedEip2200 : RefundOf.SClearReversedEip1283;
                                        }

                                        vmState.Refund += refundFromReversal;
                                        if (_txTracer.IsTracingRefunds) _txTracer.ReportRefund(refundFromReversal);
                                    }
                                }
                            }
                        }

                        if (!newSameAsCurrent)
                        {
                            byte[] valueToStore = newIsZero ? BytesZero : newValue;
                            _storage.Set(storageCell, valueToStore);
                        }

                        if (_txTracer.IsTracingInstructions)
                        {
                            byte[] valueToStore = newIsZero ? BytesZero : newValue;
                            Span<byte> span = new byte[32]; // do not stackalloc here
                            storageCell.Index.ToBigEndian(span);
                            _txTracer.ReportStorageChange(span, valueToStore);
                        }

                        if (_txTracer.IsTracingOpLevelStorage)
                        {
                            _txTracer.SetOperationStorage(storageCell.Address, storageIndex, newValue, currentValue);
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 jumpDest);
                        Jump(jumpDest);
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 jumpDest);
                        Span<byte> condition = stack.PopBytes();
                        if (!condition.SequenceEqual(BytesZero32))
                        {
                            Jump(jumpDest);
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }
                        
                        stack.PushUInt32(programCounter - 1);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 size = vmState.Memory.Size;
                        stack.PushUInt256(ref size);
                        break;
                    }
                    case Instruction.GAS:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 gas = (UInt256) gasAvailable;
                        stack.PushUInt256(ref gas);
                        break;
                    }
                    case Instruction.JUMPDEST:
                    {
                        if (!UpdateGas(GasCostOf.JumpDest, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        break;
                    }
                    case Instruction.PUSH1:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        int programCounterInt = programCounter;
                        if (programCounterInt >= code.Length)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            stack.PushByte(code[programCounterInt]);
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
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        int length = instruction - Instruction.PUSH1 + 1;
                        int programCounterInt = programCounter;
                        int usedFromCode = Math.Min(code.Length - programCounterInt, length);

                        stack.PushLeftPaddedBytes(code.Slice(programCounterInt, usedFromCode), length);

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
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

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
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.Swap(instruction - Instruction.SWAP1 + 2);
                        break;
                    }
                    case Instruction.LOG0:
                    case Instruction.LOG1:
                    case Instruction.LOG2:
                    case Instruction.LOG3:
                    case Instruction.LOG4:
                    {
                        if (vmState.IsStatic)
                        {
                            EndInstructionTraceError(EvmExceptionType.StaticCallViolation);
                            return CallResult.StaticCallViolationException;
                        }

                        stack.PopUInt256(out UInt256 memoryPos);
                        stack.PopUInt256(out UInt256 length);
                        long topicsCount = instruction - Instruction.LOG0;
                        UpdateMemoryCost(ref memoryPos, length);
                        if (!UpdateGas(
                            GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                            (long) length * GasCostOf.LogData, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        byte[] data = vmState.Memory.Load(ref memoryPos, length);
                        Keccak[] topics = new Keccak[topicsCount];
                        for (int i = 0; i < topicsCount; i++)
                        {
                            topics[i] = new Keccak(stack.PopBytes().ToArray());
                        }

                        LogEntry logEntry = new LogEntry(
                            env.ExecutingAccount,
                            data,
                            topics);
                        vmState.Logs.Add(logEntry);
                        break;
                    }
                    case Instruction.CREATE:
                    case Instruction.CREATE2:
                    {
                        if (!spec.IsEip1014Enabled && instruction == Instruction.CREATE2)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        if (vmState.IsStatic)
                        {
                            EndInstructionTraceError(EvmExceptionType.StaticCallViolation);
                            return CallResult.StaticCallViolationException;
                        }

                        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
                        if (!_state.AccountExists(env.ExecutingAccount))
                        {
                            _state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
                        }

                        stack.PopUInt256(out UInt256 value);
                        stack.PopUInt256(out UInt256 memoryPositionOfInitCode);
                        stack.PopUInt256(out UInt256 initCodeLength);
                        Span<byte> salt = null;
                        if (instruction == Instruction.CREATE2)
                        {
                            salt = stack.PopBytes();
                        }

                        long gasCost = GasCostOf.Create + (instruction == Instruction.CREATE2 ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(initCodeLength) : 0);
                        if (!UpdateGas(gasCost, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref memoryPositionOfInitCode, initCodeLength);

                        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
                        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
                        {
                            // TODO: need a test for this
                            _returnDataBuffer = new byte[0];
                            stack.PushZero();
                            break;
                        }

                        Span<byte> initCode = vmState.Memory.LoadSpan(ref memoryPositionOfInitCode, initCodeLength);
                        UInt256 balance = _state.GetBalance(env.ExecutingAccount);
                        if (value > balance)
                        {
                            stack.PushZero();
                            break;
                        }

                        EndInstructionTrace();
                        // todo: === below is a new call - refactor / move

                        long callGas = spec.IsEip150Enabled ? gasAvailable - gasAvailable / 64L : gasAvailable;
                        if (!UpdateGas(callGas, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Address contractAddress = instruction == Instruction.CREATE
                            ? ContractAddress.From(env.ExecutingAccount, _state.GetNonce(env.ExecutingAccount))
                            : ContractAddress.From(env.ExecutingAccount, salt, initCode);

                        _state.IncrementNonce(env.ExecutingAccount);

                        int stateSnapshot = _state.TakeSnapshot();
                        int storageSnapshot = _storage.TakeSnapshot();

                        bool accountExists = _state.AccountExists(contractAddress);
                        if (accountExists && (GetCachedCodeInfo(contractAddress, spec).MachineCode.Length != 0 || _state.GetNonce(contractAddress) != 0))
                        {
                            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
                            if (isTrace) _logger.Trace($"Contract collision at {contractAddress}");
                            stack.PushZero();
                            break;
                        }

                        if (accountExists)
                        {
                            _state.UpdateStorageRoot(contractAddress, Keccak.EmptyTreeHash);
                        }
                        else if (_state.IsDeadAccount(contractAddress))
                        {
                            _storage.ClearStorage(contractAddress);
                        }

                        _state.SubtractFromBalance(env.ExecutingAccount, value, spec);
                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.TransferValue = value;
                        callEnv.Value = value;
                        callEnv.Sender = env.ExecutingAccount;
                        callEnv.CodeSource = null;
                        callEnv.Originator = env.Originator;
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.ExecutingAccount = contractAddress;
                        callEnv.CodeInfo = new CodeInfo(initCode.ToArray());
                        callEnv.InputData = Bytes.Empty;
                        EvmState callState = new EvmState(
                            callGas,
                            callEnv,
                            instruction == Instruction.CREATE2 ? ExecutionType.Create2 : ExecutionType.Create,
                            false,
                            stateSnapshot,
                            storageSnapshot,
                            0L,
                            0L,
                            vmState.IsStatic,
                            false,
                            accountExists);

                        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                        return new CallResult(callState);
                    }
                    case Instruction.RETURN:
                    {
                        stack.PopUInt256(out UInt256 memoryPos);
                        stack.PopUInt256(out UInt256 length);

                        UpdateMemoryCost(ref memoryPos, length);
                        byte[] returnData = vmState.Memory.Load(ref memoryPos, length);

                        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                        EndInstructionTrace();
                        return new CallResult(returnData, null);
                    }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    case Instruction.STATICCALL:
                    {
                        Metrics.Calls++;

                        if (instruction == Instruction.DELEGATECALL && !spec.IsEip7Enabled ||
                            instruction == Instruction.STATICCALL && !spec.IsEip214Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        stack.PopUInt(out BigInteger gasLimit);
                        Address codeSource = stack.PopAddress();
                        UInt256 callValue;
                        switch (instruction)
                        {
                            case Instruction.STATICCALL:
                                callValue = UInt256.Zero;
                                break;
                            case Instruction.DELEGATECALL:
                                callValue = env.Value;
                                break;
                            default:
                                stack.PopUInt256(out callValue);
                                break;
                        }

                        UInt256 transferValue = instruction == Instruction.DELEGATECALL ? UInt256.Zero : callValue;
                        stack.PopUInt256(out UInt256 dataOffset);
                        stack.PopUInt256(out UInt256 dataLength);
                        stack.PopUInt256(out UInt256 outputOffset);
                        stack.PopUInt256(out UInt256 outputLength);

                        if (vmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE)
                        {
                            EndInstructionTraceError(EvmExceptionType.StaticCallViolation);
                            return CallResult.StaticCallViolationException;
                        }
                        
                        Address sender = instruction == Instruction.DELEGATECALL ? env.Sender : env.ExecutingAccount;
                        Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL ? codeSource : env.ExecutingAccount;

                        if (isTrace)
                        {
                            _logger.Trace($"Tx sender {sender}");
                            _logger.Trace($"Tx code source {codeSource}");
                            _logger.Trace($"Tx target {target}");
                            _logger.Trace($"Tx value {callValue}");
                            _logger.Trace($"Tx transfer value {transferValue}");
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

                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.CallEip150 : GasCostOf.Call, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dataOffset, dataLength);
                        UpdateMemoryCost(ref outputOffset, outputLength);
                        if (!UpdateGas(gasExtra, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        if (spec.IsEip150Enabled)
                        {
                            gasLimit = BigInteger.Min(gasAvailable - gasAvailable / 64L, gasLimit);
                        }

                        long gasLimitUl = (long) gasLimit;
                        if (!UpdateGas(gasLimitUl, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        if (!transferValue.IsZero)
                        {
                            if (_txTracer.IsTracingRefunds) _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
                            gasLimitUl += GasCostOf.CallStipend;
                        }

                        if (env.CallDepth >= MaxCallDepth || !transferValue.IsZero && _state.GetBalance(env.ExecutingAccount) < transferValue)
                        {
                            _returnDataBuffer = new byte[0];
                            stack.PushZero();

                            if (_txTracer.IsTracingInstructions)
                            {
                                // very specific for Parity trace, need to find generalization - very peculiar 32 length...
                                byte[] memoryTrace = vmState.Memory.Load(ref dataOffset, 32);
                                _txTracer.ReportMemoryChange((long) dataOffset, memoryTrace);
                            }

                            if (isTrace) _logger.Trace("FAIL - call depth");
                            if (_txTracer.IsTracingInstructions) _txTracer.ReportOperationRemainingGas(gasAvailable);
                            if (_txTracer.IsTracingInstructions) _txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);

                            UpdateGasUp(gasLimitUl, ref gasAvailable);
                            if (_txTracer.IsTracingInstructions) _txTracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
                            break;
                        }

                        byte[] callData = vmState.Memory.Load(ref dataOffset, dataLength);

                        int stateSnapshot = _state.TakeSnapshot();
                        int storageSnapshot = _storage.TakeSnapshot();
                        _state.SubtractFromBalance(sender, transferValue, spec);

                        ExecutionEnvironment callEnv = new ExecutionEnvironment();
                        callEnv.CallDepth = env.CallDepth + 1;
                        callEnv.CurrentBlock = env.CurrentBlock;
                        callEnv.GasPrice = env.GasPrice;
                        callEnv.Originator = env.Originator;
                        callEnv.Sender = sender;
                        callEnv.CodeSource = codeSource;
                        callEnv.ExecutingAccount = target;
                        callEnv.TransferValue = transferValue;
                        callEnv.Value = callValue;
                        callEnv.InputData = callData;
                        callEnv.CodeInfo = GetCachedCodeInfo(codeSource, spec);

                        if (isTrace) _logger.Trace($"Tx call gas {gasLimitUl}");
                        if (outputLength == 0)
                        {
                            // TODO: when output length is 0 outputOffset can have any value really
                            // and the value does not matter and it can cause trouble when beyond long range
                            outputOffset = 0;
                        }

                        ExecutionType executionType;
                        if (instruction == Instruction.CALL)
                        {
                            executionType = ExecutionType.Call;
                        }
                        else if (instruction == Instruction.DELEGATECALL)
                        {
                            executionType = ExecutionType.DelegateCall;
                        }
                        else if (instruction == Instruction.STATICCALL)
                        {
                            executionType = ExecutionType.StaticCall;
                        }
                        else if (instruction == Instruction.CALLCODE)
                        {
                            executionType = ExecutionType.CallCode;
                        }
                        else
                        {
                            throw new NotSupportedException($"Execution type is undefined for {Enum.GetName(typeof(Instruction), instruction)}");
                        }

                        EvmState callState = new EvmState(
                            gasLimitUl,
                            callEnv,
                            executionType,
                            false,
                            stateSnapshot,
                            storageSnapshot,
                            (long) outputOffset,
                            (long) outputLength,
                            instruction == Instruction.STATICCALL || vmState.IsStatic,
                            false,
                            false);

                        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                        EndInstructionTrace();
                        return new CallResult(callState);
                    }
                    case Instruction.REVERT:
                    {
                        if (!spec.IsEip140Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        stack.PopUInt256(out UInt256 memoryPos);
                        stack.PopUInt256(out UInt256 length);

                        UpdateMemoryCost(ref memoryPos, length);
                        byte[] errorDetails = vmState.Memory.Load(ref memoryPos, length);

                        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                        EndInstructionTrace();
                        return new CallResult(errorDetails, null, true);
                    }
                    case Instruction.INVALID:
                    {
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        EndInstructionTraceError(EvmExceptionType.BadInstruction);
                        return CallResult.InvalidInstructionException;
                    }
                    case Instruction.SELFDESTRUCT:
                    {
                        if (vmState.IsStatic)
                        {
                            EndInstructionTraceError(EvmExceptionType.StaticCallViolation);
                            return CallResult.StaticCallViolationException;
                        }

                        if (spec.IsEip150Enabled && !UpdateGas(GasCostOf.SelfDestructEip150, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Metrics.SelfDestructs++;

                        Address inheritor = stack.PopAddress();
                        vmState.DestroyList.Add(env.ExecutingAccount);

                        UInt256 ownerBalance = _state.GetBalance(env.ExecutingAccount);
                        if (_txTracer.IsTracingActions) _txTracer.ReportSelfDestruct(env.ExecutingAccount, ownerBalance, inheritor);
                        if (spec.IsEip158Enabled && ownerBalance != 0 && _state.IsDeadAccount(inheritor))
                        {
                            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                            {
                                EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                return CallResult.OutOfGasException;
                            }
                        }

                        bool inheritorAccountExists = _state.AccountExists(inheritor);
                        if (!spec.IsEip158Enabled && !inheritorAccountExists && spec.IsEip150Enabled)
                        {
                            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                            {
                                EndInstructionTraceError(EvmExceptionType.OutOfGas);
                                return CallResult.OutOfGasException;
                            }
                        }

                        if (!inheritorAccountExists)
                        {
                            _state.CreateAccount(inheritor, ownerBalance);
                        }
                        else if (!inheritor.Equals(env.ExecutingAccount))
                        {
                            _state.AddToBalance(inheritor, ownerBalance, spec);
                        }

                        _state.SubtractFromBalance(env.ExecutingAccount, ownerBalance, spec);

                        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                        EndInstructionTrace();
                        return CallResult.Empty;
                    }
                    case Instruction.SHL:
                    {
                        if (!spec.IsEip145Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 a);
                        if (a >= 256UL)
                        {
                            stack.PopLimbo();
                            stack.PushZero();
                        }
                        else
                        {
                            stack.PopUInt256(out UInt256 b);
                            BigInteger res = b << (int) a.S0;
                            stack.PushSignedInt(in res);
                        }

                        break;
                    }
                    case Instruction.SHR:
                    {
                        if (!spec.IsEip145Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt256(out UInt256 a);
                        if (a >= 256)
                        {
                            stack.PopLimbo();
                            stack.PushZero();
                        }
                        else
                        {
                            stack.PopUInt256(out UInt256 b);
                            UInt256 res = b >> (int) a.S0;
                            stack.PushUInt256(ref res);
                        }

                        break;
                    }
                    case Instruction.SAR:
                    {
                        if (!spec.IsEip145Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        stack.PopUInt(out BigInteger a);
                        stack.PopInt(out BigInteger b);
                        if (a >= BigInt256)
                        {
                            if (b.Sign >= 0)
                            {
                                stack.PushZero();
                            }
                            else
                            {
                                BigInteger res = BigInteger.MinusOne;
                                stack.PushSignedInt(in res);
                            }
                        }
                        else
                        {
                            BigInteger res = BigInteger.DivRem(b, BigInteger.Pow(2, (int) a), out BigInteger remainder);
                            if (remainder.Sign < 0)
                            {
                                res--;
                            }

                            stack.PushSignedInt(in res);
                        }

                        break;
                    }
                    case Instruction.EXTCODEHASH:
                    {
                        if (!spec.IsEip1052Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }

                        var gasCost = spec.IsEip1884Enabled ? GasCostOf.ExtCodeHashEip1884 : GasCostOf.ExtCodeHash;
                        if (!UpdateGas(gasCost, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        Address address = stack.PopAddress();
                        if (!_state.AccountExists(address) || _state.IsDeadAccount(address))
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            stack.PushBytes(_state.GetCodeHash(address).Bytes);
                        }

                        break;
                    }
                    case Instruction.BEGINSUB:
                    {
                        if (!spec.IsEip2315Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        // why do we even need the cost of it?
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }
                        
                        EndInstructionTraceError(EvmExceptionType.InvalidSubroutineEntry);
                        return CallResult.InvalidSubroutineEntry;
                    }
                    case Instruction.RETURNSUB:
                    {
                        if (!spec.IsEip2315Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        if (vmState.ReturnStackHead == 0)
                        {
                            EndInstructionTraceError(EvmExceptionType.InvalidSubroutineReturn);
                            return CallResult.InvalidSubroutineReturn;
                        }

                        programCounter = vmState.ReturnStack[--vmState.ReturnStackHead];
                        break;
                    }
                    case Instruction.JUMPSUB:
                    {
                        if (!spec.IsEip2315Enabled)
                        {
                            EndInstructionTraceError(EvmExceptionType.BadInstruction);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            EndInstructionTraceError(EvmExceptionType.OutOfGas);
                            return CallResult.OutOfGasException;
                        }

                        if (vmState.ReturnStackHead == EvmStack.ReturnStackSize)
                        {
                            EndInstructionTraceError(EvmExceptionType.StackOverflow);
                            return CallResult.StackOverflowException;
                        }
                        
                        vmState.ReturnStack[vmState.ReturnStackHead++] = programCounter;
                        
                        stack.PopUInt256(out UInt256 jumpDest);
                        Jump(jumpDest, true);
                        programCounter++;

                        break;
                    }
                    default:
                    {
                        EndInstructionTraceError(EvmExceptionType.BadInstruction);
                        return CallResult.InvalidInstructionException;
                    }
                }

                EndInstructionTrace();
            }

            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
            return CallResult.Empty;
        }

        internal readonly struct CallResult
        {
            public static CallResult InvalidSubroutineEntry = new CallResult(EvmExceptionType.InvalidSubroutineEntry);
            public static CallResult InvalidSubroutineReturn = new CallResult(EvmExceptionType.InvalidSubroutineReturn);
            public static CallResult OutOfGasException = new CallResult(EvmExceptionType.OutOfGas);
            public static CallResult AccessViolationException = new CallResult(EvmExceptionType.AccessViolation);
            public static CallResult InvalidJumpDestination = new CallResult(EvmExceptionType.InvalidJumpDestination);
            public static CallResult InvalidInstructionException = new CallResult(EvmExceptionType.BadInstruction);
            public static CallResult StaticCallViolationException = new CallResult(EvmExceptionType.StaticCallViolation);
            public static CallResult StackOverflowException = new CallResult(EvmExceptionType.StackOverflow); // TODO: use these to avoid CALL POP attacks
            public static CallResult StackUnderflowException = new CallResult(EvmExceptionType.StackUnderflow); // TODO: use these to avoid CALL POP attacks
            public static readonly CallResult Empty = new CallResult(Bytes.Empty, null);

            public CallResult(EvmState stateToExecute)
            {
                StateToExecute = stateToExecute;
                Output = Bytes.Empty;
                PrecompileSuccess = null;
                ShouldRevert = false;
                ExceptionType = EvmExceptionType.None;
            }

            private CallResult(EvmExceptionType exceptionType)
            {
                StateToExecute = null;
                Output = StatusCode.FailureBytes;
                PrecompileSuccess = null;
                ShouldRevert = false;
                ExceptionType = exceptionType;
            }

            public CallResult(byte[] output, bool? precompileSuccess, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
            {
                StateToExecute = null;
                Output = output;
                PrecompileSuccess = precompileSuccess;
                ShouldRevert = shouldRevert;
                ExceptionType = exceptionType;
            }

            public EvmState StateToExecute { get; }
            public byte[] Output { get; }
            public EvmExceptionType ExceptionType { get; }
            public bool ShouldRevert { get; }
            public bool? PrecompileSuccess { get; } // TODO: check this behaviour as it seems it is required and previously that was not the case
            public bool IsReturn => StateToExecute == null;
            public bool IsException => ExceptionType != EvmExceptionType.None;
        }
    }
}