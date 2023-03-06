// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Int256;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls.Shamatar;
using Nethermind.Evm.Precompiles.Snarks.Shamatar;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.Evm
{
    public partial class VirtualMachine : IVirtualMachine
    {
        public const int MaxCallDepth = 1024;

        private readonly static UInt256 P255Int = (UInt256)BigInteger.Pow(2, 255);
        private static UInt256 P255 => P255Int;
        private readonly static UInt256 BigInt256 = 256;
        public readonly static UInt256 BigInt32 = 32;

        internal byte[] BytesZero = { 0 };

        internal readonly static byte[] BytesZero32 =
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

        private readonly byte[] _chainId;

        private readonly IBlockhashProvider _blockhashProvider;
        private readonly ISpecProvider _specProvider;
        private static readonly LruCache<KeccakKey, CodeInfo> _codeCache = new(MemoryAllowance.CodeCacheSize, MemoryAllowance.CodeCacheSize, "VM bytecodes");
        private readonly ILogger _logger;
        private IWorldState _worldState;
        private IStateProvider _state;
        private readonly Stack<EvmState> _stateStack = new();
        private IStorageProvider _storage;
        private (Address Address, bool ShouldDelete) _parityTouchBugAccount = (Address.FromNumber(3), false);
        private Dictionary<Address, CodeInfo>? _precompiles;
        private byte[] _returnDataBuffer = Array.Empty<byte>();
        private ITxTracer _txTracer = NullTxTracer.Instance;

        public VirtualMachine(
            IBlockhashProvider? blockhashProvider,
            ISpecProvider? specProvider,
            ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _blockhashProvider = blockhashProvider ?? throw new ArgumentNullException(nameof(blockhashProvider));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _chainId = ((UInt256)specProvider.ChainId).ToBigEndian();
            InitializePrecompiledContracts();
        }

        public TransactionSubstate Run(EvmState state, IWorldState worldState, ITxTracer txTracer)
        {
            _txTracer = txTracer;

            _state = worldState.StateProvider;
            _storage = worldState.StorageProvider;
            _worldState = worldState;

            IReleaseSpec spec = _specProvider.GetSpec(state.Env.TxExecutionContext.Header.Number, state.Env.TxExecutionContext.Header.Timestamp);
            EvmState currentState = state;
            byte[] previousCallResult = null;
            ZeroPaddedSpan previousCallOutput = ZeroPaddedSpan.Empty;
            UInt256 previousCallOutputDestination = UInt256.Zero;
            while (true)
            {
                if (!currentState.IsContinuation)
                {
                    _returnDataBuffer = Array.Empty<byte>();
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
                            _returnDataBuffer = Array.Empty<byte>();
                            previousCallOutput = ZeroPaddedSpan.Empty;
                            continue;
                        }

                        if (callResult.IsException)
                        {
                            if (_txTracer.IsTracingActions) _txTracer.ReportActionError(callResult.ExceptionType);
                            _worldState.Restore(currentState.Snapshot);

                            RevertParityTouchBugAccount(spec);

                            if (currentState.IsTopLevel)
                            {
                                return new TransactionSubstate(callResult.ExceptionType, _txTracer != NullTxTracer.Instance);
                            }

                            previousCallResult = StatusCode.FailureBytes;
                            previousCallOutputDestination = UInt256.Zero;
                            _returnDataBuffer = Array.Empty<byte>();
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
                            long codeDepositGasCost = CodeDepositHandler.CalculateCost(callResult.Output.Length, spec);

                            if (callResult.IsException)
                            {
                                _txTracer.ReportActionError(callResult.ExceptionType);
                            }
                            else if (callResult.ShouldRevert)
                            {
                                if (currentState.ExecutionType.IsAnyCreate())
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.Revert, currentState.GasAvailable - codeDepositGasCost);
                                }
                                else
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.Revert, currentState.GasAvailable);
                                }
                            }
                            else
                            {
                                if (currentState.ExecutionType.IsAnyCreate() && currentState.GasAvailable < codeDepositGasCost)
                                {
                                    if (spec.ChargeForTopLevelCreate)
                                    {
                                        _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                                    }
                                    else
                                    {
                                        _txTracer.ReportActionEnd(currentState.GasAvailable, currentState.To, callResult.Output);
                                    }
                                }
                                // Reject code starting with 0xEF if EIP-3541 is enabled.
                                else if (currentState.ExecutionType.IsAnyCreate() && CodeDepositHandler.CodeIsInvalid(spec, callResult.Output))
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.InvalidCode);
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

                        return new TransactionSubstate(
                            callResult.Output,
                            currentState.Refund,
                            (IReadOnlyCollection<Address>)currentState.DestroyList,
                            (IReadOnlyCollection<LogEntry>)currentState.Logs,
                            callResult.ShouldRevert,
                            _txTracer != NullTxTracer.Instance);
                    }

                    Address callCodeOwner = currentState.Env.ExecutingAccount;
                    using EvmState previousState = currentState;
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
                            _returnDataBuffer = Array.Empty<byte>();
                            previousCallOutput = ZeroPaddedSpan.Empty;

                            long codeDepositGasCost = CodeDepositHandler.CalculateCost(callResult.Output.Length, spec);
                            bool invalidCode = CodeDepositHandler.CodeIsInvalid(spec, callResult.Output);
                            if (gasAvailableForCodeDeposit >= codeDepositGasCost && !invalidCode)
                            {
                                Keccak codeHash = _state.UpdateCode(callResult.Output);
                                _state.UpdateCodeHash(callCodeOwner, codeHash, spec);
                                currentState.GasAvailable -= codeDepositGasCost;

                                if (_txTracer.IsTracingActions)
                                {
                                    _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output);
                                }
                            }
                            else if (spec.FailOnOutOfGasCodeDeposit || invalidCode)
                            {
                                currentState.GasAvailable -= gasAvailableForCodeDeposit;
                                worldState.Restore(previousState.Snapshot);
                                if (!previousState.IsCreateOnPreExistingAccount)
                                {
                                    _state.DeleteAccount(callCodeOwner);
                                }

                                previousCallResult = BytesZero;
                                previousStateSucceeded = false;

                                if (_txTracer.IsTracingActions)
                                {
                                    _txTracer.ReportActionError(invalidCode ? EvmExceptionType.InvalidCode : EvmExceptionType.OutOfGas);
                                }
                            }
                            else if (_txTracer.IsTracingActions)
                            {
                                _txTracer.ReportActionEnd(0L, callCodeOwner, callResult.Output);
                            }
                        }
                        else
                        {
                            _returnDataBuffer = callResult.Output;
                            previousCallResult = callResult.PrecompileSuccess.HasValue ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes) : StatusCode.SuccessBytes;
                            previousCallOutput = callResult.Output.AsSpan().SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                            previousCallOutputDestination = (ulong)previousState.OutputDestination;
                            if (previousState.IsPrecompile)
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
                            previousState.CommitToParent(currentState);
                        }
                    }
                    else
                    {
                        worldState.Restore(previousState.Snapshot);
                        _returnDataBuffer = callResult.Output;
                        previousCallResult = StatusCode.FailureBytes;
                        previousCallOutput = callResult.Output.AsSpan().SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                        previousCallOutputDestination = (ulong)previousState.OutputDestination;


                        if (_txTracer.IsTracingActions)
                        {
                            _txTracer.ReportActionError(EvmExceptionType.Revert, previousState.GasAvailable);
                        }
                    }
                }
                catch (Exception ex) when (ex is EvmException or OverflowException)
                {
                    if (_logger.IsTrace) _logger.Trace($"exception ({ex.GetType().Name}) in {currentState.ExecutionType} at depth {currentState.Env.CallDepth} - restoring snapshot");

                    _worldState.Restore(currentState.Snapshot);

                    RevertParityTouchBugAccount(spec);

                    if (txTracer.IsTracingInstructions)
                    {
                        txTracer.ReportOperationError(ex is EvmException evmException ? evmException.ExceptionType : EvmExceptionType.Other);
                        txTracer.ReportOperationRemainingGas(0);
                    }

                    if (_txTracer.IsTracingActions)
                    {
                        EvmException evmException = ex as EvmException;
                        _txTracer.ReportActionError(evmException?.ExceptionType ?? EvmExceptionType.Other);
                    }

                    if (currentState.IsTopLevel)
                    {
                        return new TransactionSubstate(ex is OverflowException ? EvmExceptionType.Other : (ex as EvmException).ExceptionType, _txTracer != NullTxTracer.Instance);
                    }

                    previousCallResult = StatusCode.FailureBytes;
                    previousCallOutputDestination = UInt256.Zero;
                    _returnDataBuffer = Array.Empty<byte>();
                    previousCallOutput = ZeroPaddedSpan.Empty;

                    currentState.Dispose();
                    currentState = _stateStack.Pop();
                    currentState.IsContinuation = true;
                }
            }
        }

        private void RevertParityTouchBugAccount(IReleaseSpec spec)
        {
            if (_parityTouchBugAccount.ShouldDelete)
            {
                if (_state.AccountExists(_parityTouchBugAccount.Address))
                {
                    _state.AddToBalance(_parityTouchBugAccount.Address, UInt256.Zero, spec);
                }

                _parityTouchBugAccount.ShouldDelete = false;
            }
        }

        public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec vmSpec)
        {
            IStateProvider state = worldState.StateProvider;
            if (codeSource.IsPrecompile(vmSpec))
            {
                if (_precompiles is null)
                {
                    throw new InvalidOperationException("EVM precompile have not been initialized properly.");
                }

                return _precompiles[codeSource];
            }

            Keccak codeHash = state.GetCodeHash(codeSource);
            CodeInfo cachedCodeInfo = _codeCache.Get(codeHash);
            if (cachedCodeInfo is null)
            {
                byte[] code = state.GetCode(codeHash);

                if (code is null)
                {
                    throw new NullReferenceException($"Code {codeHash} missing in the state for address {codeSource}");
                }

                cachedCodeInfo = new CodeInfo(code);
                _codeCache.Set(codeHash, cachedCodeInfo);
            }
            else
            {
                // need to touch code so that any collectors that track database access are informed
                state.TouchCode(codeHash);
            }

            return cachedCodeInfo;
        }

        private void InitializePrecompiledContracts()
        {
            _precompiles = new Dictionary<Address, CodeInfo>
            {
                [EcRecoverPrecompile.Instance.Address] = new(EcRecoverPrecompile.Instance),
                [Sha256Precompile.Instance.Address] = new(Sha256Precompile.Instance),
                [Ripemd160Precompile.Instance.Address] = new(Ripemd160Precompile.Instance),
                [IdentityPrecompile.Instance.Address] = new(IdentityPrecompile.Instance),

                [Bn256AddPrecompile.Instance.Address] = new(Bn256AddPrecompile.Instance),
                [Bn256MulPrecompile.Instance.Address] = new(Bn256MulPrecompile.Instance),
                [Bn256PairingPrecompile.Instance.Address] = new(Bn256PairingPrecompile.Instance),
                [ModExpPrecompile.Instance.Address] = new(ModExpPrecompile.Instance),

                [Blake2FPrecompile.Instance.Address] = new(Blake2FPrecompile.Instance),

                [G1AddPrecompile.Instance.Address] = new(G1AddPrecompile.Instance),
                [G1MulPrecompile.Instance.Address] = new(G1MulPrecompile.Instance),
                [G1MultiExpPrecompile.Instance.Address] = new(G1MultiExpPrecompile.Instance),
                [G2AddPrecompile.Instance.Address] = new(G2AddPrecompile.Instance),
                [G2MulPrecompile.Instance.Address] = new(G2MulPrecompile.Instance),
                [G2MultiExpPrecompile.Instance.Address] = new(G2MultiExpPrecompile.Instance),
                [PairingPrecompile.Instance.Address] = new(PairingPrecompile.Instance),
                [MapToG1Precompile.Instance.Address] = new(MapToG1Precompile.Instance),
                [MapToG2Precompile.Instance.Address] = new(MapToG2Precompile.Instance),

                [PointEvaluationPrecompile.Instance.Address] = new(PointEvaluationPrecompile.Instance),
            };
        }

        private static bool UpdateMemoryCost(EvmPooledMemory? memory, ref long gasAvailable, in UInt256 position, in UInt256 length)
        {
            if (memory is null)
            {
                ThrowInvalidOperationException();
            }

            long memoryCost = memory.CalculateMemoryCost(in position, length);
            bool result = true;
            if (memoryCost > 0L
                && !UpdateGas(memoryCost, ref gasAvailable))
            {
                result = false;
            }

            return result;

            [DoesNotReturn]
            static void ThrowInvalidOperationException()
            {
                throw new InvalidOperationException("EVM memory has not been initialized properly.");
            }
        }

        private static bool UpdateGas(long gasCost, ref long gasAvailable)
        {
            if (gasAvailable < gasCost)
            {
                return false;
            }

            gasAvailable -= gasCost;
            return true;
        }

        private static void UpdateGasUp(long refund, ref long gasAvailable)
        {
            gasAvailable += refund;
        }

        private bool ChargeAccountAccessGas(ref long gasAvailable, EvmState vmState, Address address, IReleaseSpec spec, bool chargeForWarm = true)
        {
            // Console.WriteLine($"Accessing {address}");

            bool result = true;
            if (spec.UseHotAndColdStorage)
            {
                if (_txTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
                {
                    vmState.WarmUp(address);
                }

                if (vmState.IsCold(address) && !address.IsPrecompile(spec))
                {
                    result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
                    vmState.WarmUp(address);
                }
                else if (chargeForWarm)
                {
                    result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
                }
            }

            return result;
        }

        private enum StorageAccessType
        {
            SLOAD,
            SSTORE
        }

        private bool ChargeStorageAccessGas(
            ref long gasAvailable,
            EvmState vmState,
            StorageCell storageCell,
            StorageAccessType storageAccessType,
            IReleaseSpec spec)
        {
            // Console.WriteLine($"Accessing {storageCell} {storageAccessType}");

            bool result = true;
            if (spec.UseHotAndColdStorage)
            {
                if (_txTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
                {
                    vmState.WarmUp(storageCell);
                }

                if (vmState.IsCold(storageCell))
                {
                    result = UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
                    vmState.WarmUp(storageCell);
                }
                else if (storageAccessType == StorageAccessType.SLOAD)
                {
                    // we do not charge for WARM_STORAGE_READ_COST in SSTORE scenario
                    result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
                }
            }

            return result;
        }

        private CallResult ExecutePrecompile(EvmState state, IReleaseSpec spec)
        {
            ReadOnlyMemory<byte> callData = state.Env.InputData;
            UInt256 transferValue = state.Env.TransferValue;
            long gasAvailable = state.GasAvailable;

            IPrecompile precompile = state.Env.CodeInfo.Precompile;
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

            // https://github.com/ethereum/EIPs/blob/master/EIPS/eip-161.md
            // An additional issue was found in Parity,
            // where the Parity client incorrectly failed
            // to revert empty account deletions in a more limited set of contexts
            // involving out-of-gas calls to precompiled contracts;
            // the new Geth behavior matches Parity’s,
            // and empty accounts will cease to be a source of concern in general
            // in about one week once the state clearing process finishes.
            if (state.Env.ExecutingAccount.Equals(_parityTouchBugAccount.Address)
                && !wasCreated
                && transferValue.IsZero
                && spec.ClearEmptyAccountWhenTouched)
            {
                _parityTouchBugAccount.ShouldDelete = true;
            }

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
                (ReadOnlyMemory<byte> output, bool success) = precompile.Run(callData, spec);
                CallResult callResult = new(output.ToArray(), success, !success);
                return callResult;
            }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Error($"Precompiled contract ({precompile.GetType()}) execution exception", exception);
                CallResult callResult = new(Array.Empty<byte>(), false, true);
                return callResult;
            }
        }

        [SkipLocalsInit]
        private CallResult ExecuteCall(EvmState vmState, byte[]? previousCallResult, ZeroPaddedSpan previousCallOutput, scoped in UInt256 previousCallOutputDestination, IReleaseSpec spec)
        {
            bool traceOpcodes = _txTracer.IsTracingInstructions;
            ref readonly ExecutionEnvironment env = ref vmState.Env;

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

                if (vmState.ExecutionType.IsAnyCreate() && spec.ClearEmptyAccountWhenTouched)
                {
                    _state.IncrementNonce(env.ExecutingAccount);
                }
            }

            if (vmState.Env.CodeInfo.MachineCode.Length == 0)
            {
                goto Empty;
            }

            vmState.InitStacks();
            EvmStack stack = new(vmState.DataStack.AsSpan(), vmState.DataStackHead, _txTracer);
            long gasAvailable = vmState.GasAvailable;
            int programCounter = vmState.ProgramCounter;
            Span<byte> code = env.CodeInfo.MachineCode.AsSpan();

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void UpdateCurrentState(EvmState state, in int pc, in long gas, in int stackHead)
            {
                state.ProgramCounter = pc;
                state.GasAvailable = gas;
                state.DataStackHead = stackHead;
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void StartInstructionTrace(Instruction instruction, EvmStack stackValue, in ExecutionEnvironment env)
            {
                _txTracer.StartOperation(env.CallDepth + 1, gasAvailable, instruction, programCounter, env.TxExecutionContext.Header.IsPostMerge);
                if (_txTracer.IsTracingMemory)
                {
                    _txTracer.SetOperationMemory(vmState.Memory?.GetTrace() ?? new List<string>());
                }

                if (_txTracer.IsTracingStack)
                {
                    _txTracer.SetOperationStack(stackValue.GetStackTrace());
                }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Jump(in UInt256 jumpDest, in ExecutionEnvironment env, bool isSubroutine = false)
            {
                if (jumpDest > int.MaxValue)
                {
                    Metrics.EvmExceptions++;
                    if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.InvalidJumpDestination);
                    // https://github.com/NethermindEth/nethermind/issues/140
                    throw new InvalidJumpDestinationException();
                    //                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
                }

                int jumpDestInt = (int)jumpDest;

                if (!env.CodeInfo.ValidateJump(jumpDestInt, isSubroutine))
                {
                    if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.InvalidJumpDestination);
                    // https://github.com/NethermindEth/nethermind/issues/140
                    throw new InvalidJumpDestinationException();
                    //                                return CallResult.InvalidJumpDestination; // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
                }

                programCounter = jumpDestInt;
            }

            if (previousCallResult is not null)
            {
                stack.PushBytes(previousCallResult);
                if (traceOpcodes) _txTracer.ReportOperationRemainingGas(vmState.GasAvailable);
            }

            if (previousCallOutput.Length > 0)
            {
                if (!UpdateMemoryCost(vmState.Memory, ref gasAvailable, in previousCallOutputDestination, (ulong)previousCallOutput.Length))
                {
                    goto OutOfGas;
                }

                vmState.Memory.Save(in previousCallOutputDestination, previousCallOutput);
                //                if(traceOpcodes) _txTracer.ReportMemoryChange((long)localPreviousDest, previousCallOutput);
            }
            
            ref readonly TxExecutionContext txCtx = ref env.TxExecutionContext;
            while (programCounter < code.Length)
            {
                Instruction instruction = (Instruction)code[programCounter];
                // Console.WriteLine(instruction);
                if (traceOpcodes)
                {
                    StartInstructionTrace(instruction, stack, in env);
                }

                programCounter++;
                switch (instruction)
                {
                    case Instruction.STOP:
                        {
                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
                            goto Empty;
                        }
                    case Instruction.ADD:
                        if (!InstructionADD(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.MUL:
                        if (!InstructionMUL(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.SUB:
                        if (!InstructionSUB(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.DIV:
                        if (!InstructionDIV(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.SDIV:
                        if (!InstructionSDIV(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.MOD:
                        if (!InstructionMOD(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.SMOD:
                        if (!InstructionSMOD(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.ADDMOD:
                        if (!InstructionADDMOD(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.MULMOD:
                        if (!InstructionMULMOD(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.EXP:
                        if (!InstructionEXP(ref stack, ref gasAvailable, spec)) goto OutOfGas;
                        break;
                    case Instruction.SIGNEXTEND:
                        if (!InstructionSIGNEXTEND(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.LT:
                        if (!InstructionLT(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.GT:
                        if (!InstructionGT(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.SLT:
                        if (!InstructionSLT(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.SGT:
                        if (!InstructionSGT(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.EQ:
                        if (!InstructionEQ(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.ISZERO:
                        if (!InstructionISZERO(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.AND:
                        if (!InstructionAND(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.OR:
                        if (!InstructionOR(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.XOR:
                        if (!InstructionXOR(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.NOT:
                        if (!InstructionNOT(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.BYTE:
                        if (!InstructionBYTE(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.SHA3:
                        if (!InstructionSHA3(ref stack, ref gasAvailable, vmState.Memory)) goto OutOfGas;
                        break;
                    case Instruction.ADDRESS:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;
                            stack.PushBytes(env.ExecutingAccount.Bytes);
                        }
                        break;
                    case Instruction.BALANCE:
                        if (!InstructionBALANCE(ref stack, ref gasAvailable, vmState, spec)) goto OutOfGas;
                        break;
                    case Instruction.CALLER:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;
                            stack.PushBytes(env.Caller.Bytes);
                        }
                        break;
                    case Instruction.CALLVALUE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;
                            stack.PushUInt256(in env.Value);
                        }
                        break;
                    case Instruction.ORIGIN:
                        if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;
                        stack.PushBytes(txCtx.Origin.Bytes);
                        break;
                    case Instruction.CALLDATALOAD:
                        if (!InstructionCALLDATALOAD(ref stack, ref gasAvailable, in env.InputData)) goto OutOfGas;
                        break;
                    case Instruction.CALLDATASIZE:
                        if (!InstructionCALLDATASIZE(ref stack, ref gasAvailable, in env.InputData)) goto OutOfGas;
                        break;
                    case Instruction.CALLDATACOPY:
                        if (!InstructionCALLDATACOPY(ref stack, ref gasAvailable, vmState.Memory, in env.InputData)) goto OutOfGas;
                        break;
                    case Instruction.CODESIZE:
                        if (!InstructionCODESIZE(ref stack, ref gasAvailable, code.Length)) goto OutOfGas;
                        break;
                    case Instruction.CODECOPY:
                        if (!InstructionCODECOPY(ref stack, ref gasAvailable, vmState.Memory, in code)) goto OutOfGas;
                        break;
                    case Instruction.GASPRICE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt256(in txCtx.GasPrice);
                            break;
                        }
                    case Instruction.EXTCODESIZE:
                        if (!InstructionEXTCODESIZE(ref stack, ref gasAvailable, vmState, spec)) goto OutOfGas;
                        break;
                    case Instruction.EXTCODECOPY:
                        if (!InstructionEXTCODECOPY(ref stack, ref gasAvailable, vmState, spec)) goto OutOfGas;
                        break;
                    case Instruction.RETURNDATASIZE:
                        {
                            if (!spec.ReturnDataOpcodesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            InstructionRETURNDATASIZE(ref stack);
                            break;
                        }
                    case Instruction.RETURNDATACOPY:
                        {
                            if (!spec.ReturnDataOpcodesEnabled) goto InvalidInstruction;

                            InstructionReturn result = InstructionRETURNDATACOPY(ref stack, ref gasAvailable, vmState.Memory);
                            if (result == InstructionReturn.OutOfGas) goto OutOfGas;
                            if (result == InstructionReturn.AccessViolation) return CallResult.AccessViolationException;
                            break;
                        }
                    case Instruction.BLOCKHASH:
                        if (!InstructionBLOCKHASH(ref stack, ref gasAvailable, txCtx.Header)) goto OutOfGas;
                        break;
                    case Instruction.COINBASE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushBytes(txCtx.Header.GasBeneficiary.Bytes);
                            break;
                        }
                    case Instruction.PREVRANDAO:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            if (txCtx.Header.IsPostMerge)
                            {
                                stack.PushBytes(txCtx.Header.Random.Bytes);
                            }
                            else
                            {
                                stack.PushUInt256(in txCtx.Header.Difficulty);
                            }
                            break;
                        }
                    case Instruction.TIMESTAMP:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt256(txCtx.Header.Timestamp);
                            break;
                        }
                    case Instruction.NUMBER:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt256(txCtx.Header.Number);
                            break;
                        }
                    case Instruction.GASLIMIT:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt256(txCtx.Header.GasLimit);
                            break;
                        }
                    case Instruction.CHAINID:
                        {
                            if (!spec.ChainIdOpcodeEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushBytes(_chainId);
                            break;
                        }
                    case Instruction.SELFBALANCE:
                        {
                            if (!spec.SelfBalanceOpcodeEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.SelfBalance, ref gasAvailable)) goto OutOfGas;

                            UInt256 balance = _state.GetBalance(env.ExecutingAccount);
                            stack.PushUInt256(in balance);
                            break;
                        }
                    case Instruction.BASEFEE:
                        {
                            if (!spec.BaseFeeEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt256(in txCtx.Header.BaseFeePerGas);
                            break;
                        }
                    case Instruction.DATAHASH:
                        {
                            if (!spec.IsEip4844Enabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.DataHash, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 blobIndex);

                            if (txCtx.BlobVersionedHashes is not null && blobIndex < txCtx.BlobVersionedHashes.Length)
                            {
                                stack.PushBytes(txCtx.BlobVersionedHashes[blobIndex.u0]);
                            }
                            else
                            {
                                stack.PushZero();
                            }
                            break;
                        }
                    case Instruction.POP:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PopLimbo();
                            break;
                        }
                    case Instruction.MLOAD:
                        if (!InstructionMLOAD(ref stack, ref gasAvailable, vmState)) goto OutOfGas;
                        break;
                    case Instruction.MSTORE:
                        if (!InstructionMSTORE(ref stack, ref gasAvailable, vmState)) goto OutOfGas;
                        break;
                    case Instruction.MSTORE8:
                        if (!InstructionMSTORE8(ref stack, ref gasAvailable, vmState)) goto OutOfGas;
                        break;
                    case Instruction.SLOAD:
                        if (!InstructionSLOAD(ref stack, ref gasAvailable, vmState, env.ExecutingAccount, spec)) goto OutOfGas;
                        break;
                    case Instruction.SSTORE:
                        {
                            Metrics.SstoreOpcode++;
                            if (vmState.IsStatic) goto StaticCallViolation;

                            if (!InstructionSSTORE(ref stack, ref gasAvailable, env.ExecutingAccount, vmState, spec)) goto OutOfGas;
                            break;
                        }
                    case Instruction.TLOAD:
                        {
                            Metrics.TloadOpcode++;
                            if (!spec.TransientStorageEnabled) goto InvalidInstruction;

                            if (!InstructionTLOAD(ref stack, ref gasAvailable, env.ExecutingAccount, spec)) goto OutOfGas;
                            break;
                        }
                    case Instruction.TSTORE:
                        {
                            Metrics.TstoreOpcode++;
                            if (!spec.TransientStorageEnabled) goto InvalidInstruction;
                            if (vmState.IsStatic) goto StaticCallViolation;

                            if (!InstructionTSTORE(ref stack, ref gasAvailable, env.ExecutingAccount, spec)) goto OutOfGas;
                            break;
                        }
                    case Instruction.JUMP:
                        {
                            if (!UpdateGas(GasCostOf.Mid, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 jumpDest);
                            Jump(jumpDest, in env);
                            break;
                        }
                    case Instruction.JUMPI:
                        {
                            if (!UpdateGas(GasCostOf.High, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out UInt256 jumpDest);
                            if (!stack.PopBytes().IsZero())
                            {
                                Jump(jumpDest, in env);
                            }

                            break;
                        }
                    case Instruction.PC:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt32(programCounter - 1);
                            break;
                        }
                    case Instruction.MSIZE:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt256(vmState.Memory.Size);
                            break;
                        }
                    case Instruction.GAS:
                        {
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt256(gasAvailable);
                            break;
                        }
                    case Instruction.JUMPDEST:
                        {
                            if (!UpdateGas(GasCostOf.JumpDest, ref gasAvailable)) goto OutOfGas;

                            break;
                        }
                    case Instruction.PUSH0:
                        if (!spec.IncludePush0Instruction) goto InvalidInstruction;
                        if (!InstructionPUSH0(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.PUSH1:
                        if (!InstructionPUSH1(ref stack, ref gasAvailable, ref programCounter, in code)) goto OutOfGas;
                        break;
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
                        if (!InstructionPUSH(instruction, ref stack, ref gasAvailable, ref programCounter, in code)) goto OutOfGas;
                        break;
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
                        if (!InstructionDUP(instruction, ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
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
                        if (!InstructionSWAP(instruction, ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.LOG0:
                    case Instruction.LOG1:
                    case Instruction.LOG2:
                    case Instruction.LOG3:
                    case Instruction.LOG4:
                        if (vmState.IsStatic) goto StaticCallViolation;
                        if (!InstructionLOG(ref stack, ref gasAvailable, vmState, instruction, env.ExecutingAccount)) goto OutOfGas;
                        break;
                    case Instruction.CREATE:
                    case Instruction.CREATE2:
                        {
                            if (!spec.Create2OpcodeEnabled && instruction == Instruction.CREATE2) goto InvalidInstruction;
                            if (vmState.IsStatic) goto StaticCallViolation;

                            (InstructionReturn result, EvmState? callState) = InstructionCREATE(instruction, ref stack, ref gasAvailable, in env, vmState, spec);
                            if (result == InstructionReturn.OutOfGas) goto OutOfGas;
                            if (result == InstructionReturn.Success)
                            {
                                Debug.Assert(callState is not null);
                                UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                                return new CallResult(callState);
                            }
                            Debug.Assert(result == InstructionReturn.Continue);
                            break;
                        }
                    case Instruction.RETURN:
                        {
                            (InstructionReturn result, byte[]? returnData) = InstructionRETURN(ref stack, ref gasAvailable, vmState);
                            if (result == InstructionReturn.OutOfGas) goto OutOfGas;
                            
                            Debug.Assert(result == InstructionReturn.Success);
                            Debug.Assert(returnData is not null);
                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            return new CallResult(returnData, null);
                        }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    case Instruction.STATICCALL:
                        {
                            (InstructionReturn result, EvmState? callState) = InstructionCALL(instruction, ref stack, ref gasAvailable, in env, vmState, txCtx.Header.IsPostMerge, spec);
                            if (result == InstructionReturn.OutOfGas) goto OutOfGas;
                            if (result == InstructionReturn.InvalidInstruction) goto InvalidInstruction;
                            if (result == InstructionReturn.StaticCallViolation) goto StaticCallViolation;
                            if (result == InstructionReturn.Success)
                            {
                                Debug.Assert(callState is not null);
                                UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                                return new CallResult(callState);
                            }
                            Debug.Assert(result == InstructionReturn.Continue);
                            break;
                        }
                    case Instruction.REVERT:
                        {
                            if (!spec.RevertOpcodeEnabled) goto InvalidInstruction;

                            (InstructionReturn result, byte[]? message) = InstructionREVERT(ref stack, ref gasAvailable, vmState);
                            if (result == InstructionReturn.OutOfGas) goto OutOfGas;

                            Debug.Assert(result == InstructionReturn.Success);
                            Debug.Assert(message is not null);
                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            return new CallResult(message, null, true);
                        }
                    case Instruction.INVALID:
                        {
                            if (!UpdateGas(GasCostOf.High, ref gasAvailable)) goto OutOfGas;

                            goto InvalidInstruction;
                        }
                    case Instruction.SELFDESTRUCT:
                        {
                            if (vmState.IsStatic) goto StaticCallViolation;
                            if (spec.UseShanghaiDDosProtection && !UpdateGas(GasCostOf.SelfDestructEip150, ref gasAvailable)) goto OutOfGas;

                            if (!InstructionSELFDESTRUCT(ref stack, ref gasAvailable, vmState, env.ExecutingAccount, spec)) goto OutOfGas;

                            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
                            goto Empty;
                        }
                    case Instruction.SHL:
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;
                        if (!InstructionSHL(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.SHR:
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;
                        if (!InstructionSHR(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.SAR:
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;
                        if (!InstructionSAR(ref stack, ref gasAvailable)) goto OutOfGas;
                        break;
                    case Instruction.EXTCODEHASH:
                        if (!spec.ExtCodeHashOpcodeEnabled) goto InvalidInstruction;
                        if (!InstructionEXTCODEHASH(ref stack, ref gasAvailable, vmState, spec)) goto OutOfGas;
                        break;
                    case Instruction.BEGINSUB:
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Base, ref gasAvailable)) goto OutOfGas;

                            goto InvalidSubroutineEntry;
                        }
                    case Instruction.RETURNSUB:
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.Low, ref gasAvailable)) goto OutOfGas;

                            if (vmState.ReturnStackHead == 0)
                            {
                                goto InvalidSubroutineReturn;
                            }

                            programCounter = vmState.ReturnStack[--vmState.ReturnStackHead];
                            break;
                        }
                    case Instruction.JUMPSUB:
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;
                            if (!UpdateGas(GasCostOf.High, ref gasAvailable)) goto OutOfGas;

                            if (vmState.ReturnStackHead == EvmStack.ReturnStackSize)
                            {
                                goto StackOverflowException;
                            }

                            vmState.ReturnStack[vmState.ReturnStackHead++] = programCounter;

                            stack.PopUInt256(out UInt256 jumpDest);
                            Jump(jumpDest, in env, true);
                            programCounter++;

                            break;
                        }
                    default:
                        {
                            goto InvalidInstruction;
                        }
                }

                if (traceOpcodes) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
            }

            UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
// Fall through to Empty: label

// Common exit errors, goto labels to reduce in loop code duplication and to keep loop body smaller
Empty:
            return CallResult.Empty;
OutOfGas:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.OutOfGas);
            return CallResult.OutOfGasException;
InvalidInstruction:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.BadInstruction);
            return CallResult.InvalidInstructionException;
StaticCallViolation:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.StaticCallViolation);
            return CallResult.StaticCallViolationException;
InvalidSubroutineEntry:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.InvalidSubroutineEntry);
            return CallResult.InvalidSubroutineEntry;
InvalidSubroutineReturn:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.InvalidSubroutineReturn);
            return CallResult.InvalidSubroutineReturn;
StackOverflowException:
            if (traceOpcodes) EndInstructionTraceError(gasAvailable, EvmExceptionType.StackOverflow);
            return CallResult.StackOverflowException;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EndInstructionTrace(long gasAvailable, ulong memorySize)
        {
            if (_txTracer.IsTracingMemory)
            {
                _txTracer.SetOperationMemorySize(memorySize);
            }

            _txTracer.ReportOperationRemainingGas(gasAvailable);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void EndInstructionTraceError(long gasAvailable, EvmExceptionType evmExceptionType)
        {
            _txTracer.ReportOperationError(evmExceptionType);
            _txTracer.ReportOperationRemainingGas(gasAvailable);
        }

        private static ExecutionType GetCallExecutionType(Instruction instruction, bool isPostMerge = false)
        {
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
                throw new NotSupportedException($"Execution type is undefined for {instruction.GetName(isPostMerge)}");
            }

            return executionType;
        }

        internal readonly ref struct CallResult
        {
            public static CallResult InvalidSubroutineEntry => new(EvmExceptionType.InvalidSubroutineEntry);
            public static CallResult InvalidSubroutineReturn => new(EvmExceptionType.InvalidSubroutineReturn);
            public static CallResult OutOfGasException => new(EvmExceptionType.OutOfGas);
            public static CallResult AccessViolationException => new(EvmExceptionType.AccessViolation);
            public static CallResult InvalidJumpDestination => new(EvmExceptionType.InvalidJumpDestination);
            public static CallResult InvalidInstructionException
            {
                get
                {
                    return new(EvmExceptionType.BadInstruction);
                }
            }

            public static CallResult StaticCallViolationException => new(EvmExceptionType.StaticCallViolation);
            public static CallResult StackOverflowException => new(EvmExceptionType.StackOverflow); // TODO: use these to avoid CALL POP attacks
            public static CallResult StackUnderflowException => new(EvmExceptionType.StackUnderflow); // TODO: use these to avoid CALL POP attacks

            public static CallResult InvalidCodeException => new(EvmExceptionType.InvalidCode);
            public static CallResult Empty => new(Array.Empty<byte>(), null);

            public CallResult(EvmState stateToExecute)
            {
                StateToExecute = stateToExecute;
                Output = Array.Empty<byte>();
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

            public EvmState? StateToExecute { get; }
            public byte[] Output { get; }
            public EvmExceptionType ExceptionType { get; }
            public bool ShouldRevert { get; }
            public bool? PrecompileSuccess { get; } // TODO: check this behaviour as it seems it is required and previously that was not the case
            public bool IsReturn => StateToExecute is null;
            public bool IsException => ExceptionType != EvmExceptionType.None;
        }
    }
}
