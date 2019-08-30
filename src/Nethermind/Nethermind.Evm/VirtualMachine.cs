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
using System.ComponentModel.Design;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;

[assembly:InternalsVisibleTo("Nethermind.Evm.Test")]
namespace Nethermind.Evm
{
    public class VirtualMachine : IVirtualMachine
    {
        // cannot just use the dictionary because of the release spec
        protected virtual bool IsPrecompiled(Address address, IReleaseSpec releaseSpec) { return address.IsPrecompiled(releaseSpec); }
        protected Dictionary<Address, IPrecompiledContract> Precompiles { get; set; }

        private const EvmExceptionType BadInstructionErrorText = EvmExceptionType.BadInstruction;
        private const EvmExceptionType OutOfGasErrorText = EvmExceptionType.OutOfGas;

        private bool _simdOperationsEnabled = Vector<byte>.Count == 32;
        private BigInteger P255Int = BigInteger.Pow(2, 255);
        private BigInteger P256Int = BigInteger.Pow(2, 256);
        private BigInteger P255 => P255Int;
        private BigInteger BigInt256 = 256;
        public BigInteger BigInt32 = 32;

        internal byte[] BytesZero = {0};

        public static byte[] BytesOne32 =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 1
        };
        
        public static byte[] BytesZero32 =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0
        };

        internal byte[] MissingStateHash = BytesZero32;
        
        internal byte[] BytesMax32 =
        {
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255,
            255, 255, 255, 255, 255, 255, 255, 255
        };

        private byte[] _chainId;

        private readonly IStateUpdateHashProvider _blockhashProvider;
        private readonly ISpecProvider _specProvider;
        private readonly LruCache<Keccak, CodeInfo> _codeCache = new LruCache<Keccak, CodeInfo>(4 * 1024);
        private readonly ILogger _logger;
        private readonly IStateProvider _state;
        private readonly Stack<VmState> _stateStack = new Stack<VmState>();
        private readonly IStorageProvider _storage;
        private Address _parityTouchBugAccount;
        private byte[] _returnDataBuffer = new byte[0];
        private ITxTracer _txTracer;

        public VirtualMachine(IStateProvider stateProvider, IStorageProvider storageProvider,
            IStateUpdateHashProvider blockhashProvider, ISpecProvider specProvider, ILogManager logManager)
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
        public TransactionSubstate Run(VmState state, ITxTracer txTracer)
        {
            _txTracer = txTracer;

            IReleaseSpec spec = _specProvider.GetSpec(state.Env.CurrentBlock.Number);
            VmState currentState = state;
            byte[] previousCallResult = null;
            byte[] previousCallOutput = Bytes.Empty;
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
                            _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.ExecutionType == ExecutionType.Create ? currentState.Env.CodeInfo.MachineCode : currentState.Env.InputData, currentState.ExecutionType);
                            if (_txTracer.IsTracingCode) _txTracer.ReportByteCode(currentState.Env.CodeInfo.MachineCode);
                        }
                        
                        callResult = ExecuteCall(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                        if (!callResult.IsReturn)
                        {
                            _stateStack.Push(currentState);
                            currentState = callResult.StateToExecute;
                            previousCallResult = null; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests (failing block 9411 on Ropsten https://ropsten.etherscan.io/vmtrace?txhash=0x666194d15c14c54fffafab1a04c08064af165870ef9a87f65711dcce7ed27fe1)
                            _returnDataBuffer = previousCallOutput = Bytes.Empty; // TODO: testing on ropsten sync, write VirtualMachineTest for this case as it was not covered by Ethereum tests
                            continue;
                        }

                        if (callResult.IsException)
                        {
                            if(_txTracer.IsTracingActions) _txTracer.ReportActionError(callResult.ExceptionType);
                            _state.Restore(currentState.StateSnapshot);
                            _storage.Restore(currentState.StorageSnapshot);

                            if (_parityTouchBugAccount != null)
                            {
                                _state.AddToBalance(_parityTouchBugAccount, UInt256.Zero, spec);
                                _parityTouchBugAccount = null;
                            }

                            if (currentState.IsTopLevel)
                            {
                                return new TransactionSubstate("Error");
                            }

                            previousCallResult = StatusCode.FailureBytes;
                            previousCallOutputDestination = UInt256.Zero;
                            _returnDataBuffer = previousCallOutput = Bytes.Empty;

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
                                if (currentState.ExecutionType == ExecutionType.Create && currentState.GasAvailable < codeDepositGasCost)
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                                }
                                else
                                {
                                    if (currentState.ExecutionType == ExecutionType.Create)
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
                    VmState previousState = currentState;
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                    currentState.GasAvailable += previousState.GasAvailable;
                    bool previousStateSucceeded = true;
                    
                    if (!callResult.ShouldRevert)
                    {
                        long gasAvailableForCodeDeposit = previousState.GasAvailable; // TODO: refactor, this is to fix 61363 Ropsten
                        if (previousState.ExecutionType == ExecutionType.Create)
                        {
                            previousCallResult = callCodeOwner.Bytes;
                            previousCallOutputDestination = UInt256.Zero;
                            _returnDataBuffer = previousCallOutput = Bytes.Empty;

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
                                    _state.DeleteAccount(callCodeOwner);
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
                            previousCallOutput = callResult.Output.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                            previousCallOutputDestination = (ulong)previousState.OutputDestination;
                            if(previousState.IsPrecompile)
                            {
                                // parity induced if else for vmtrace
                                if (_txTracer.IsTracingInstructions)
                                {
                                    _txTracer.ReportMemoryChange((long)previousCallOutputDestination, previousCallOutput);
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
                        previousCallOutput = callResult.Output.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                        previousCallOutputDestination = (ulong)previousState.OutputDestination;
                        
                        
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
                        return new TransactionSubstate("Error");
                    }

                    previousCallResult = StatusCode.FailureBytes;
                    previousCallOutputDestination = UInt256.Zero;
                    _returnDataBuffer = previousCallOutput = Bytes.Empty;

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

        protected virtual void InitializePrecompiledContracts()
        {
            Precompiles = new Dictionary<Address, IPrecompiledContract>
            {
                [EcRecoverPrecompiledContract.Instance.Address] = EcRecoverPrecompiledContract.Instance,
                [Sha256PrecompiledContract.Instance.Address] = Sha256PrecompiledContract.Instance,
                [Ripemd160PrecompiledContract.Instance.Address] = Ripemd160PrecompiledContract.Instance,
                [IdentityPrecompiledContract.Instance.Address] = IdentityPrecompiledContract.Instance,
                [Bn128AddPrecompiledContract.Instance.Address] = Bn128AddPrecompiledContract.Instance,
                [Bn128MulPrecompiledContract.Instance.Address] = Bn128MulPrecompiledContract.Instance,
                [Bn128PairingPrecompiledContract.Instance.Address] = Bn128PairingPrecompiledContract.Instance,
                [ModExpPrecompiledContract.Instance.Address] = ModExpPrecompiledContract.Instance,
                [Blake2BPrecompiledContract.Instance.Address] = Blake2BPrecompiledContract.Instance,
            };
        }

        private bool UpdateGas(long gasCost, ref long gasAvailable)
        {
            if (gasAvailable < gasCost)
            {
                Metrics.EvmExceptions++;
                return false;
            }

            gasAvailable -= gasCost;
            return true;
        }

        private void RefundGas(long refund, ref long gasAvailable)
        {
            gasAvailable += refund;
        }

        private CallResult ExecutePrecompile(VmState state, IReleaseSpec spec)
        {
            byte[] callData = state.Env.InputData;
            UInt256 transferValue = state.Env.TransferValue;
            long gasAvailable = state.GasAvailable;

            IPrecompiledContract precompile = Precompiles[state.Env.CodeInfo.PrecompileAddress];
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
                throw new OutOfGasException();
            }

            if (!UpdateGas(dataGasCost, ref gasAvailable))
            {
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

        private CallResult ExecuteCall(VmState vmState, byte[] previousCallResult, byte[] previousCallOutput, in UInt256 previousCallOutputDestination, IReleaseSpec spec)
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

                if (vmState.ExecutionType == ExecutionType.Create && spec.IsEip158Enabled)
                {
                    _state.IncrementNonce(env.ExecutingAccount);
                }
            }

            if (vmState.Env.CodeInfo.MachineCode == null || vmState.Env.CodeInfo.MachineCode.Length == 0)
            {
                return CallResult.Empty;
            }

            vmState.InitStacks();
            Span<byte> bytesOnStack = vmState.BytesOnStack.AsSpan();
            int stackHead = vmState.StackHead;
            long gasAvailable = vmState.GasAvailable;
            UInt256 programCounter = vmState.ProgramCounter;
            Span<byte> code = env.CodeInfo.MachineCode.AsSpan();

            void UpdateCurrentState()
            {
                vmState.ProgramCounter = programCounter;
                vmState.GasAvailable = gasAvailable;
                vmState.StackHead = stackHead;
            }

            void StartInstructionTrace(Instruction instruction, Span<byte> stack)
            {
                if (!traceOpcodes)
                {
                    return;
                }
                
                _txTracer.StartOperation(env.CallDepth + 1, gasAvailable, instruction, (int)programCounter);
                if (_txTracer.IsTracingMemory) { _txTracer.SetOperationMemory(vmState.Memory.GetTrace()); }
                if (_txTracer.IsTracingStack) { _txTracer.SetOperationStack(GetStackTrace(stack)); }
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

            void PushBytes(Span<byte> value, Span<byte> stack)
            {
                if (_txTracer.IsTracingInstructions) _txTracer.ReportStackPush(value);
                
                if (value.Length != 32)
                {
                    stack.Slice(stackHead * 32, 32 - value.Length).Clear();
                }

                value.CopyTo(stack.Slice(stackHead * 32 + (32 - value.Length), value.Length));
                stackHead++;
                if (stackHead >= VmParams.MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushLeftPaddedBytes(Span<byte> value, int paddingLength, Span<byte> stack)
            {
                if (_txTracer.IsTracingInstructions) _txTracer.ReportStackPush(value);
                
                if (value.Length != 32)
                {
                    stack.Slice(stackHead * 32, 32).Clear();
                }

                value.CopyTo(stack.Slice(stackHead * 32 + 32 - paddingLength, value.Length));
                stackHead++;
                if (stackHead >= VmParams.MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushByte(byte value, Span<byte> stack)
            {
                if (_txTracer.IsTracingInstructions) _txTracer.ReportStackPush(new byte[] {value});
                
                stack.Slice(stackHead * 32, 32).Clear();
                stack[stackHead * 32 + 31] = value;
                stackHead++;

                if (stackHead >= VmParams.MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushOne(Span<byte> stack)
            {
                if (_txTracer.IsTracingInstructions) _txTracer.ReportStackPush(new byte[] {1});
                
                stack.Slice(stackHead * 32, 32).Clear();
                stack[stackHead * 32 + 31] = 1;
                stackHead++;
                if (stackHead >= VmParams.MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushZero(Span<byte> stack)
            {
                if (_txTracer.IsTracingInstructions) _txTracer.ReportStackPush(new byte[] {0});
                
                stack.Slice(stackHead * 32, 32).Clear();
                stackHead++;
                if (stackHead >= VmParams.MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushUInt256(ref UInt256 value, Span<byte> stack)
            {
                Span<byte> target = stack.Slice(stackHead * 32, 32);
                value.ToBigEndian(target);
                
                if (_txTracer.IsTracingInstructions) _txTracer.ReportStackPush(target);
                
                stackHead++;
                if (stackHead >= VmParams.MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }
            
            void PushUInt(ref BigInteger value, Span<byte> stack)
            {
                Span<byte> target = stack.Slice(stackHead * 32, 32);
                int bytesToWrite = value.GetByteCount(true);
                if (bytesToWrite != 32)
                {
                    target.Clear();
                    target = target.Slice(32 - bytesToWrite, bytesToWrite);
                }

                value.TryWriteBytes(target, out int bytesWritten, true, true);

                if (_txTracer.IsTracingInstructions) _txTracer.ReportStackPush(target);
                
                stackHead++;
                if (stackHead >= VmParams.MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            void PushSignedInt(ref BigInteger value, Span<byte> stack)
            {
                Span<byte> target = stack.Slice(stackHead * 32, 32);
                int bytesToWrite = value.GetByteCount(false);
                bool treatAsUnsigned = bytesToWrite == 33;
                if (treatAsUnsigned)
                {
                    bytesToWrite = 32;
                }

                if (bytesToWrite != 32)
                {
                    if (value.Sign >= 0)
                    {
                        target.Clear();
                    }
                    else
                    {
                        target.Fill(0xff);
                    }

                    target = target.Slice(32 - bytesToWrite, bytesToWrite);
                }

                value.TryWriteBytes(target, out int bytesWritten, treatAsUnsigned, true);

                if (_txTracer.IsTracingInstructions) _txTracer.ReportStackPush(target);
                
                stackHead++;
                if (stackHead >= VmParams.MaxStackSize)
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
                    throw new EvmStackUnderflowException();
                }

                stackHead--;
            }

            void Dup(int depth, Span<byte> stack)
            {
                if (stackHead < depth)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackUnderflowException();
                }

                stack.Slice((stackHead - depth) * 32, 32).CopyTo(stack.Slice(stackHead * 32, 32));
                if (_txTracer.IsTracingInstructions)
                {
                    for (int i = depth; i >= 0; i--)
                    {
                        _txTracer.ReportStackPush(stack.Slice(stackHead * 32 - i * 32, 32));    
                    }
                }
                
                stackHead++;
                if (stackHead >= VmParams.MaxStackSize)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackOverflowException();
                }
            }

            byte[] wordBufferArray = new byte[32];
            Span<byte> wordBuffer = wordBufferArray.AsSpan();

            void Swap(int depth, Span<byte> stack, Span<byte> buffer)
            {
                if (stackHead < depth)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackUnderflowException();
                }

                Span<byte> bottomSpan = stack.Slice((stackHead - depth) * 32, 32);
                Span<byte> topSpan = stack.Slice((stackHead - 1) * 32, 32);

                bottomSpan.CopyTo(buffer);
                topSpan.CopyTo(bottomSpan);
                buffer.CopyTo(topSpan);
                
                if (_txTracer.IsTracingInstructions)
                {
                    for (int i = depth; i > 0; i--)
                    {
                        _txTracer.ReportStackPush(stack.Slice(stackHead * 32 - i * 32, 32));    
                    }
                }
            }

            Span<byte> PopBytes(Span<byte> stack)
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackUnderflowException();
                }

                stackHead--;

                return stack.Slice(stackHead * 32, 32);
            }

            byte PopByte(Span<byte> stack)
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackUnderflowException();
                }

                stackHead--;

                return stack[stackHead * 32 + 31];
            }

            List<string> GetStackTrace(Span<byte> stack)
            {
                List<string> stackTrace = new List<string>();
                for (int i = 0; i < stackHead; i++)
                {
                    Span<byte> stackItem = stack.Slice(i * 32, 32);
                    stackTrace.Add(stackItem.ToArray().ToHexString());
                }

                return stackTrace;
            }

            void PopUInt256(out UInt256 result, Span<byte> stack)
            {
                UInt256.CreateFromBigEndian(out result, PopBytes(stack));
            }
            
            void PopUInt(out BigInteger result, Span<byte> stack)
            {
                result = PopBytes(stack).ToUnsignedBigInteger();
            }

            void PopInt(out BigInteger result, Span<byte> stack)
            {
                result = PopBytes(stack).ToSignedBigInteger(32);
            }

            Address PopAddress(Span<byte> stack)
            {
                if (stackHead == 0)
                {
                    Metrics.EvmExceptions++;
                    throw new EvmStackUnderflowException();
                }

                stackHead--;

                return new Address(stack.Slice(stackHead * 32 + 12, 20).ToArray());
            }

            void UpdateMemoryCost(ref UInt256 position, in UInt256 length)
            {
                long memoryCost = vmState.Memory.CalculateMemoryCost(ref position, length);
                if (memoryCost != 0L)
                {
                    if (!UpdateGas(memoryCost, ref gasAvailable))
                    {
                        Metrics.EvmExceptions++;
                        EndInstructionTraceError(OutOfGasErrorText);
                        throw new OutOfGasException();
                    }
                }
            }

            if (previousCallResult != null)
            {
                PushBytes(previousCallResult, bytesOnStack);
                if(_txTracer.IsTracingInstructions) _txTracer.ReportOperationRemainingGas(vmState.GasAvailable);
            }

            if (previousCallOutput.Length > 0)
            {
                UInt256 localPreviousDest = previousCallOutputDestination;
                UpdateMemoryCost(ref localPreviousDest, (ulong)previousCallOutput.Length);
                vmState.Memory.Save(ref localPreviousDest, previousCallOutput);
//                if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)localPreviousDest, previousCallOutput);
            }

            while (programCounter < code.Length)
            {
                Instruction instruction = (Instruction)code[(int)programCounter];
                if (traceOpcodes)
                {
                    StartInstructionTrace(instruction, bytesOnStack);
                }

                programCounter++;
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
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        // TODO: can calculate in place...
                        PopUInt256(out UInt256 b, bytesOnStack);
                        PopUInt256(out UInt256 a, bytesOnStack);
                        UInt256.Add(out UInt256 c, ref a, ref b, false);
                        PushUInt256(ref c, bytesOnStack);
                        
                        break;
                    }
                    case Instruction.MUL:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger a, bytesOnStack);
                        PopUInt(out BigInteger b, bytesOnStack);
                        BigInteger res = BigInteger.Remainder(a * b, P256Int);
                        PushUInt(ref res, bytesOnStack);
                        break;
                    }
                    case Instruction.SUB:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        // TODO: can calculate in place...
                        PopUInt256(out UInt256 a, bytesOnStack);
                        PopUInt256(out UInt256 b, bytesOnStack);
                        UInt256 result = a - b;

                        PushUInt256(ref result, bytesOnStack);
                        break;
                    }
                    case Instruction.DIV:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        // TODO: can calculate in place...
                        PopUInt(out BigInteger a, bytesOnStack);
                        PopUInt(out BigInteger b, bytesOnStack);
                        if (b.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            BigInteger res = BigInteger.Divide(a, b);
                            PushUInt(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SDIV:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopInt(out BigInteger a, bytesOnStack);
                        PopInt(out BigInteger b, bytesOnStack);
                        if (b.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else if (b == BigInteger.MinusOne && a == P255Int)
                        {
                            BigInteger res = P255;
                            PushUInt(ref res, bytesOnStack);
                        }
                        else
                        {
                            BigInteger res = BigInteger.Divide(a, b);
                            PushSignedInt(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.MOD:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger a, bytesOnStack);
                        PopUInt(out BigInteger b, bytesOnStack);
                        BigInteger res = b.IsZero ? BigInteger.Zero : BigInteger.Remainder(a, b);
                        PushUInt(ref res, bytesOnStack);
                        break;
                    }
                    case Instruction.SMOD:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopInt(out BigInteger a, bytesOnStack);
                        PopInt(out BigInteger b, bytesOnStack);
                        if (b.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            BigInteger res = a.Sign * BigInteger.Remainder(a.Abs(), b.Abs());
                            PushSignedInt(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.ADDMOD:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger a, bytesOnStack);
                        PopUInt(out BigInteger b, bytesOnStack);
                        PopUInt(out BigInteger mod, bytesOnStack);

                        if (mod.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            BigInteger res = BigInteger.Remainder(a + b, mod);
                            PushUInt(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.MULMOD:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger a, bytesOnStack);
                        PopUInt(out BigInteger b, bytesOnStack);
                        PopUInt(out BigInteger mod, bytesOnStack);

                        if (mod.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            BigInteger res = BigInteger.Remainder(a * b, mod);
                            PushUInt(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.EXP:
                    {
                        if (!UpdateGas(GasCostOf.Exp, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Metrics.ModExpOpcode++;

                        PopUInt(out BigInteger baseInt, bytesOnStack);
                        Span<byte> exp = PopBytes(bytesOnStack);
                        
                        int leadingZeros = exp.LeadingZerosCount();
                        if (leadingZeros != 32)
                        {
                            int expSize = 32 - leadingZeros;
                            if (!UpdateGas((spec.IsEip160Enabled ? GasCostOf.ExpByteEip160 : GasCostOf.ExpByte) * expSize, ref gasAvailable))
                            {
                                EndInstructionTraceError(OutOfGasErrorText);
                                return CallResult.OutOfGasException;
                            }
                        }
                        else
                        {
                            PushOne(bytesOnStack);
                            break;
                        }

                        if (baseInt.IsZero)
                        {
                            PushZero(bytesOnStack);
                        }
                        else if (baseInt.IsOne)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            BigInteger res = BigInteger.ModPow(baseInt, exp.ToUnsignedBigInteger(), P256Int);
                            PushUInt(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SIGNEXTEND:
                    {
                        if (!UpdateGas(GasCostOf.Low, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger a, bytesOnStack);
                        if (a >= BigInt32)
                        {
                            break;
                        }

                        int position = 31 - (int)a;

                        Span<byte> b = PopBytes(bytesOnStack);
                        sbyte sign = (sbyte)b[position];

                        if (sign >= 0)
                        {
                            BytesZero32.AsSpan(0, position).CopyTo(b.Slice(0, position));
                        }
                        else
                        {
                            BytesMax32.AsSpan(0, position).CopyTo(b.Slice(0, position));
                        }

                        PushBytes(b, bytesOnStack);
                        break;
                    }
                    case Instruction.LT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger a, bytesOnStack);
                        PopUInt(out BigInteger b, bytesOnStack);
                        if (a < b)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.GT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger a, bytesOnStack);
                        PopUInt(out BigInteger b, bytesOnStack);
                        if (a > b)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SLT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopInt(out BigInteger a, bytesOnStack);
                        PopInt(out BigInteger b, bytesOnStack);

                        if (BigInteger.Compare(a, b) < 0)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SGT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopInt(out BigInteger a, bytesOnStack);
                        PopInt(out BigInteger b, bytesOnStack);
                        if (BigInteger.Compare(a, b) > 0)
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.EQ:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        Span<byte> b = PopBytes(bytesOnStack);
                        if (a.SequenceEqual(b))
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.ISZERO:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        if (a.SequenceEqual(BytesZero32))
                        {
                            PushOne(bytesOnStack);
                        }
                        else
                        {
                            PushZero(bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.AND:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        Span<byte> b = PopBytes(bytesOnStack);
        
                        if (_simdOperationsEnabled)
                        {
                            Vector<byte> aVec = new Vector<byte>(a);
                            Vector<byte> bVec = new Vector<byte>(b);

                            Vector.BitwiseAnd(aVec, bVec).CopyTo(wordBufferArray);
                        }
                        else
                        {
                            for (int i = 0; i < 32; i++)
                            {
                                wordBuffer[i] = (byte)(a[i] & b[i]);
                            }
                        }

                        PushBytes(wordBufferArray, bytesOnStack);
                        break;
                    }
                    case Instruction.OR:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        Span<byte> b = PopBytes(bytesOnStack);
        
                        if (_simdOperationsEnabled)
                        {
                            Vector<byte> aVec = new Vector<byte>(a);
                            Vector<byte> bVec = new Vector<byte>(b);

                            Vector.BitwiseOr(aVec, bVec).CopyTo(wordBufferArray);
                        }
                        else
                        {
                            for (int i = 0; i < 32; i++)
                            {
                                wordBuffer[i] = (byte)(a[i] | b[i]);
                            }
                        }

                        PushBytes(wordBufferArray, bytesOnStack);
                        break;
                    }
                    case Instruction.XOR:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);
                        Span<byte> b = PopBytes(bytesOnStack);

                        if (_simdOperationsEnabled)
                        {
                            Vector<byte> aVec = new Vector<byte>(a);
                            Vector<byte> bVec = new Vector<byte>(b);

                            Vector.Xor(aVec, bVec).CopyTo(wordBufferArray);
                        }
                        else
                        {
                            for (int i = 0; i < 32; i++)
                            {
                                wordBuffer[i] = (byte)(a[i] ^ b[i]);
                            }
                        }

                        PushBytes(wordBufferArray, bytesOnStack);
                        break;
                    }
                    case Instruction.NOT:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Span<byte> a = PopBytes(bytesOnStack);

                        if (_simdOperationsEnabled)
                        {
                            Vector<byte> aVec = new Vector<byte>(a);
                            Vector<byte> negVec = Vector.Xor(aVec, new Vector<byte>(BytesMax32));

                            negVec.CopyTo(wordBufferArray);
                        }
                        else
                        {
                            for (int i = 0; i < 32; ++i)
                            {
                                wordBufferArray[i] = (byte)~a[i];
                            }
                        }

                        PushBytes(wordBufferArray, bytesOnStack);
                        break;
                    }
                    case Instruction.BYTE:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger position, bytesOnStack);
                        Span<byte> bytes = PopBytes(bytesOnStack);

                        if (position >= BigInt32)
                        {
                            PushZero(bytesOnStack);
                            break;
                        }

                        int adjustedPosition = bytes.Length - 32 + (int)position;
                        if (adjustedPosition < 0)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushByte(bytes[adjustedPosition], bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SHA3:
                    {
                        PopUInt256(out UInt256 memSrc, bytesOnStack);
                        PopUInt256(out UInt256 memLength, bytesOnStack);
                        if (!UpdateGas(GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(memLength),
                            ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref memSrc, memLength);

                        Span<byte> memData = vmState.Memory.LoadSpan(ref memSrc, memLength);
                        PushBytes(ValueKeccak.Compute(memData).BytesAsSpan, bytesOnStack);
                        break;
                    }
                    case Instruction.ADDRESS:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(env.ExecutingAccount.Bytes, bytesOnStack);
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
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Address address = PopAddress(bytesOnStack);
                        UInt256 balance = _state.GetBalance(address);
                        PushUInt256(ref balance, bytesOnStack);
                        break;
                    }
                    case Instruction.CALLER:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(env.Sender.Bytes, bytesOnStack);
                        break;
                    }
                    case Instruction.CALLVALUE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 callValue = env.Value;
                        PushUInt256(ref callValue, bytesOnStack);
                        break;
                    }
                    case Instruction.ORIGIN:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(env.Originator.Bytes, bytesOnStack);
                        break;
                    }
                    case Instruction.CALLDATALOAD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger src, bytesOnStack);
                        PushBytes(env.InputData.SliceWithZeroPadding(src, 32), bytesOnStack);
                        break;
                    }
                    case Instruction.CALLDATASIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        BigInteger callDataSize = env.InputData.Length; 
                        PushUInt(ref callDataSize, bytesOnStack);
                        break;
                    }
                    case Instruction.CALLDATACOPY:
                    {
                        PopUInt256(out UInt256 dest, bytesOnStack);
                        PopUInt256(out UInt256 src, bytesOnStack);
                        PopUInt256(out UInt256 length, bytesOnStack);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length),
                            ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dest, length);

                        byte[] callDataSlice = env.InputData.SliceWithZeroPadding(src, (int)length);
                        vmState.Memory.Save(ref dest, callDataSlice);
                        if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)dest, callDataSlice);
                        break;
                    }
                    case Instruction.CODESIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        BigInteger codeLength = code.Length; 
                        PushUInt(ref codeLength, bytesOnStack);
                        break;
                    }
                    case Instruction.CODECOPY:
                    {
                        PopUInt256(out UInt256 dest, bytesOnStack);
                        PopUInt256(out UInt256 src, bytesOnStack);
                        PopUInt256(out UInt256 length, bytesOnStack);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length), ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dest, length);
                        Span<byte> codeSlice = code.SliceWithZeroPadding(src, (int)length);
                        vmState.Memory.Save(ref dest, codeSlice);
                        if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)dest, codeSlice);
                        break;
                    }
                    case Instruction.GASPRICE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 gasPrice = env.GasPrice; 
                        PushUInt256(ref gasPrice, bytesOnStack);
                        break;
                    }
                    case Instruction.EXTCODESIZE:
                    {
                        if (!UpdateGas(spec.IsEip150Enabled ? GasCostOf.ExtCodeSizeEip150 : GasCostOf.ExtCodeSize, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Address address = PopAddress(bytesOnStack);
                        byte[] accountCode = GetCachedCodeInfo(address)?.MachineCode;
                        BigInteger codeSize = accountCode?.Length ?? BigInteger.Zero;
                        PushUInt(ref codeSize, bytesOnStack);
                        break;
                    }
                    case Instruction.EXTCODECOPY:
                    {
                        Address address = PopAddress(bytesOnStack);
                        PopUInt256(out UInt256 dest, bytesOnStack);
                        PopUInt256(out UInt256 src, bytesOnStack);
                        PopUInt256(out UInt256 length, bytesOnStack);
                        if (!UpdateGas((spec.IsEip150Enabled ? GasCostOf.ExtCodeEip150 : GasCostOf.ExtCode) + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length),
                            ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dest, length);
                        byte[] externalCode = GetCachedCodeInfo(address)?.MachineCode;
                        byte[] callDataSlice = externalCode.SliceWithZeroPadding(src, (int)length);
                        vmState.Memory.Save(ref dest, callDataSlice);
                        if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)dest, callDataSlice);
                        break;
                    }
                    case Instruction.RETURNDATASIZE:
                    {
                        if (!spec.IsEip211Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }

                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        BigInteger res = _returnDataBuffer.Length; 
                        PushUInt(ref res, bytesOnStack);
                        break;
                    }
                    case Instruction.RETURNDATACOPY:
                    {
                        if (!spec.IsEip211Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }

                        PopUInt256(out UInt256 dest, bytesOnStack);
                        PopUInt256(out UInt256 src, bytesOnStack);
                        PopUInt256(out UInt256 length, bytesOnStack);
                        if (!UpdateGas(GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(length), ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dest, length);

                        if (UInt256.AddWouldOverflow(ref length, ref src) || length + src > _returnDataBuffer.Length)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.AccessViolationException;
                        }

                        byte[] returnDataSlice = _returnDataBuffer.SliceWithZeroPadding(src, (int)length);
                        vmState.Memory.Save(ref dest, returnDataSlice);
                        if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)dest, returnDataSlice);
                        break;
                    }
                    case Instruction.BLOCKHASH:
                    {
                        Metrics.BlockhashOpcode++;
                        
                        if (!UpdateGas(GasCostOf.BlockHash, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 a, bytesOnStack);
                        long number = a > long.MaxValue ? long.MaxValue : (long)a;
                        PushBytes(_blockhashProvider.GetHash(env.CurrentBlock, number) ?? BytesZero32, bytesOnStack);

                        break;
                    }
                    case Instruction.COINBASE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(env.CurrentBlock.GasBeneficiary.Bytes, bytesOnStack);
                        break;
                    }
                    case Instruction.DIFFICULTY:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 diff = env.CurrentBlock.Difficulty;
                        PushUInt256(ref diff, bytesOnStack);
                        break;
                    }
                    case Instruction.TIMESTAMP:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 timestamp = env.CurrentBlock.Timestamp;
                        PushUInt256(ref timestamp, bytesOnStack);
                        break;
                    }
                    case Instruction.NUMBER:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 blockNumber = (UInt256)env.CurrentBlock.Number;
                        PushUInt256(ref blockNumber, bytesOnStack);
                        break;
                    }
                    case Instruction.GASLIMIT:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 gasLimit = (UInt256)env.CurrentBlock.GasLimit; 
                        PushUInt256(ref gasLimit, bytesOnStack);
                        break;
                    }
                    case Instruction.CHAINID:
                    {
                        if (!spec.IsEip1344Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PushBytes(_chainId, bytesOnStack);
                        break;
                    }
                    case Instruction.SELFBALANCE:
                    {
                        if (!spec.IsEip1884Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        if (!UpdateGas(GasCostOf.SelfBalance, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }
                        
                        UInt256 balance = _state.GetBalance(env.ExecutingAccount);
                        PushUInt256(ref balance, bytesOnStack);
                        break;
                    }
                    case Instruction.POP:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopLimbo();
                        break;
                    }
                    case Instruction.MLOAD:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 memPosition, bytesOnStack);
                        UpdateMemoryCost(ref memPosition, 32);
                        Span<byte> memData = vmState.Memory.LoadSpan(ref memPosition);
                        if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)memPosition, memData);
                        
                        PushBytes(memData, bytesOnStack);
                        break;
                    }
                    case Instruction.MSTORE:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 memPosition, bytesOnStack);
                        Span<byte> data = PopBytes(bytesOnStack);
                        UpdateMemoryCost(ref memPosition, 32);
                        vmState.Memory.SaveWord(ref memPosition, data);
                        if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)memPosition, data.PadLeft(32));
                        
                        break;
                    }
                    case Instruction.MSTORE8:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 memPosition, bytesOnStack);
                        byte data = PopByte(bytesOnStack);
                        UpdateMemoryCost(ref memPosition, UInt256.One);
                        vmState.Memory.SaveByte(ref memPosition, data);
                        if(_txTracer.IsTracingInstructions) _txTracer.ReportMemoryChange((long)memPosition, new [] {data});
                        
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
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }
                        
                        PopUInt256(out UInt256 storageIndex, bytesOnStack);
                        byte[] value = _storage.Get(new StorageAddress(env.ExecutingAccount, storageIndex));
                        PushBytes(value, bytesOnStack);
                        break;
                    }
                    case Instruction.SSTORE:
                    {
                        Metrics.SstoreOpcode++;
                        
                        if (vmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        bool useNetMetering = spec.IsEip1283Enabled | spec.IsEip2200Enabled;
                        // fail fast before the first storage read if gas is not enough even for reset
                        if (!useNetMetering && !UpdateGas(GasCostOf.SReset, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }
                        
                        if (spec.IsEip2200Enabled && gasAvailable <= GasCostOf.CallStipend)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 storageIndex, bytesOnStack);
                        byte[] newValue = PopBytes(bytesOnStack).WithoutLeadingZeros().ToArray();
                        bool newIsZero = newValue.IsZero();

                        StorageAddress storageAddress = new StorageAddress(env.ExecutingAccount, storageIndex);
                        byte[] currentValue = _storage.Get(storageAddress);
                        bool currentIsZero = currentValue.IsZero();
                        
                        bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, newValue);

                        if (!useNetMetering) // note that for this case we already deducted 5000
                        {
                            if (newIsZero)
                            {
                                if (!newSameAsCurrent)
                                {
                                    vmState.Refund += RefundOf.SClear;
                                }
                            }
                            else if (currentIsZero)
                            {
                                if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable))
                                {
                                    EndInstructionTraceError(OutOfGasErrorText);
                                    return CallResult.OutOfGasException;    
                                }
                            }
                        }
                        else // eip1283enabled
                        {
                            if (newSameAsCurrent)
                            {
                                if(!UpdateGas(GasCostOf.SStoreNetMetered, ref gasAvailable))
                                {
                                    EndInstructionTraceError(OutOfGasErrorText);
                                    return CallResult.OutOfGasException;
                                }
                            }
                            else // eip1283enabled, C != N
                            {
                                byte[] originalValue = _storage.GetOriginal(storageAddress);
                                bool originalIsZero = originalValue.IsZero();

                                bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);
                                if (currentSameAsOriginal)
                                {
                                    if (currentIsZero)
                                    {
                                        if (!UpdateGas(GasCostOf.SSet, ref gasAvailable))
                                        {
                                            EndInstructionTraceError(OutOfGasErrorText);
                                            return CallResult.OutOfGasException;
                                        }
                                    }
                                    else // eip1283enabled, C == O != N, !currentIsZero
                                    {
                                        if (!UpdateGas(GasCostOf.SReset, ref gasAvailable))
                                        {
                                            EndInstructionTraceError(OutOfGasErrorText);
                                            return CallResult.OutOfGasException;
                                        }

                                        if (newIsZero)
                                        {
                                            vmState.Refund += RefundOf.SClear;
                                        }
                                    }
                                }
                                else // eip1283enabled, N != C != O
                                {
                                    if (!UpdateGas(GasCostOf.SStoreNetMetered, ref gasAvailable))
                                    {
                                        EndInstructionTraceError(OutOfGasErrorText);
                                        return CallResult.OutOfGasException;
                                    }

                                    if (!originalIsZero) // eip1283enabled, N != C != O != 0
                                    {
                                        if (currentIsZero)
                                        {
                                            vmState.Refund -= RefundOf.SClear; 
                                        }

                                        if (newIsZero)
                                        {
                                            vmState.Refund += RefundOf.SClear;
                                        }
                                    }
                                    
                                    bool newSameAsOriginal = Bytes.AreEqual(originalValue, newValue);
                                    if(newSameAsOriginal)
                                    {
                                        if (originalIsZero)
                                        {
                                            vmState.Refund += RefundOf.SSetReversed;
                                        }
                                        else
                                        {
                                            vmState.Refund += RefundOf.SClearReversed;
                                        }
                                    }
                                }  
                            }
                        }

                        if (!newSameAsCurrent)
                        {
                            byte[] valueToStore = newIsZero ? BytesZero : newValue;
                            _storage.Set(storageAddress, valueToStore);
                        }
                        
                        if (_txTracer.IsTracingInstructions)
                        {
                            byte[] valueToStore = newIsZero ? BytesZero : newValue;
                            Span<byte> span = stackalloc byte[32];
                            storageAddress.Index.ToBigEndian(span);
                            _txTracer.ReportStorageChange(span, valueToStore);
                        }
                        
                        if (_txTracer.IsTracingOpLevelStorage)
                        {
                            _txTracer.SetOperationStorage(storageAddress.Address, storageIndex, newValue, currentValue);
                        }

                        break;
                    }
                    case Instruction.JUMP:
                    {
                        if (!UpdateGas(GasCostOf.Mid, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 jumpDest, bytesOnStack);
                        if (jumpDest > int.MaxValue)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(EvmExceptionType.InvalidJumpDestination);
                            // https://github.com/NethermindEth/nethermind/issues/140
                            throw new InvalidJumpDestinationException();
//                            return CallResult.InvalidJumpDestination;
                        }

                        int dest = (int)jumpDest;
                        if (!env.CodeInfo.ValidateJump(dest))
                        {
                            // https://github.com/NethermindEth/nethermind/issues/140
                            EndInstructionTraceError(EvmExceptionType.InvalidJumpDestination);
                            throw new InvalidJumpDestinationException();
//                            return CallResult.InvalidJumpDestination;
                        }

                        programCounter = jumpDest;
                        break;
                    }
                    case Instruction.JUMPI:
                    {
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 jumpDest, bytesOnStack);
                        Span<byte> condition = PopBytes(bytesOnStack);
                        if (!condition.SequenceEqual(BytesZero32))
                        {
                            if (jumpDest > int.MaxValue)
                            {
                                Metrics.EvmExceptions++;
                                EndInstructionTraceError(EvmExceptionType.InvalidJumpDestination);
                                // https://github.com/NethermindEth/nethermind/issues/140
                                throw new InvalidJumpDestinationException();
//                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
                            }

                            int dest = (int)jumpDest;

                            if (!env.CodeInfo.ValidateJump(dest))
                            {
                                EndInstructionTraceError(EvmExceptionType.InvalidJumpDestination);
                                // https://github.com/NethermindEth/nethermind/issues/140
                                throw new InvalidJumpDestinationException();
//                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
                            }

                            programCounter = jumpDest;
                        }

                        break;
                    }
                    case Instruction.PC:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 pc = programCounter - 1L;
                        PushUInt256(ref pc, bytesOnStack);
                        break;
                    }
                    case Instruction.MSIZE:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 size = vmState.Memory.Size;
                        PushUInt256(ref size, bytesOnStack);
                        break;
                    }
                    case Instruction.GAS:
                    {
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UInt256 gas = (UInt256)gasAvailable;
                        PushUInt256(ref gas, bytesOnStack);
                        break;
                    }
                    case Instruction.JUMPDEST:
                    {
                        if (!UpdateGas(GasCostOf.JumpDest, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        break;
                    }
                    case Instruction.PUSH1:
                    {
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        int programCounterInt = (int)programCounter;
                        if (programCounterInt >= code.Length)
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushByte(code[programCounterInt], bytesOnStack);
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
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        int length = instruction - Instruction.PUSH1 + 1;
                        int programCounterInt = (int)programCounter;
                        int usedFromCode = Math.Min(code.Length - programCounterInt, length);

                        PushLeftPaddedBytes(code.Slice(programCounterInt, usedFromCode), length, bytesOnStack);

                        programCounter += (ulong)length;
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
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Dup(instruction - Instruction.DUP1 + 1, bytesOnStack);
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
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Swap(instruction - Instruction.SWAP1 + 2, bytesOnStack, wordBuffer);
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
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        PopUInt256(out UInt256 memoryPos, bytesOnStack);
                        PopUInt256(out UInt256 length, bytesOnStack);
                        long topicsCount = instruction - Instruction.LOG0;
                        UpdateMemoryCost(ref memoryPos, length);
                        if (!UpdateGas(
                            GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                            (long)length * GasCostOf.LogData, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        byte[] data = vmState.Memory.Load(ref memoryPos, length);
                        Keccak[] topics = new Keccak[topicsCount];
                        for (int i = 0; i < topicsCount; i++)
                        {
                            topics[i] = new Keccak(PopBytes(bytesOnStack).ToArray());
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
                        if(!spec.IsEip1014Enabled && instruction == Instruction.CREATE2)
                        {
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        if (vmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
                        if (!_state.AccountExists(env.ExecutingAccount))
                        {
                            _state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
                        }

                        PopUInt256(out UInt256 value, bytesOnStack);
                        PopUInt256(out UInt256 memoryPositionOfInitCode, bytesOnStack);
                        PopUInt256(out UInt256 initCodeLength, bytesOnStack);
                        Span<byte> salt = null;
                        if (instruction == Instruction.CREATE2)
                        {
                            salt = PopBytes(bytesOnStack);
                        }

                        long gasCost = GasCostOf.Create + (instruction == Instruction.CREATE2 ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(initCodeLength) : 0); 
                        if (!UpdateGas(gasCost, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref memoryPositionOfInitCode, initCodeLength);

                        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
                        if (env.CallDepth >= VmParams.MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
                        {
                            // TODO: need a test for this
                            _returnDataBuffer = new byte[0];
                            PushZero(bytesOnStack);
                            break;
                        }

                        Span<byte> initCode = vmState.Memory.LoadSpan(ref memoryPositionOfInitCode, initCodeLength);
                        UInt256 balance = _state.GetBalance(env.ExecutingAccount);
                        if (value > balance)
                        {
                            PushZero(bytesOnStack);
                            break;
                        }

                        EndInstructionTrace();
                        // todo: === below is a new call - refactor / move
                        
                        long callGas = spec.IsEip150Enabled ? gasAvailable - gasAvailable / 64L : gasAvailable;
                        if (!UpdateGas(callGas, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }
                        
                        Address contractAddress = instruction == Instruction.CREATE
                            ? Address.OfContract(env.ExecutingAccount, _state.GetNonce(env.ExecutingAccount))
                            : Address.OfContract(env.ExecutingAccount, salt, initCode);
                            
                        _state.IncrementNonce(env.ExecutingAccount);

                        int stateSnapshot = _state.TakeSnapshot();
                        int storageSnapshot = _storage.TakeSnapshot();
                        
                        bool accountExists = _state.AccountExists(contractAddress);
                        if (accountExists && ((GetCachedCodeInfo(contractAddress)?.MachineCode?.Length ?? 0) != 0 || _state.GetNonce(contractAddress) != 0))
                        {
                            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
                            if (isTrace) _logger.Trace($"Contract collision at {contractAddress}");
                            PushZero(bytesOnStack);
                            break;
                        }

                        if (accountExists)
                        {
                            _state.UpdateStorageRoot(contractAddress, Keccak.EmptyTreeHash);
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
                        VmState callState = new VmState(
                            callGas,
                            callEnv,
                            ExecutionType.Create,
                            false,
                            false,
                            stateSnapshot,
                            storageSnapshot,
                            0L,
                            0L,
                            vmState.IsStatic,
                            false);

                        UpdateCurrentState();
                        return new CallResult(callState);
                    }
                    case Instruction.RETURN:
                    {
                        PopUInt256(out UInt256 memoryPos, bytesOnStack);
                        PopUInt256(out UInt256 length, bytesOnStack);

                        UpdateMemoryCost(ref memoryPos, length);
                        byte[] returnData = vmState.Memory.Load(ref memoryPos, length);

                        UpdateCurrentState();
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
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }

                        PopUInt(out BigInteger gasLimit, bytesOnStack);                        
                        Address codeSource = PopAddress(bytesOnStack);
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
                                PopUInt256(out callValue, bytesOnStack);
                                break;
                        }

                        UInt256 transferValue = instruction == Instruction.DELEGATECALL ? UInt256.Zero : callValue;
                        PopUInt256(out UInt256 dataOffset, bytesOnStack);
                        PopUInt256(out UInt256 dataLength, bytesOnStack);
                        PopUInt256(out UInt256 outputOffset, bytesOnStack);
                        PopUInt256(out UInt256 outputLength, bytesOnStack);

                        if (vmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        bool isPrecompile = IsPrecompiled(codeSource, spec);
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
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        UpdateMemoryCost(ref dataOffset, dataLength);
                        UpdateMemoryCost(ref outputOffset, outputLength);
                        if (!UpdateGas(gasExtra, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        if (spec.IsEip150Enabled)
                        {
                            gasLimit = BigInteger.Min(gasAvailable - gasAvailable / 64L, gasLimit);
                        }

                        long gasLimitUl = (long)gasLimit;
                        if (!UpdateGas(gasLimitUl, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        if (!transferValue.IsZero)
                        {
                            gasLimitUl += GasCostOf.CallStipend;
                        }

                        if (env.CallDepth >= VmParams.MaxCallDepth || !transferValue.IsZero && _state.GetBalance(env.ExecutingAccount) < transferValue)
                        {
                            _returnDataBuffer = new byte[0];
                            PushZero(bytesOnStack);
                            
                            if (_txTracer.IsTracingInstructions)
                            {
                                // very specific for Parity trace, need to find generalization - very peculiar 32 length...
                                byte[] memoryTrace = vmState.Memory.Load(ref dataOffset, 32);
                                _txTracer.ReportMemoryChange((long)dataOffset, memoryTrace);
                            }
                            
                            if (isTrace) _logger.Trace("FAIL - call depth");
                            if(_txTracer.IsTracingInstructions) _txTracer.ReportOperationRemainingGas(gasAvailable);
                            if(_txTracer.IsTracingInstructions) _txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
                            
                            RefundGas(gasLimitUl, ref gasAvailable);
                            if(_txTracer.IsTracingInstructions) _txTracer.ReportRefund(gasAvailable);
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
                        callEnv.CodeInfo = isPrecompile ? new CodeInfo(codeSource) : GetCachedCodeInfo(codeSource);

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
                            throw new NotImplementedException($"Execution type is undefined for {Enum.GetName(typeof(Instruction), instruction)}"); 
                        }
                        
                        VmState callState = new VmState(
                            gasLimitUl,
                            callEnv,
                            executionType,
                            isPrecompile,
                            false,
                            stateSnapshot,
                            storageSnapshot,
                            (long)outputOffset,
                            (long)outputLength,
                            instruction == Instruction.STATICCALL || vmState.IsStatic,
                            false);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        return new CallResult(callState);
                    }
                    case Instruction.REVERT:
                    {
                        if (!spec.IsEip140Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }

                        PopUInt256(out UInt256 memoryPos, bytesOnStack);
                        PopUInt256(out UInt256 length, bytesOnStack);

                        UpdateMemoryCost(ref memoryPos, length);
                        byte[] errorDetails = vmState.Memory.Load(ref memoryPos, length);

                        UpdateCurrentState();
                        EndInstructionTrace();
                        return new CallResult(errorDetails, null, true);
                    }
                    case Instruction.INVALID:
                    {
                        if (!UpdateGas(GasCostOf.High, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Metrics.EvmExceptions++;
                        EndInstructionTraceError(BadInstructionErrorText);
                        return CallResult.InvalidInstructionException;
                    }
                    case Instruction.SELFDESTRUCT:
                    {
                        if (vmState.IsStatic)
                        {
                            Metrics.EvmExceptions++;
                            return CallResult.StaticCallViolationException;
                        }

                        if (spec.IsEip150Enabled && !UpdateGas(GasCostOf.SelfDestructEip150, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Metrics.SelfDestructs++;

                        Address inheritor = PopAddress(bytesOnStack);
                        vmState.DestroyList.Add(env.ExecutingAccount);
                        _storage.Destroy(env.ExecutingAccount);
                        
                        UInt256 ownerBalance = _state.GetBalance(env.ExecutingAccount);
                        if(_txTracer.IsTracingActions) _txTracer.ReportSelfDestruct(env.ExecutingAccount, ownerBalance, inheritor);
                        if (spec.IsEip158Enabled && ownerBalance != 0 && _state.IsDeadAccount(inheritor))
                        {
                            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                            {
                                EndInstructionTraceError(OutOfGasErrorText);
                                return CallResult.OutOfGasException;
                            }
                        }

                        bool inheritorAccountExists = _state.AccountExists(inheritor);
                        if (!spec.IsEip158Enabled && !inheritorAccountExists && spec.IsEip150Enabled)
                        {
                            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                            {
                                EndInstructionTraceError(OutOfGasErrorText);
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

                        UpdateCurrentState();
                        EndInstructionTrace();
                        return CallResult.Empty;
                    }
                    case Instruction.SHL:
                    {
                        if (!spec.IsEip145Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 a, bytesOnStack);
                        if (a >= 256UL)
                        {
                            PopLimbo();
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PopUInt256(out UInt256 b, bytesOnStack);
                            BigInteger res = b << (int) a.S0;
                            PushSignedInt(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SHR:
                    {
                        if (!spec.IsEip145Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt256(out UInt256 a, bytesOnStack);
                        if (a >= 256)
                        {
                            PopLimbo();
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PopUInt256(out UInt256 b, bytesOnStack);
                            UInt256 res = b >> (int) a.S0;
                            PushUInt256(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.SAR:
                    {
                        if (!spec.IsEip145Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }
                        
                        if (!UpdateGas(GasCostOf.VeryLow, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        PopUInt(out BigInteger a, bytesOnStack);
                        PopInt(out BigInteger b, bytesOnStack);
                        if (a >= BigInt256)
                        {
                            if (b.Sign >= 0)
                            {
                                PushZero(bytesOnStack);
                            }
                            else
                            {
                                BigInteger res = BigInteger.MinusOne;
                                PushSignedInt(ref res, bytesOnStack);
                            }
                        }
                        else
                        {
                            BigInteger res = BigInteger.DivRem(b, BigInteger.Pow(2, (int)a), out BigInteger remainder);
                            if (remainder.Sign < 0)
                            {
                                res--;
                            }
                            
                            PushSignedInt(ref res, bytesOnStack);
                        }

                        break;
                    }
                    case Instruction.EXTCODEHASH:
                    {
                        if (!spec.IsEip1052Enabled)
                        {
                            Metrics.EvmExceptions++;
                            EndInstructionTraceError(BadInstructionErrorText);
                            return CallResult.InvalidInstructionException;
                        }

                        var gasCost = spec.IsEip1884Enabled ? GasCostOf.ExtCodeHashEip1884 : GasCostOf.ExtCodeHash;
                        if (!UpdateGas(gasCost, ref gasAvailable))
                        {
                            EndInstructionTraceError(OutOfGasErrorText);
                            return CallResult.OutOfGasException;
                        }

                        Address address = PopAddress(bytesOnStack);
                        if (!_state.AccountExists(address) || _state.IsDeadAccount(address))
                        {
                            PushZero(bytesOnStack);
                        }
                        else
                        {
                            PushBytes(_state.GetCodeHash(address).Bytes, bytesOnStack);
                        }

                        break;
                    }
                    default:
                    {
                        Metrics.EvmExceptions++;
                        EndInstructionTraceError(BadInstructionErrorText);
                        return CallResult.InvalidInstructionException;
                    }
                }

                EndInstructionTrace();
            }

            UpdateCurrentState();
            return CallResult.Empty;
        }
        
        private struct CallResult
        {
            public static CallResult OutOfGasException = new CallResult(EvmExceptionType.OutOfGas);
            public static CallResult AccessViolationException = new CallResult(EvmExceptionType.AccessViolation);
            public static CallResult InvalidJumpDestination = new CallResult(EvmExceptionType.InvalidJumpDestination);
            public static CallResult InvalidInstructionException = new CallResult(EvmExceptionType.BadInstruction);
            public static CallResult StaticCallViolationException = new CallResult(EvmExceptionType.StaticCallViolation);
            public static CallResult StackOverflowException = new CallResult(EvmExceptionType.StackOverflow); // TODO: use these to avoid CALL POP attacks
            public static CallResult StackUnderflowException = new CallResult(EvmExceptionType.StackUnderflow); // TODO: use these to avoid CALL POP attacks
            public static readonly CallResult Empty = new CallResult(Bytes.Empty, null);

            public CallResult(VmState stateToExecute)
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

            public bool ShouldRevert { get; }
            public bool? PrecompileSuccess { get; } // TODO: check this behaviour as it seems it is required and previously that was not the case

            public VmState StateToExecute { get; }
            public byte[] Output { get; }
            public bool IsReturn => StateToExecute == null;
            public bool IsException => ExceptionType != EvmExceptionType.None;

            public EvmExceptionType ExceptionType { get; }
        }
    }
}