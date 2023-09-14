
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Precompiles.Bls;
using Nethermind.Evm.Precompiles.Snarks;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;

#if DEBUG
using Nethermind.Evm.Tracing.Debugger;
#endif

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.Evm;

using System.Linq;
using Int256;

public class VirtualMachine : IVirtualMachine
{
    public const int MaxCallDepth = 1024;

    private readonly IVirtualMachine _evm;

    public VirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider,
        ILogManager? logManager)
    {
        ILogger logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        if (!logger.IsTrace)
        {
            _evm = new VirtualMachine<NotTracing>(blockhashProvider, specProvider, logger);
        }
        else
        {
            _evm = new VirtualMachine<IsTracing>(blockhashProvider, specProvider, logger);
        }
    }

    public CodeInfo GetCachedCodeInfo(IWorldState worldState, Address codeSource, IReleaseSpec spec)
        => _evm.GetCachedCodeInfo(worldState, codeSource, spec);

    public TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
        where TTracingActions : struct, IIsTracing
        => _evm.Run<TTracingActions>(state, worldState, txTracer);

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

    public interface IIsTracing { }
    public readonly struct NotTracing : IIsTracing { }
    public readonly struct IsTracing : IIsTracing { }
}

internal sealed class VirtualMachine<TLogger> : IVirtualMachine
    where TLogger : struct, IIsTracing
{
    private UInt256 P255Int = (UInt256)System.Numerics.BigInteger.Pow(2, 255);
    private UInt256 P255 => P255Int;
    private UInt256 BigInt256 = 256;
    public UInt256 BigInt32 = 32;

    internal byte[] BytesZero = { 0 };

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

    private readonly byte[] _chainId;

    private readonly IBlockhashProvider _blockhashProvider;
    private readonly ISpecProvider _specProvider;
    private static readonly LruCache<ValueKeccak, CodeInfo> _codeCache = new(MemoryAllowance.CodeCacheSize, MemoryAllowance.CodeCacheSize, "VM bytecodes");
    private readonly ILogger _logger;
    private IWorldState _worldState;
    private IWorldState _state;
    private readonly Stack<EvmState> _stateStack = new();
    private (Address Address, bool ShouldDelete) _parityTouchBugAccount = (Address.FromNumber(3), false);
    private Dictionary<Address, CodeInfo>? _precompiles;
    private byte[] _returnDataBuffer = Array.Empty<byte>();
    private ITxTracer _txTracer = NullTxTracer.Instance;

    public VirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider,
        ILogger? logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blockhashProvider = blockhashProvider ?? throw new ArgumentNullException(nameof(blockhashProvider));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _chainId = ((UInt256)specProvider.ChainId).ToBigEndian();
        InitializePrecompiledContracts();
    }

    public TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
        where TTracingActions : struct, IIsTracing
    {
        _txTracer = txTracer;
        _state = worldState;
        _worldState = worldState;

        IReleaseSpec spec = _specProvider.GetSpec(state.Env.TxExecutionContext.BlockExecutionContext.Header.Number, state.Env.TxExecutionContext.BlockExecutionContext.Header.Timestamp);
        EvmState currentState = state;
        byte[] previousCallResult = null;
        ZeroPaddedSpan previousCallOutput = ZeroPaddedSpan.Empty;
        UInt256 previousCallOutputDestination = UInt256.Zero;
        bool isTracing = _txTracer.IsTracing;

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
                    if (typeof(TTracingActions) == typeof(IsTracing))
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
                    if (typeof(TTracingActions) == typeof(IsTracing) && !currentState.IsContinuation)
                    {
                        _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.ExecutionType.IsAnyCreate() ? currentState.Env.CodeInfo.MachineCode : currentState.Env.InputData, currentState.ExecutionType);
                        if (_txTracer.IsTracingCode) _txTracer.ReportByteCode(currentState.Env.CodeInfo.MachineCode);
                    }

                    if (!_txTracer.IsTracingInstructions)
                    {
                        callResult = ExecuteCall<NotTracing>(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                    }
                    else
                    {
                        callResult = ExecuteCall<IsTracing>(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);
                    }

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
                        if (typeof(TTracingActions) == typeof(IsTracing)) _txTracer.ReportActionError(callResult.ExceptionType);
                        _worldState.Restore(currentState.Snapshot);

                        RevertParityTouchBugAccount(spec);

                        if (currentState.IsTopLevel)
                        {
                            return new TransactionSubstate(callResult.ExceptionType, isTracing);
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
                    if (typeof(TTracingActions) == typeof(IsTracing))
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
                        isTracerConnected: isTracing);
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
                            _state.InsertCode(callCodeOwner, callResult.Output, spec);
                            currentState.GasAvailable -= codeDepositGasCost;

                            if (typeof(TTracingActions) == typeof(IsTracing))
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

                            if (typeof(TTracingActions) == typeof(IsTracing))
                            {
                                _txTracer.ReportActionError(invalidCode ? EvmExceptionType.InvalidCode : EvmExceptionType.OutOfGas);
                            }
                        }
                        else if (typeof(TTracingActions) == typeof(IsTracing))
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

                        if (typeof(TTracingActions) == typeof(IsTracing))
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


                    if (typeof(TTracingActions) == typeof(IsTracing))
                    {
                        _txTracer.ReportActionError(EvmExceptionType.Revert, previousState.GasAvailable);
                    }
                }
            }
            catch (Exception ex) when (ex is EvmException or OverflowException)
            {
                if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"exception ({ex.GetType().Name}) in {currentState.ExecutionType} at depth {currentState.Env.CallDepth} - restoring snapshot");

                _worldState.Restore(currentState.Snapshot);

                RevertParityTouchBugAccount(spec);

                if (txTracer.IsTracingInstructions)
                {
                    txTracer.ReportOperationError(ex is EvmException evmException ? evmException.ExceptionType : EvmExceptionType.Other);
                    txTracer.ReportOperationRemainingGas(0);
                }

                if (typeof(TTracingActions) == typeof(IsTracing))
                {
                    EvmException evmException = ex as EvmException;
                    _txTracer.ReportActionError(evmException?.ExceptionType ?? EvmExceptionType.Other);
                }

                if (currentState.IsTopLevel)
                {
                    return new TransactionSubstate(ex is OverflowException ? EvmExceptionType.Other : (ex as EvmException).ExceptionType, isTracing);
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
        if (codeSource.IsPrecompile(vmSpec))
        {
            if (_precompiles is null)
            {
                throw new InvalidOperationException("EVM precompile have not been initialized properly.");
            }

            return _precompiles[codeSource];
        }

        Keccak codeHash = worldState.GetCodeHash(codeSource);
        CodeInfo cachedCodeInfo = _codeCache.Get(codeHash);
        if (cachedCodeInfo is null)
        {
            byte[] code = worldState.GetCode(codeHash);

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
            worldState.TouchCode(codeHash);
        }

        return cachedCodeInfo;
    }

    private void InitializePrecompiledContracts()
    {
        _precompiles = new Dictionary<Address, CodeInfo>
        {
            [EcRecoverPrecompile.Address] = new(EcRecoverPrecompile.Instance),
            [Sha256Precompile.Address] = new(Sha256Precompile.Instance),
            [Ripemd160Precompile.Address] = new(Ripemd160Precompile.Instance),
            [IdentityPrecompile.Address] = new(IdentityPrecompile.Instance),

            [Bn254AddPrecompile.Address] = new(Bn254AddPrecompile.Instance),
            [Bn254MulPrecompile.Address] = new(Bn254MulPrecompile.Instance),
            [Bn254PairingPrecompile.Address] = new(Bn254PairingPrecompile.Instance),
            [ModExpPrecompile.Address] = new(ModExpPrecompile.Instance),

            [Blake2FPrecompile.Address] = new(Blake2FPrecompile.Instance),

            [G1AddPrecompile.Address] = new(G1AddPrecompile.Instance),
            [G1MulPrecompile.Address] = new(G1MulPrecompile.Instance),
            [G1MultiExpPrecompile.Address] = new(G1MultiExpPrecompile.Instance),
            [G2AddPrecompile.Address] = new(G2AddPrecompile.Instance),
            [G2MulPrecompile.Address] = new(G2MulPrecompile.Instance),
            [G2MultiExpPrecompile.Address] = new(G2MultiExpPrecompile.Instance),
            [PairingPrecompile.Address] = new(PairingPrecompile.Instance),
            [MapToG1Precompile.Address] = new(MapToG1Precompile.Instance),
            [MapToG2Precompile.Address] = new(MapToG2Precompile.Instance),

            [PointEvaluationPrecompile.Address] = new(PointEvaluationPrecompile.Instance),
        };
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
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec)
    {
        // Console.WriteLine($"Accessing {storageCell} {storageAccessType}");

        bool result = true;
        if (spec.UseHotAndColdStorage)
        {
            if (_txTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
            {
                vmState.WarmUp(in storageCell);
            }

            if (vmState.IsCold(in storageCell))
            {
                result = UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
                vmState.WarmUp(in storageCell);
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
        long blobGasCost = precompile.DataGasCost(callData, spec);

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

        if (!UpdateGas(checked(baseGasCost + blobGasCost), ref gasAvailable))
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
        catch (DllNotFoundException exception)
        {
            if (_logger.IsError) _logger.Error($"Failed to load one of the dependencies of {precompile.GetType()} precompile", exception);
            throw;
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"Precompiled contract ({precompile.GetType()}) execution exception", exception);
            CallResult callResult = new(Array.Empty<byte>(), false, true);
            return callResult;
        }
    }

    /// <remarks>
    /// Struct generic parameter is used to burn out all the if statements and inner code
    /// by typeof(TTracingInstructions) == typeof(NotTracing) checks that are evaluated to constant
    /// values at compile time.
    /// </remarks>
    [SkipLocalsInit]
    private CallResult ExecuteCall<TTracingInstructions>(EvmState vmState, byte[]? previousCallResult, ZeroPaddedSpan previousCallOutput, scoped in UInt256 previousCallOutputDestination, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
    {
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

        if (env.CodeInfo.MachineCode.Length == 0)
        {
            if (!vmState.IsTopLevel)
            {
                Metrics.EmptyCalls++;
            }
            goto Empty;
        }

        vmState.InitStacks();
        EvmStack<TTracingInstructions> stack = new(vmState.DataStack.AsSpan(), vmState.DataStackHead, _txTracer);
        long gasAvailable = vmState.GasAvailable;

        if (previousCallResult is not null)
        {
            stack.PushBytes(previousCallResult);
            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportOperationRemainingGas(vmState.GasAvailable);
        }

        if (previousCallOutput.Length > 0)
        {
            UInt256 localPreviousDest = previousCallOutputDestination;
            if (!UpdateMemoryCost(vmState, ref gasAvailable, in localPreviousDest, (ulong)previousCallOutput.Length))
            {
                goto OutOfGas;
            }

            vmState.Memory.Save(in localPreviousDest, previousCallOutput);
        }

        // Struct generic parameter is used to burn out all the if statements
        // and inner code by typeof(TTracing) == typeof(NotTracing)
        // checks that are evaluated to constant values at compile time.
        // This only works for structs, not for classes or interface types
        // which use shared generics.
        if (!_txTracer.IsTracingRefunds)
        {
            return _txTracer.IsTracingOpLevelStorage ?
                ExecuteCode<TTracingInstructions, NotTracing, IsTracing>(vmState, ref stack, gasAvailable, spec) :
                ExecuteCode<TTracingInstructions, NotTracing, NotTracing>(vmState, ref stack, gasAvailable, spec);
        }
        else
        {
            return _txTracer.IsTracingOpLevelStorage ?
                ExecuteCode<TTracingInstructions, IsTracing, IsTracing>(vmState, ref stack, gasAvailable, spec) :
                ExecuteCode<TTracingInstructions, IsTracing, NotTracing>(vmState, ref stack, gasAvailable, spec);
        }
Empty:
        return CallResult.Empty;
OutOfGas:
        return CallResult.OutOfGasException;
    }

    [SkipLocalsInit]
    private CallResult ExecuteCode<TTracingInstructions, TTracingRefunds, TTracingStorage>(EvmState vmState, scoped ref EvmStack<TTracingInstructions> stack, long gasAvailable, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        int programCounter = vmState.ProgramCounter;
        ref readonly ExecutionEnvironment env = ref vmState.Env;
        ref readonly TxExecutionContext txCtx = ref env.TxExecutionContext;
        ref readonly BlockExecutionContext blkCtx = ref txCtx.BlockExecutionContext;
        Span<byte> code = env.CodeInfo.MachineCode.AsSpan();
        EvmExceptionType exceptionType = EvmExceptionType.None;
        bool isRevert = false;
#if DEBUG
        DebugTracer? debugger = _txTracer.GetTracer<DebugTracer>();
#endif
        Span<byte> bytes;
        SkipInit(out UInt256 a);
        SkipInit(out UInt256 b);
        SkipInit(out UInt256 c);
        SkipInit(out UInt256 result);
        SkipInit(out StorageCell storageCell);
        object returnData;
        ZeroPaddedSpan slice;
        uint codeLength = (uint)code.Length;
        while ((uint)programCounter < codeLength)
        {
#if DEBUG
            debugger?.TryWait(ref vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
            Instruction instruction = (Instruction)code[programCounter];

            // Evaluated to constant at compile time and code elided if not tracing
            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartInstructionTrace(instruction, vmState, gasAvailable, programCounter, in stack);

            programCounter++;
            switch (instruction)
            {
                case Instruction.STOP:
                    {
                        goto EmptyReturn;
                    }
                case Instruction.ADD:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out b);
                        stack.PopUInt256(out a);
                        UInt256.Add(in a, in b, out result);
                        stack.PushUInt256(result);

                        break;
                    }
                case Instruction.MUL:
                    {
                        gasAvailable -= GasCostOf.Low;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        UInt256.Multiply(in a, in b, out result);
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.SUB:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        UInt256.Subtract(in a, in b, out result);

                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.DIV:
                    {
                        gasAvailable -= GasCostOf.Low;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        if (b.IsZero)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            UInt256.Divide(in a, in b, out result);
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.SDIV:
                    {
                        gasAvailable -= GasCostOf.Low;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        if (b.IsZero)
                        {
                            stack.PushZero();
                        }
                        else if (As<UInt256, Int256>(ref b) == Int256.MinusOne && a == P255)
                        {
                            result = P255;
                            stack.PushUInt256(in result);
                        }
                        else
                        {
                            Int256.Divide(in As<UInt256, Int256>(ref a), in As<UInt256, Int256>(ref b), out As<UInt256, Int256>(ref result));
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.MOD:
                    {
                        gasAvailable -= GasCostOf.Low;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        UInt256.Mod(in a, in b, out result);
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.SMOD:
                    {
                        gasAvailable -= GasCostOf.Low;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        if (b.IsZeroOrOne)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            As<UInt256, Int256>(ref a)
                                .Mod(in As<UInt256, Int256>(ref b), out As<UInt256, Int256>(ref result));
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.ADDMOD:
                    {
                        gasAvailable -= GasCostOf.Mid;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        stack.PopUInt256(out c);

                        if (c.IsZero)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            UInt256.AddMod(a, b, c, out result);
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.MULMOD:
                    {
                        gasAvailable -= GasCostOf.Mid;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        stack.PopUInt256(out c);

                        if (c.IsZero)
                        {
                            stack.PushZero();
                        }
                        else
                        {
                            UInt256.MultiplyMod(in a, in b, in c, out result);
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.EXP:
                    {
                        gasAvailable -= GasCostOf.Exp;

                        Metrics.ModExpOpcode++;

                        stack.PopUInt256(out a);
                        bytes = stack.PopWord256();

                        int leadingZeros = bytes.LeadingZerosCount();
                        if (leadingZeros != 32)
                        {
                            int expSize = 32 - leadingZeros;
                            gasAvailable -= spec.GetExpByteCost() * expSize;
                        }
                        else
                        {
                            stack.PushOne();
                            break;
                        }

                        if (a.IsZero)
                        {
                            stack.PushZero();
                        }
                        else if (a.IsOne)
                        {
                            stack.PushOne();
                        }
                        else
                        {
                            UInt256.Exp(a, new UInt256(bytes, true), out result);
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.SIGNEXTEND:
                    {
                        gasAvailable -= GasCostOf.Low;

                        stack.PopUInt256(out a);
                        if (a >= BigInt32)
                        {
                            stack.EnsureDepth(1);
                            break;
                        }

                        int position = 31 - (int)a;

                        bytes = stack.PeekWord256();
                        sbyte sign = (sbyte)bytes[position];

                        if (sign >= 0)
                        {
                            BytesZero32.AsSpan(0, position).CopyTo(bytes[..position]);
                        }
                        else
                        {
                            BytesMax32.AsSpan(0, position).CopyTo(bytes[..position]);
                        }

                        // Didn't remove from stack so don't need to push back
                        break;
                    }
                case Instruction.LT:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
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
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
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
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);

                        if (As<UInt256, Int256>(ref a).CompareTo(As<UInt256, Int256>(ref b)) < 0)
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
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        if (As<UInt256, Int256>(ref a).CompareTo(As<UInt256, Int256>(ref b)) > 0)
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
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        if (a.Equals(b))
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
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        if (a.IsZero)
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
                        gasAvailable -= GasCostOf.VeryLow;

                        Vector256<byte> aVec = ReadUnaligned<Vector256<byte>>(ref stack.PopBytesByRef());
                        Vector256<byte> bVec = ReadUnaligned<Vector256<byte>>(ref stack.PopBytesByRef());

                        WriteUnaligned(ref stack.PushBytesRef(), Vector256.BitwiseAnd(aVec, bVec));
                        break;
                    }
                case Instruction.OR:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        Vector256<byte> aVec = ReadUnaligned<Vector256<byte>>(ref stack.PopBytesByRef());
                        Vector256<byte> bVec = ReadUnaligned<Vector256<byte>>(ref stack.PopBytesByRef());

                        WriteUnaligned(ref stack.PushBytesRef(), Vector256.BitwiseOr(aVec, bVec));
                        break;
                    }
                case Instruction.XOR:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        Vector256<byte> aVec = ReadUnaligned<Vector256<byte>>(ref stack.PopBytesByRef());
                        Vector256<byte> bVec = ReadUnaligned<Vector256<byte>>(ref stack.PopBytesByRef());

                        WriteUnaligned(ref stack.PushBytesRef(), Vector256.Xor(aVec, bVec));
                        break;
                    }
                case Instruction.NOT:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        Vector256<byte> negVec = Vector256.OnesComplement(ReadUnaligned<Vector256<byte>>(ref stack.PopBytesByRef()));

                        WriteUnaligned(ref stack.PushBytesRef(), negVec);
                        break;
                    }
                case Instruction.BYTE:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        bytes = stack.PopWord256();

                        if (a >= BigInt32)
                        {
                            stack.PushZero();
                            break;
                        }

                        int adjustedPosition = bytes.Length - 32 + (int)a;
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
                case Instruction.KECCAK256:
                    {
                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        gasAvailable -= GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in b);

                        if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, b)) goto OutOfGas;

                        bytes = vmState.Memory.LoadSpan(in a, b);
                        stack.PushBytes(ValueKeccak.Compute(bytes).BytesAsSpan);
                        break;
                    }
                case Instruction.ADDRESS:
                    {
                        gasAvailable -= GasCostOf.Base;

                        stack.PushBytes(env.ExecutingAccount.Bytes);
                        break;
                    }
                case Instruction.BALANCE:
                    {
                        gasAvailable -= spec.GetBalanceCost();

                        Address address = stack.PopAddress();
                        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) goto OutOfGas;

                        result = _state.GetBalance(address);
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.CALLER:
                    {
                        gasAvailable -= GasCostOf.Base;

                        stack.PushBytes(env.Caller.Bytes);
                        break;
                    }
                case Instruction.CALLVALUE:
                    {
                        gasAvailable -= GasCostOf.Base;

                        result = env.Value;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.ORIGIN:
                    {
                        gasAvailable -= GasCostOf.Base;

                        stack.PushBytes(txCtx.Origin.Bytes);
                        break;
                    }
                case Instruction.CALLDATALOAD:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out result);
                        stack.PushBytes(env.InputData.SliceWithZeroPadding(result, 32));
                        break;
                    }
                case Instruction.CALLDATASIZE:
                    {
                        gasAvailable -= GasCostOf.Base;

                        result = (UInt256)env.InputData.Length;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.CALLDATACOPY:
                    {
                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        stack.PopUInt256(out result);
                        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

                        if (!result.IsZero)
                        {
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, in result)) goto OutOfGas;

                            slice = env.InputData.SliceWithZeroPadding(b, (int)result);
                            vmState.Memory.Save(in a, in slice);
                            if (typeof(TTracingInstructions) == typeof(IsTracing))
                            {
                                _txTracer.ReportMemoryChange((long)a, slice);
                            }
                        }

                        break;
                    }
                case Instruction.CODESIZE:
                    {
                        gasAvailable -= GasCostOf.Base;

                        result = (UInt256)code.Length;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.CODECOPY:
                    {
                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        stack.PopUInt256(out result);
                        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

                        if (!result.IsZero)
                        {
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) goto OutOfGas;

                            slice = code.SliceWithZeroPadding(in b, (int)result);
                            vmState.Memory.Save(in a, in slice);
                            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange((long)a, in slice);
                        }

                        break;
                    }
                case Instruction.GASPRICE:
                    {
                        gasAvailable -= GasCostOf.Base;

                        result = txCtx.GasPrice;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.EXTCODESIZE:
                    {
                        gasAvailable -= spec.GetExtCodeCost();

                        Address address = stack.PopAddress();
                        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) goto OutOfGas;

                        if (typeof(TTracingInstructions) != typeof(IsTracing) && programCounter < code.Length)
                        {
                            bool optimizeAccess = false;
                            Instruction nextInstruction = (Instruction)code[programCounter];
                            // code.length is zero
                            if (nextInstruction == Instruction.ISZERO)
                            {
                                optimizeAccess = true;
                            }
                            // code.length > 0 || code.length == 0
                            else if ((nextInstruction == Instruction.GT || nextInstruction == Instruction.EQ) &&
                                    stack.PeekUInt256IsZero())
                            {
                                optimizeAccess = true;
                                stack.PopLimbo();
                            }

                            if (optimizeAccess)
                            {
                                // EXTCODESIZE ISZERO/GT/EQ peephole optimization.
                                // In solidity 0.8.1+: `return account.code.length > 0;`
                                // is is a common pattern to check if address is a contract
                                // however we can just check the address's loaded CodeHash
                                // to reduce storage access from trying to load the code

                                programCounter++;
                                // Add gas cost for ISZERO, GT, or EQ
                                gasAvailable -= GasCostOf.VeryLow;

                                // IsContract
                                bool isCodeLengthNotZero = _state.IsContract(address);
                                if (nextInstruction == Instruction.GT)
                                {
                                    // Invert, to IsNotContract
                                    isCodeLengthNotZero = !isCodeLengthNotZero;
                                }

                                if (!isCodeLengthNotZero)
                                {
                                    stack.PushOne();
                                }
                                else
                                {
                                    stack.PushZero();
                                }
                                break;
                            }
                        }

                        InstructionExtCodeSize(address, ref stack, spec);
                        break;
                    }
                case Instruction.EXTCODECOPY:
                    {
                        Address address = stack.PopAddress();
                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        stack.PopUInt256(out result);

                        gasAvailable -= spec.GetExtCodeCost() + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

                        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) goto OutOfGas;

                        if (!result.IsZero)
                        {
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) goto OutOfGas;

                            byte[] externalCode = GetCachedCodeInfo(_worldState, address, spec).MachineCode;
                            slice = externalCode.SliceWithZeroPadding(b, (int)result);
                            vmState.Memory.Save(in a, in slice);
                            if (typeof(TTracingInstructions) == typeof(IsTracing))
                            {
                                _txTracer.ReportMemoryChange((long)a, in slice);
                            }
                        }

                        break;
                    }
                case Instruction.RETURNDATASIZE:
                    {
                        if (!spec.ReturnDataOpcodesEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.Base;

                        result = (UInt256)_returnDataBuffer.Length;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.RETURNDATACOPY:
                    {
                        if (!spec.ReturnDataOpcodesEnabled) goto InvalidInstruction;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        stack.PopUInt256(out result);
                        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result);

                        if (UInt256.AddOverflow(result, b, out c) || c > _returnDataBuffer.Length)
                        {
                            goto AccessViolation;
                        }

                        if (!result.IsZero)
                        {
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) goto OutOfGas;

                            slice = _returnDataBuffer.AsSpan().SliceWithZeroPadding(b, (int)result);
                            vmState.Memory.Save(in a, in slice);
                            if (typeof(TTracingInstructions) == typeof(IsTracing))
                            {
                                _txTracer.ReportMemoryChange((long)a, in slice);
                            }
                        }

                        break;
                    }
                case Instruction.BLOCKHASH:
                    {
                        Metrics.BlockhashOpcode++;

                        gasAvailable -= GasCostOf.BlockHash;

                        stack.PopUInt256(out a);
                        long number = a > long.MaxValue ? long.MaxValue : (long)a;
                        Keccak blockHash = _blockhashProvider.GetBlockhash(blkCtx.Header, number);
                        stack.PushBytes(blockHash != null ? blockHash.Bytes : BytesZero32);

                        if (typeof(TLogger) == typeof(IsTracing))
                        {
                            if (_txTracer.IsTracingBlockHash && blockHash is not null)
                            {
                                _txTracer.ReportBlockHash(blockHash);
                            }
                        }

                        break;
                    }
                case Instruction.COINBASE:
                    {
                        gasAvailable -= GasCostOf.Base;

                        stack.PushBytes(blkCtx.Header.GasBeneficiary.Bytes);
                        break;
                    }
                case Instruction.PREVRANDAO:
                    {
                        gasAvailable -= GasCostOf.Base;

                        if (blkCtx.Header.IsPostMerge)
                        {
                            stack.PushBytes(blkCtx.Header.Random.Bytes);
                        }
                        else
                        {
                            result = blkCtx.Header.Difficulty;
                            stack.PushUInt256(in result);
                        }
                        break;
                    }
                case Instruction.TIMESTAMP:
                    {
                        gasAvailable -= GasCostOf.Base;

                        result = blkCtx.Header.Timestamp;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.NUMBER:
                    {
                        gasAvailable -= GasCostOf.Base;

                        result = (UInt256)blkCtx.Header.Number;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.GASLIMIT:
                    {
                        gasAvailable -= GasCostOf.Base;

                        result = (UInt256)blkCtx.Header.GasLimit;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.CHAINID:
                    {
                        if (!spec.ChainIdOpcodeEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.Base;

                        stack.PushBytes(_chainId);
                        break;
                    }
                case Instruction.SELFBALANCE:
                    {
                        if (!spec.SelfBalanceOpcodeEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.SelfBalance;

                        result = _state.GetBalance(env.ExecutingAccount);
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.BASEFEE:
                    {
                        if (!spec.BaseFeeEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.Base;

                        result = blkCtx.Header.BaseFeePerGas;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.BLOBBASEFEE:
                    {
                        if (!spec.BlobBaseFeeEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.Base;
                        result = blkCtx.BlobBaseFee ?? throw new Exception("BlobBaseFee is not set. EIP-4844 has to be enabled for this opcode");
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.BLOBHASH:
                    {
                        if (!spec.IsEip4844Enabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.BlobHash;

                        stack.PopUInt256(out result);

                        if (txCtx.BlobVersionedHashes is not null && result < txCtx.BlobVersionedHashes.Length)
                        {
                            stack.PushBytes(txCtx.BlobVersionedHashes[result.u0]);
                        }
                        else
                        {
                            stack.PushZero();
                        }
                        break;
                    }
                case Instruction.POP:
                    {
                        gasAvailable -= GasCostOf.Base;

                        stack.PopLimbo();
                        break;
                    }
                case Instruction.MLOAD:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out result);
                        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) goto OutOfGas;
                        bytes = vmState.Memory.LoadSpan(in result);
                        if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(result, bytes);

                        stack.PushBytes(bytes);
                        break;
                    }
                case Instruction.MSTORE:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out result);

                        bytes = stack.PopWord256();
                        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) goto OutOfGas;
                        vmState.Memory.SaveWord(in result, bytes);
                        if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange((long)result, bytes);

                        break;
                    }
                case Instruction.MSTORE8:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out result);
                        byte data = stack.PopByte();
                        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, UInt256.One)) goto OutOfGas;
                        vmState.Memory.SaveByte(in result, data);
                        if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange((long)result, data);

                        break;
                    }
                case Instruction.SLOAD:
                    {
                        Metrics.SloadOpcode++;
                        gasAvailable -= spec.GetSLoadCost();

                        stack.PopUInt256(out result);
                        storageCell = new(env.ExecutingAccount, result);
                        if (!ChargeStorageAccessGas(
                            ref gasAvailable,
                            vmState,
                            in storageCell,
                            StorageAccessType.SLOAD,
                            spec)) goto OutOfGas;

                        byte[] value = _state.Get(in storageCell);
                        stack.PushBytes(value);

                        if (typeof(TTracingStorage) == typeof(IsTracing))
                        {
                            _txTracer.LoadOperationStorage(storageCell.Address, result, value);
                        }

                        break;
                    }
                case Instruction.SSTORE:
                    {
                        Metrics.SstoreOpcode++;

                        if (vmState.IsStatic) goto StaticCallViolation;

                        if (!InstructionSStore<TTracingInstructions, TTracingRefunds, TTracingStorage>(vmState, ref stack, ref gasAvailable, spec))
                            goto OutOfGas;

                        break;
                    }
                case Instruction.JUMP:
                    {
                        gasAvailable -= GasCostOf.Mid;

                        stack.PopUInt256(out result);
                        if (!Jump(result, ref programCounter, in env)) goto InvalidJumpDestination;
                        break;
                    }
                case Instruction.JUMPI:
                    {
                        gasAvailable -= GasCostOf.High;

                        stack.PopUInt256(out result);
                        bytes = stack.PopWord256();
                        if (!bytes.SequenceEqual(BytesZero32))
                        {
                            if (!Jump(result, ref programCounter, in env)) goto InvalidJumpDestination;
                        }

                        break;
                    }
                case Instruction.PC:
                    {
                        gasAvailable -= GasCostOf.Base;

                        stack.PushUInt32(programCounter - 1);
                        break;
                    }
                case Instruction.MSIZE:
                    {
                        gasAvailable -= GasCostOf.Base;

                        result = vmState.Memory.Size;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.GAS:
                    {
                        gasAvailable -= GasCostOf.Base;
                        // Ensure gas is positive before pushing to stack
                        if (gasAvailable < 0) goto OutOfGas;

                        result = (UInt256)gasAvailable;
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.JUMPDEST:
                    {
                        gasAvailable -= GasCostOf.JumpDest;

                        break;
                    }
                case Instruction.PUSH0:
                    {
                        if (!spec.IncludePush0Instruction) goto InvalidInstruction;
                        gasAvailable -= GasCostOf.Base;

                        stack.PushZero();
                        break;
                    }
                case Instruction.PUSH1:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

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
                        gasAvailable -= GasCostOf.VeryLow;

                        int length = instruction - Instruction.PUSH1 + 1;
                        int usedFromCode = Math.Min(code.Length - programCounter, length);
                        stack.PushLeftPaddedBytes(code.Slice(programCounter, usedFromCode), length);

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
                        gasAvailable -= GasCostOf.VeryLow;

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
                        gasAvailable -= GasCostOf.VeryLow;

                        stack.Swap(instruction - Instruction.SWAP1 + 2);
                        break;
                    }
                case Instruction.LOG0:
                case Instruction.LOG1:
                case Instruction.LOG2:
                case Instruction.LOG3:
                case Instruction.LOG4:
                    {
                        if (vmState.IsStatic) goto StaticCallViolation;

                        if (!InstructionLog(vmState, ref stack, ref gasAvailable, instruction)) goto OutOfGas;
                        break;
                    }
                case Instruction.CREATE:
                case Instruction.CREATE2:
                    {
                        Metrics.Creates++;
                        if (!spec.Create2OpcodeEnabled && instruction == Instruction.CREATE2) goto InvalidInstruction;

                        if (vmState.IsStatic) goto StaticCallViolation;

                        (bool outOfGas, returnData) = InstructionCreate(vmState, ref stack, ref gasAvailable, spec, instruction);

                        if (outOfGas) goto OutOfGas;
                        if (returnData is null) break;

                        goto DataReturnNoTrace;
                    }
                case Instruction.RETURN:
                    {
                        if (!InstructionReturn(vmState, ref stack, ref gasAvailable, out returnData)) goto OutOfGas;

                        goto DataReturn;
                    }
                case Instruction.CALL:
                case Instruction.CALLCODE:
                case Instruction.DELEGATECALL:
                case Instruction.STATICCALL:
                    {
                        exceptionType = InstructionCall<TTracingInstructions, TTracingRefunds>(vmState, ref stack, ref gasAvailable, spec, instruction, out returnData);
                        if (exceptionType != EvmExceptionType.None)
                        {
                            goto ReturnFailure;
                        }
                        if (returnData is null)
                        {
                            break;
                        }

                        goto DataReturn;
                    }
                case Instruction.REVERT:
                    {
                        if (!spec.RevertOpcodeEnabled) goto InvalidInstruction;

                        if (!InstructionRevert(vmState, ref stack, ref gasAvailable, out returnData)) goto OutOfGas;

                        isRevert = true;
                        goto DataReturn;
                    }
                case Instruction.INVALID:
                    {
                        gasAvailable -= GasCostOf.High;

                        goto InvalidInstruction;
                    }
                case Instruction.SELFDESTRUCT:
                    {
                        if (vmState.IsStatic) goto StaticCallViolation;

                        if (spec.UseShanghaiDDosProtection)
                        {
                            gasAvailable -= GasCostOf.SelfDestructEip150;
                        }

                        if (!InstructionSelfDestruct(vmState, ref stack, ref gasAvailable, spec)) goto OutOfGas;

                        goto EmptyReturn;
                    }
                case Instruction.SHL:
                    {
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        if (a >= 256UL)
                        {
                            stack.PopLimbo();
                            stack.PushZero();
                        }
                        else
                        {
                            stack.PopUInt256(out b);
                            result = b << (int)a.u0;
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.SHR:
                    {
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        if (a >= 256)
                        {
                            stack.PopLimbo();
                            stack.PushZero();
                        }
                        else
                        {
                            stack.PopUInt256(out b);
                            result = b >> (int)a.u0;
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.SAR:
                    {
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.VeryLow;

                        stack.PopUInt256(out a);
                        stack.PopUInt256(out b);
                        if (a >= BigInt256)
                        {
                            if (As<UInt256, Int256>(ref b).Sign >= 0)
                            {
                                stack.PushZero();
                            }
                            else
                            {
                                stack.PushSignedInt256(in Int256.MinusOne);
                            }
                        }
                        else
                        {
                            As<UInt256, Int256>(ref b).RightShift((int)a, out As<UInt256, Int256>(ref result));
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.EXTCODEHASH:
                    {
                        if (!spec.ExtCodeHashOpcodeEnabled) goto InvalidInstruction;

                        gasAvailable -= spec.GetExtCodeHashCost();

                        Address address = stack.PopAddress();
                        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, spec)) goto OutOfGas;

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
                case Instruction.BEGINSUB | Instruction.TLOAD:
                    {
                        if (spec.TransientStorageEnabled)
                        {
                            Metrics.TloadOpcode++;
                            gasAvailable -= GasCostOf.TLoad;

                            stack.PopUInt256(out result);
                            storageCell = new(env.ExecutingAccount, result);

                            byte[] value = _state.GetTransientState(in storageCell);
                            stack.PushBytes(value);

                            if (typeof(TTracingStorage) == typeof(IsTracing))
                            {
                                if (gasAvailable < 0) goto OutOfGas;
                                _txTracer.LoadOperationTransientStorage(storageCell.Address, result, value);
                            }

                            break;
                        }
                        else
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;

                            // why do we even need the cost of it?
                            gasAvailable -= GasCostOf.Base;

                            goto InvalidSubroutineEntry;
                        }

                    }
                case Instruction.RETURNSUB | Instruction.TSTORE:
                    {
                        if (spec.TransientStorageEnabled)
                        {
                            Metrics.TstoreOpcode++;

                            if (vmState.IsStatic) goto StaticCallViolation;

                            gasAvailable -= GasCostOf.TStore;

                            stack.PopUInt256(out result);
                            storageCell = new(env.ExecutingAccount, result);
                            bytes = stack.PopWord256();

                            _state.SetTransientState(in storageCell, !bytes.IsZero() ? bytes.ToArray() : BytesZero32);

                            if (typeof(TTracingStorage) == typeof(IsTracing))
                            {
                                if (gasAvailable < 0) goto OutOfGas;
                                byte[] currentValue = _state.GetTransientState(in storageCell);
                                _txTracer.SetOperationTransientStorage(storageCell.Address, result, bytes, currentValue);
                            }

                            break;
                        }
                        else
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;

                            gasAvailable -= GasCostOf.Low;

                            if (vmState.ReturnStackHead == 0)
                            {
                                goto InvalidSubroutineReturn;
                            }

                            programCounter = vmState.ReturnStack[--vmState.ReturnStackHead];
                            break;
                        }
                    }
                case Instruction.JUMPSUB or Instruction.MCOPY:
                    {
                        if (spec.MCopyIncluded)
                        {
                            Metrics.MCopyOpcode++;

                            stack.PopUInt256(out a);
                            stack.PopUInt256(out b);
                            stack.PopUInt256(out c);

                            gasAvailable -= GasCostOf.VeryLow + GasCostOf.VeryLow * EvmPooledMemory.Div32Ceiling(c);
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, UInt256.Max(b, a), c)) goto OutOfGas;

                            bytes = vmState.Memory.LoadSpan(in b, c);
                            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(b, bytes);

                            vmState.Memory.Save(in a, bytes);
                            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(a, bytes);

                            break;
                        }
                        else
                        {
                            if (!spec.SubroutinesEnabled) goto InvalidInstruction;

                            gasAvailable -= GasCostOf.High;

                            if (vmState.ReturnStackHead == EvmStack.ReturnStackSize) goto StackOverflow;

                            vmState.ReturnStack[vmState.ReturnStackHead++] = programCounter;

                            stack.PopUInt256(out UInt256 jumpDest);
                            if (!Jump(jumpDest, ref programCounter, in env, true)) goto InvalidJumpDestination;
                            programCounter++;

                            break;
                        }
                    }
                default:
                    {
                        goto InvalidInstruction;
                    }
            }

            if (gasAvailable < 0)
            {
                goto OutOfGas;
            }

            if (typeof(TTracingInstructions) == typeof(IsTracing))
            {
                EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
            }
        }

        goto EmptyReturnNoTrace;

// Common exit errors, goto labels to reduce in loop code duplication and to keep loop body smaller
EmptyReturn:
        if (typeof(TTracingInstructions) == typeof(IsTracing)) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
EmptyReturnNoTrace:
// Ensure gas is positive before updating state
        if (gasAvailable < 0) goto OutOfGas;
        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
#if DEBUG
        debugger?.TryWait(ref vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
        return CallResult.Empty;
DataReturn:
        if (typeof(TTracingInstructions) == typeof(IsTracing)) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
DataReturnNoTrace:
// Ensure gas is positive before updating state
        if (gasAvailable < 0) goto OutOfGas;
        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);

        if (returnData is EvmState state)
        {
            return new CallResult(state);
        }
        return new CallResult((byte[])returnData, null, shouldRevert: isRevert);

OutOfGas:
        exceptionType = EvmExceptionType.OutOfGas;
        goto ReturnFailure;
InvalidInstruction:
        exceptionType = EvmExceptionType.BadInstruction;
        goto ReturnFailure;
StaticCallViolation:
        exceptionType = EvmExceptionType.StaticCallViolation;
        goto ReturnFailure;
InvalidSubroutineEntry:
        exceptionType = EvmExceptionType.InvalidSubroutineEntry;
        goto ReturnFailure;
InvalidSubroutineReturn:
        exceptionType = EvmExceptionType.InvalidSubroutineReturn;
        goto ReturnFailure;
StackOverflow:
        exceptionType = EvmExceptionType.StackOverflow;
        goto ReturnFailure;
InvalidJumpDestination:
        exceptionType = EvmExceptionType.InvalidJumpDestination;
        goto ReturnFailure;
AccessViolation:
        exceptionType = EvmExceptionType.AccessViolation;
ReturnFailure:
        return GetFailureReturn<TTracingInstructions>(gasAvailable, exceptionType);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InstructionExtCodeSize<TTracingInstructions>(Address address, ref EvmStack<TTracingInstructions> stack, IReleaseSpec spec) where TTracingInstructions : struct, IIsTracing
    {
        byte[] accountCode = GetCachedCodeInfo(_worldState, address, spec).MachineCode;
        UInt256 result = (UInt256)accountCode.Length;
        stack.PushUInt256(in result);
    }

    [SkipLocalsInit]
    private EvmExceptionType InstructionCall<TTracingInstructions, TTracingRefunds>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, IReleaseSpec spec,
        Instruction instruction, out object returnData)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
    {
        returnData = null;
        ref readonly ExecutionEnvironment env = ref vmState.Env;

        Metrics.Calls++;

        if (instruction == Instruction.DELEGATECALL && !spec.DelegateCallEnabled ||
            instruction == Instruction.STATICCALL && !spec.StaticCallEnabled) return EvmExceptionType.BadInstruction;

        stack.PopUInt256(out UInt256 gasLimit);
        Address codeSource = stack.PopAddress();

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, codeSource, spec)) return EvmExceptionType.OutOfGas;

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

        if (vmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE) return EvmExceptionType.StaticCallViolation;

        Address caller = instruction == Instruction.DELEGATECALL ? env.Caller : env.ExecutingAccount;
        Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL
            ? codeSource
            : env.ExecutingAccount;

        if (typeof(TLogger) == typeof(IsTracing))
        {
            _logger.Trace($"caller {caller}");
            _logger.Trace($"code source {codeSource}");
            _logger.Trace($"target {target}");
            _logger.Trace($"value {callValue}");
            _logger.Trace($"transfer value {transferValue}");
        }

        long gasExtra = 0L;

        if (!transferValue.IsZero)
        {
            gasExtra += GasCostOf.CallValue;
        }

        if (!spec.ClearEmptyAccountWhenTouched && !_state.AccountExists(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }
        else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && _state.IsDeadAccount(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }

        if (!UpdateGas(spec.GetCallCost(), ref gasAvailable) ||
            !UpdateMemoryCost(vmState, ref gasAvailable, in dataOffset, dataLength) ||
            !UpdateMemoryCost(vmState, ref gasAvailable, in outputOffset, outputLength) ||
            !UpdateGas(gasExtra, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (spec.Use63Over64Rule)
        {
            gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
        }

        if (gasLimit >= long.MaxValue) return EvmExceptionType.OutOfGas;

        long gasLimitUl = (long)gasLimit;
        if (!UpdateGas(gasLimitUl, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (!transferValue.IsZero)
        {
            if (typeof(TTracingRefunds) == typeof(IsTracing)) _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
            gasLimitUl += GasCostOf.CallStipend;
        }

        if (env.CallDepth >= MaxCallDepth ||
            !transferValue.IsZero && _state.GetBalance(env.ExecutingAccount) < transferValue)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();

            if (typeof(TTracingInstructions) == typeof(IsTracing))
            {
                // very specific for Parity trace, need to find generalization - very peculiar 32 length...
                ReadOnlyMemory<byte> memoryTrace = vmState.Memory.Inspect(in dataOffset, 32);
                _txTracer.ReportMemoryChange(dataOffset, memoryTrace.Span);
            }

            if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace("FAIL - call depth");
            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportOperationRemainingGas(gasAvailable);
            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);

            UpdateGasUp(gasLimitUl, ref gasAvailable);
            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
            return EvmExceptionType.None;
        }

        ReadOnlyMemory<byte> callData = vmState.Memory.Load(in dataOffset, dataLength);

        Snapshot snapshot = _worldState.TakeSnapshot();
        _state.SubtractFromBalance(caller, transferValue, spec);

        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: caller,
            codeSource: codeSource,
            executingAccount: target,
            transferValue: transferValue,
            value: callValue,
            inputData: callData,
            codeInfo: GetCachedCodeInfo(_worldState, codeSource, spec)
        );
        if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Tx call gas {gasLimitUl}");
        if (outputLength == 0)
        {
            // TODO: when output length is 0 outputOffset can have any value really
            // and the value does not matter and it can cause trouble when beyond long range
            outputOffset = 0;
        }

        ExecutionType executionType = GetCallExecutionType(instruction, env.TxExecutionContext.BlockExecutionContext.Header.IsPostMerge);
        returnData = new EvmState(
            gasLimitUl,
            callEnv,
            executionType,
            isTopLevel: false,
            snapshot,
            (long)outputOffset,
            (long)outputLength,
            instruction == Instruction.STATICCALL || vmState.IsStatic,
            vmState,
            isContinuation: false,
            isCreateOnPreExistingAccount: false);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private static bool InstructionRevert<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable, out object returnData)
        where TTracing : struct, IIsTracing
    {
        stack.PopUInt256(out UInt256 position);
        stack.PopUInt256(out UInt256 length);

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, in length))
        {
            returnData = null;
            return false;
        }

        returnData = vmState.Memory.Load(in position, in length).ToArray();
        return true;
    }

    [SkipLocalsInit]
    private static bool InstructionReturn<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable, out object returnData)
        where TTracing : struct, IIsTracing
    {
        stack.PopUInt256(out UInt256 position);
        stack.PopUInt256(out UInt256 length);

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, in length))
        {
            returnData = null;
            return false;
        }

        returnData = vmState.Memory.Load(in position, in length).ToArray();

        return true;
    }

    [SkipLocalsInit]
    private bool InstructionSelfDestruct<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable, IReleaseSpec spec)
        where TTracing : struct, IIsTracing
    {
        Metrics.SelfDestructs++;

        Address inheritor = stack.PopAddress();
        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, inheritor, spec, false)) return false;

        Address executingAccount = vmState.Env.ExecutingAccount;
        bool createInSameTx = vmState.CreateList.Contains(executingAccount);
        if (!spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
            vmState.DestroyList.Add(executingAccount);

        UInt256 result = _state.GetBalance(executingAccount);
        if (_txTracer.IsTracingActions) _txTracer.ReportSelfDestruct(executingAccount, result, inheritor);
        if (spec.ClearEmptyAccountWhenTouched && !result.IsZero && _state.IsDeadAccount(inheritor))
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return false;
        }

        bool inheritorAccountExists = _state.AccountExists(inheritor);
        if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return false;
        }

        if (!inheritorAccountExists)
        {
            _state.CreateAccount(inheritor, result);
        }
        else if (!inheritor.Equals(executingAccount))
        {
            _state.AddToBalance(inheritor, result, spec);
        }

        if (spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
            return true; // dont burn eth when contract is not destroyed per EIP clarification

        _state.SubtractFromBalance(executingAccount, result, spec);
        return true;
    }

    [SkipLocalsInit]
    private (bool outOfGas, EvmState? callState) InstructionCreate<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable, IReleaseSpec spec, Instruction instruction)
        where TTracing : struct, IIsTracing
    {
        ref readonly ExecutionEnvironment env = ref vmState.Env;

        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
        if (!_state.AccountExists(env.ExecutingAccount))
        {
            _state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
        }

        stack.PopUInt256(out UInt256 value);
        stack.PopUInt256(out UInt256 memoryPositionOfInitCode);
        stack.PopUInt256(out UInt256 initCodeLength);
        Span<byte> salt = default;
        if (instruction == Instruction.CREATE2)
        {
            salt = stack.PopWord256();
        }

        //EIP-3860
        if (spec.IsEip3860Enabled)
        {
            if (initCodeLength > spec.MaxInitCodeSize) return (outOfGas: true, null);
        }

        long gasCost = GasCostOf.Create +
                       (spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmPooledMemory.Div32Ceiling(initCodeLength) : 0) +
                       (instruction == Instruction.CREATE2
                           ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(initCodeLength)
                           : 0);

        if (!UpdateGas(gasCost, ref gasAvailable)) return (outOfGas: true, null);

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in memoryPositionOfInitCode, initCodeLength)) return (outOfGas: true, null);

        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
        {
            // TODO: need a test for this
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (outOfGas: false, null);
        }

        Span<byte> initCode = vmState.Memory.LoadSpan(in memoryPositionOfInitCode, initCodeLength);

        UInt256 balance = _state.GetBalance(env.ExecutingAccount);
        if (value > balance)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (outOfGas: false, null);
        }

        UInt256 accountNonce = _state.GetNonce(env.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (outOfGas: false, null);
        }

        if (typeof(TTracing) == typeof(IsTracing)) EndInstructionTrace(gasAvailable, vmState.Memory?.Size ?? 0);
        // todo: === below is a new call - refactor / move

        long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!UpdateGas(callGas, ref gasAvailable)) return (outOfGas: true, null);

        Address contractAddress = instruction == Instruction.CREATE
            ? ContractAddress.From(env.ExecutingAccount, _state.GetNonce(env.ExecutingAccount))
            : ContractAddress.From(env.ExecutingAccount, salt, initCode);

        if (spec.UseHotAndColdStorage)
        {
            // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
            vmState.WarmUp(contractAddress);
        }

        _state.IncrementNonce(env.ExecutingAccount);

        Snapshot snapshot = _worldState.TakeSnapshot();

        bool accountExists = _state.AccountExists(contractAddress);
        if (accountExists && (GetCachedCodeInfo(_worldState, contractAddress, spec).MachineCode.Length != 0 ||
                              _state.GetNonce(contractAddress) != 0))
        {
            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
            if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Contract collision at {contractAddress}");
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (outOfGas: false, null);
        }

        if (accountExists)
        {
            _state.UpdateStorageRoot(contractAddress, Keccak.EmptyTreeHash);
        }
        else if (_state.IsDeadAccount(contractAddress))
        {
            _state.ClearStorage(contractAddress);
        }

        _state.SubtractFromBalance(env.ExecutingAccount, value, spec);

        ValueKeccak codeHash = ValueKeccak.Compute(initCode);
        // Prefer code from code cache (e.g. if create from a factory contract or copypasta)
        if (!_codeCache.TryGet(codeHash, out CodeInfo codeInfo))
        {
            codeInfo = new(initCode.ToArray());
            // Prime the code cache as likely to be used by more txs
            _codeCache.Set(codeHash, codeInfo);
        }

        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: env.ExecutingAccount,
            executingAccount: contractAddress,
            codeSource: null,
            codeInfo: codeInfo,
            inputData: default,
            transferValue: value,
            value: value
        );
        EvmState callState = new(
            callGas,
            callEnv,
            instruction == Instruction.CREATE2 ? ExecutionType.Create2 : ExecutionType.Create,
            false,
            snapshot,
            0L,
            0L,
            vmState.IsStatic,
            vmState,
            false,
            accountExists);

        return (outOfGas: false, callState);
    }

    [SkipLocalsInit]
    private static bool InstructionLog<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable, Instruction instruction)
        where TTracing : struct, IIsTracing
    {
        stack.PopUInt256(out UInt256 position);
        stack.PopUInt256(out UInt256 length);
        long topicsCount = instruction - Instruction.LOG0;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, length)) return false;
        if (!UpdateGas(
                GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                (long)length * GasCostOf.LogData, ref gasAvailable)) return false;

        ReadOnlyMemory<byte> data = vmState.Memory.Load(in position, length);
        Keccak[] topics = new Keccak[topicsCount];
        for (int i = 0; i < topicsCount; i++)
        {
            topics[i] = new Keccak(stack.PopWord256());
        }

        LogEntry logEntry = new(
            vmState.Env.ExecutingAccount,
            data.ToArray(),
            topics);
        vmState.Logs.Add(logEntry);

        return true;
    }

    [SkipLocalsInit]
    private bool InstructionSStore<TTracingInstructions, TTracingRefunds, TTracingStorage>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        // fail fast before the first storage read if gas is not enough even for reset
        if (!spec.UseNetGasMetering && !UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return false;

        if (spec.UseNetGasMeteringWithAStipendFix)
        {
            if (typeof(TTracingRefunds) == typeof(IsTracing))
                _txTracer.ReportExtraGasPressure(GasCostOf.CallStipend - spec.GetNetMeteredSStoreCost() + 1);
            if (gasAvailable <= GasCostOf.CallStipend) return false;
        }

        stack.PopUInt256(out UInt256 result);
        Span<byte> bytes = stack.PopWord256();
        bool newIsZero = bytes.IsZero();
        if (!newIsZero)
        {
            bytes = bytes.WithoutLeadingZeros().ToArray();
        }
        else
        {
            bytes = new byte[] { 0 };
        }

        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);

        if (!ChargeStorageAccessGas(
                ref gasAvailable,
                vmState,
                in storageCell,
                StorageAccessType.SSTORE,
                spec)) return false;

        Span<byte> currentValue = _state.Get(in storageCell);
        // Console.WriteLine($"current: {currentValue.ToHexString()} newValue {newValue.ToHexString()}");
        bool currentIsZero = currentValue.IsZero();

        bool newSameAsCurrent = (newIsZero && currentIsZero) || Bytes.AreEqual(currentValue, bytes);
        long sClearRefunds = RefundOf.SClear(spec.IsEip3529Enabled);

        if (!spec.UseNetGasMetering) // note that for this case we already deducted 5000
        {
            if (newIsZero)
            {
                if (!newSameAsCurrent)
                {
                    vmState.Refund += sClearRefunds;
                    if (typeof(TTracingRefunds) == typeof(IsTracing)) _txTracer.ReportRefund(sClearRefunds);
                }
            }
            else if (currentIsZero)
            {
                if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable)) return false;
            }
        }
        else // net metered
        {
            if (newSameAsCurrent)
            {
                if (!UpdateGas(spec.GetNetMeteredSStoreCost(), ref gasAvailable)) return false;
            }
            else // net metered, C != N
            {
                Span<byte> originalValue = _state.GetOriginal(in storageCell);
                bool originalIsZero = originalValue.IsZero();

                bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);
                if (currentSameAsOriginal)
                {
                    if (currentIsZero)
                    {
                        if (!UpdateGas(GasCostOf.SSet, ref gasAvailable)) return false;
                    }
                    else // net metered, current == original != new, !currentIsZero
                    {
                        if (!UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return false;

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (typeof(TTracingRefunds) == typeof(IsTracing)) _txTracer.ReportRefund(sClearRefunds);
                        }
                    }
                }
                else // net metered, new != current != original
                {
                    long netMeteredStoreCost = spec.GetNetMeteredSStoreCost();
                    if (!UpdateGas(netMeteredStoreCost, ref gasAvailable)) return false;

                    if (!originalIsZero) // net metered, new != current != original != 0
                    {
                        if (currentIsZero)
                        {
                            vmState.Refund -= sClearRefunds;
                            if (typeof(TTracingRefunds) == typeof(IsTracing)) _txTracer.ReportRefund(-sClearRefunds);
                        }

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (typeof(TTracingRefunds) == typeof(IsTracing)) _txTracer.ReportRefund(sClearRefunds);
                        }
                    }

                    bool newSameAsOriginal = Bytes.AreEqual(originalValue, bytes);
                    if (newSameAsOriginal)
                    {
                        long refundFromReversal;
                        if (originalIsZero)
                        {
                            refundFromReversal = spec.GetSetReversalRefund();
                        }
                        else
                        {
                            refundFromReversal = spec.GetClearReversalRefund();
                        }

                        vmState.Refund += refundFromReversal;
                        if (typeof(TTracingRefunds) == typeof(IsTracing)) _txTracer.ReportRefund(refundFromReversal);
                    }
                }
            }
        }

        if (!newSameAsCurrent)
        {
            _state.Set(in storageCell, newIsZero ? BytesZero : bytes.ToArray());
        }

        if (typeof(TTracingInstructions) == typeof(IsTracing))
        {
            Span<byte> valueToStore = newIsZero ? BytesZero : bytes;
            bytes = new byte[32]; // do not stackalloc here
            storageCell.Index.ToBigEndian(bytes);
            _txTracer.ReportStorageChange(bytes, valueToStore);
        }

        if (typeof(TTracingStorage) == typeof(IsTracing))
        {
            _txTracer.SetOperationStorage(storageCell.Address, result, bytes, currentValue);
        }

        return true;
    }

    private CallResult GetFailureReturn<TTracingInstructions>(long gasAvailable, EvmExceptionType exceptionType)
        where TTracingInstructions : struct, IIsTracing
    {
        if (typeof(TTracingInstructions) == typeof(IsTracing)) EndInstructionTraceError(gasAvailable, exceptionType);

        return exceptionType switch
        {
            EvmExceptionType.OutOfGas => CallResult.OutOfGasException,
            EvmExceptionType.BadInstruction => CallResult.InvalidInstructionException,
            EvmExceptionType.StaticCallViolation => CallResult.StaticCallViolationException,
            EvmExceptionType.InvalidSubroutineEntry => CallResult.InvalidSubroutineEntry,
            EvmExceptionType.InvalidSubroutineReturn => CallResult.InvalidSubroutineReturn,
            EvmExceptionType.StackOverflow => CallResult.StackOverflowException,
            EvmExceptionType.InvalidJumpDestination => CallResult.InvalidJumpDestination,
            EvmExceptionType.AccessViolation => CallResult.AccessViolationException,
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionType), exceptionType, "")
        };
    }

    private static void UpdateCurrentState(EvmState state, int pc, long gas, int stackHead)
    {
        state.ProgramCounter = pc;
        state.GasAvailable = gas;
        state.DataStackHead = stackHead;
    }

    private static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        if (vmState.Memory is null)
        {
            ThrowNotInitialized();
        }

        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length);
        if (memoryCost != 0L)
        {
            if (!UpdateGas(memoryCost, ref gasAvailable))
            {
                return false;
            }
        }

        return true;

        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowNotInitialized()
        {
            throw new InvalidOperationException("EVM memory has not been initialized properly.");
        }
    }

    private static bool Jump(in UInt256 jumpDest, ref int programCounter, in ExecutionEnvironment env, bool isSubroutine = false)
    {
        if (jumpDest > int.MaxValue)
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
            return false;
        }

        int jumpDestInt = (int)jumpDest;
        if (!env.CodeInfo.ValidateJump(jumpDestInt, isSubroutine))
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
            return false;
        }

        programCounter = jumpDestInt;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartInstructionTrace<TIsTracing>(Instruction instruction, EvmState vmState, long gasAvailable, int programCounter, in EvmStack<TIsTracing> stackValue)
        where TIsTracing : struct, IIsTracing
    {
        _txTracer.StartOperation(vmState.Env.CallDepth + 1, gasAvailable, instruction, programCounter, vmState.Env.TxExecutionContext.BlockExecutionContext.Header.IsPostMerge);
        if (_txTracer.IsTracingMemory)
        {
            _txTracer.SetOperationMemory(vmState.Memory?.GetTrace() ?? Enumerable.Empty<string>());
            _txTracer.SetOperationMemorySize(vmState.Memory?.Size ?? 0);
        }

        if (_txTracer.IsTracingStack)
        {
            _txTracer.SetOperationStack(stackValue.GetStackTrace());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndInstructionTrace(long gasAvailable, ulong memorySize)
    {
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
}
