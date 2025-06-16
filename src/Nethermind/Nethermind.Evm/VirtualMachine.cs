
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// #define ILVM_TESTING

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Evm.CodeAnalysis.IL;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;
using static Nethermind.Evm.CodeAnalysis.IL.IlInfo;

#if DEBUG
using Nethermind.Evm.Tracing.Debugger;
#endif

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]
[assembly: InternalsVisibleTo("Nethermind.Evm.Benchmark")]

namespace Nethermind.Evm;
using Int256;
using Nethermind.Evm.Config;
using Sigil;
using System.Diagnostics;
using System.IO;
using ZstdSharp.Unsafe;

public class VirtualMachine : IVirtualMachine
{
    public const int MaxCallDepth = 1024;
    public static readonly UInt256 P255Int = (UInt256)System.Numerics.BigInteger.Pow(2, 255);
    internal static ref readonly UInt256 P255 => ref P255Int;
    internal static readonly UInt256 BigInt256 = 256;
    public static readonly UInt256 BigInt32 = 32;

    internal static readonly byte[] BytesZero = [0];

    public static readonly byte[] BytesZero32 =
    {
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0
    };

    public static readonly byte[] BytesMax32 =
    {
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255
    };

    internal static readonly PrecompileExecutionFailureException PrecompileExecutionFailureException = new();
    internal static readonly OutOfGasException PrecompileOutOfGasException = new();

    private readonly IVirtualMachine _evm;

    private IVMConfig _vmConfig;
    public VirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider,
        ICodeInfoRepository codeInfoRepository,
        ILogManager? logManager,
        IVMConfig vmConfig = null)
    {
        ILogger logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
#if ILVM_TESTING
        _vmConfig = new VMConfig
        {
            IsILEvmEnabled = true,
            IsIlEvmAggressiveModeEnabled = !logger.IsTrace,
            IlEvmEnabledMode = ILMode.FULL_AOT_MODE,
            IlEvmBytecodeMinLength = 4,
            IlEvmBytecodeMaxLength = (int)24.KB(),
            IlEvmPersistPrecompiledContractsOnDisk = false,
            IlEvmContractsPerDllCount = 16,
            IlEvmAnalysisThreshold = 2,
            IlEvmAnalysisQueueMaxSize = 1,
            IlEvmAnalysisCoreUsage = 0.75f
        };
#else
        _vmConfig = vmConfig ?? new VMConfig();
#endif

        switch (_vmConfig.IlEvmEnabledMode)
        {
            case ILMode.NO_ILVM when !logger.IsTrace:
                _evm = new VirtualMachine<NotTracing, NotOptimizing>(blockhashProvider, codeInfoRepository, specProvider, _vmConfig, logger);
                break;
            case ILMode.NO_ILVM when logger.IsTrace:
                _evm = new VirtualMachine<IsTracing, NotOptimizing>(blockhashProvider, codeInfoRepository, specProvider, _vmConfig, logger);
                break;
            default:
                if (!logger.IsTrace)
                {
                    _evm = new VirtualMachine<NotTracing, IsPrecompiling>(blockhashProvider, codeInfoRepository, specProvider, _vmConfig, logger);
                }
                else
                {
                    _evm = new VirtualMachine<IsTracing, IsPrecompiling>(blockhashProvider, codeInfoRepository, specProvider, _vmConfig, logger);
                }
                break;
        }
    }

    public TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
        where TTracingActions : struct, VirtualMachine.IIsTracing
        => _evm.Run<TTracingActions>(state, worldState, txTracer);

    public readonly ref struct CallResult
    {
        public static CallResult InvalidSubroutineEntry => new(EvmExceptionType.InvalidSubroutineEntry);
        public static CallResult InvalidSubroutineReturn => new(EvmExceptionType.InvalidSubroutineReturn);
        public static CallResult OutOfGasException => new(EvmExceptionType.OutOfGas);
        public static CallResult AccessViolationException => new(EvmExceptionType.AccessViolation);
        public static CallResult InvalidJumpDestination => new(EvmExceptionType.InvalidJumpDestination);
        public static CallResult InvalidInstructionException => new(EvmExceptionType.BadInstruction);
        public static CallResult StaticCallViolationException => new(EvmExceptionType.StaticCallViolation);
        public static CallResult StackOverflowException => new(EvmExceptionType.StackOverflow); // TODO: use these to avoid CALL POP attacks
        public static CallResult StackUnderflowException => new(EvmExceptionType.StackUnderflow); // TODO: use these to avoid CALL POP attacks
        public static CallResult InvalidCodeException => new(EvmExceptionType.InvalidCode);
        public static CallResult Empty => new(default, null);
        public static object BoxedEmpty { get; } = new object();

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
        public ReadOnlyMemory<byte> Output { get; }
        public EvmExceptionType ExceptionType { get; }
        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess { get; } // TODO: check this behaviour as it seems it is required and previously that was not the case
        public bool IsReturn => StateToExecute is null;
        public bool IsException => ExceptionType != EvmExceptionType.None;
    }

    public interface IIsTracing { }
    public readonly struct NotTracing : IIsTracing { }
    public readonly struct IsTracing : IIsTracing { }

    public interface IIsOptimizing { }
    public readonly struct NotOptimizing : IIsOptimizing { }
    public readonly struct IsPrecompiling : IIsOptimizing { }
}

public sealed class VirtualMachine<TLogger, TOptimizing> : IVirtualMachine
    where TLogger : struct, IIsTracing
    where TOptimizing : struct, IIsOptimizing
{
    private readonly byte[] _chainId;

    private readonly IBlockhashProvider _blockhashProvider;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private IWorldState _state = null!;
    private readonly Stack<EvmState> _stateStack = new();
    private (Address Address, bool ShouldDelete) _parityTouchBugAccount = (Address.FromNumber(3), false);
    private ReadOnlyMemory<byte> _returnDataBuffer = Array.Empty<byte>();
    private ITxTracer _txTracer = NullTxTracer.Instance;
    private readonly IVMConfig _vmConfig;
    public VirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ICodeInfoRepository codeInfoRepository,
        ISpecProvider? specProvider,
        IVMConfig vmConfig,
        ILogger? logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blockhashProvider = blockhashProvider ?? throw new ArgumentNullException(nameof(blockhashProvider));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _chainId = ((UInt256)specProvider.ChainId).ToBigEndian();
        _vmConfig = vmConfig;
    }

    public TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
        where TTracingActions : struct, IIsTracing
    {
        _txTracer = txTracer;
        _state = worldState;

        ref readonly TxExecutionContext txExecutionContext = ref state.Env.TxExecutionContext;
        ICodeInfoRepository codeInfoRepository = txExecutionContext.CodeInfoRepository;
        IReleaseSpec spec = _specProvider.GetSpec(txExecutionContext.BlockExecutionContext.Header.Number, txExecutionContext.BlockExecutionContext.Header.Timestamp);
        EvmState currentState = state;
        ReadOnlyMemory<byte>? previousCallResult = null;
        ZeroPaddedSpan previousCallOutput = ZeroPaddedSpan.Empty;
        UInt256 previousCallOutputDestination = UInt256.Zero;
        bool isTracing = _txTracer.IsTracing;

        while (true)
        {
            Exception? failure = null;
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
                        if (callResult.IsException)
                        {
                            failure = VirtualMachine.PrecompileOutOfGasException;
                            goto Failure;
                        }
                        if (currentState.IsPrecompile && currentState.IsTopLevel)
                        {
                            failure = VirtualMachine.PrecompileExecutionFailureException;
                            // TODO: when direct / calls are treated same we should not need such differentiation
                            goto Failure;
                        }

                        // TODO: testing it as it seems the way to pass zkSNARKs tests
                        currentState.GasAvailable = 0;
                    }
                }
                else
                {
                    if (typeof(TTracingActions) == typeof(IsTracing) && !currentState.IsContinuation)
                    {
                        _txTracer.ReportAction(currentState.GasAvailable,
                            currentState.Env.Value,
                            currentState.From,
                            currentState.To,
                            currentState.ExecutionType.IsAnyCreate()
                                ? currentState.Env.CodeInfo.MachineCode
                                : currentState.Env.InputData,
                            currentState.ExecutionType);

                        if (_txTracer.IsTracingCode) _txTracer.ReportByteCode(currentState.Env.CodeInfo.MachineCode);
                    }

                    callResult = !_txTracer.IsTracingInstructions
                        ? ExecuteCall<NotTracing>(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec)
                        : ExecuteCall<IsTracing>(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination, spec);

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
                        _state.Restore(currentState.Snapshot);

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
                            _txTracer.ReportActionRevert(currentState.ExecutionType.IsAnyCreate()
                                    ? currentState.GasAvailable - codeDepositGasCost
                                    : currentState.GasAvailable,
                                callResult.Output);
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
                        currentState.AccessTracker.DestroyList,
                        (IReadOnlyCollection<LogEntry>)currentState.AccessTracker.Logs,
                        callResult.ShouldRevert,
                        isTracerConnected: isTracing,
                        _logger);
                }

                Address callCodeOwner = currentState.Env.ExecutingAccount;
                using (EvmState previousState = currentState)
                {
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
                                ReadOnlyMemory<byte> code = callResult.Output;
                                codeInfoRepository.InsertCode(_state, code, callCodeOwner, spec);

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
                            previousCallOutput = callResult.Output.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                            previousCallOutputDestination = (ulong)previousState.OutputDestination;
                            if (previousState.IsPrecompile)
                            {
                                // parity induced if else for vmtrace
                                if (_txTracer.IsTracingInstructions)
                                {
                                    _txTracer.ReportMemoryChange(previousCallOutputDestination, previousCallOutput);
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
                        previousCallOutput = callResult.Output.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)previousState.OutputLength));
                        previousCallOutputDestination = (ulong)previousState.OutputDestination;


                        if (typeof(TTracingActions) == typeof(IsTracing))
                        {
                            _txTracer.ReportActionRevert(previousState.GasAvailable, callResult.Output);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is EvmException or OverflowException)
            {
                failure = ex;
                goto Failure;
            }

            continue;

        Failure:
            {
                if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"exception ({failure.GetType().Name}) in {currentState.ExecutionType} at depth {currentState.Env.CallDepth} - restoring snapshot");

                _state.Restore(currentState.Snapshot);

                RevertParityTouchBugAccount(spec);

                if (txTracer.IsTracingInstructions)
                {
                    txTracer.ReportOperationRemainingGas(0);
                    txTracer.ReportOperationError(failure is EvmException evmException ? evmException.ExceptionType : EvmExceptionType.Other);
                }

                if (typeof(TTracingActions) == typeof(IsTracing))
                {
                    EvmException evmException = failure as EvmException;
                    _txTracer.ReportActionError(evmException?.ExceptionType ?? EvmExceptionType.Other);
                }

                if (currentState.IsTopLevel)
                {
                    return new TransactionSubstate(failure is OverflowException ? EvmExceptionType.Other : (failure as EvmException).ExceptionType, isTracing);
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

    public static bool UpdateGas(long gasCost, ref long gasAvailable)
    {
        if (gasAvailable < gasCost)
        {
            return false;
        }

        gasAvailable -= gasCost;
        return true;
    }

    public static void UpdateGasUp(long refund, ref long gasAvailable)
    {
        gasAvailable += refund;
    }

    public static bool ChargeAccountAccessGas(ref long gasAvailable, EvmState vmState, Address address, bool chargeForDelegation, IWorldState state, IReleaseSpec spec, ITxTracer tracer, bool chargeForWarm = true)
    {
        if (!spec.UseHotAndColdStorage)
        {
            return true;
        }
        bool notOutOfGas = ChargeAccountGas(ref gasAvailable, vmState, address, spec);
        return notOutOfGas
               && (!chargeForDelegation
                   || !vmState.Env.TxExecutionContext.CodeInfoRepository.TryGetDelegation(state, address, out Address delegated)
                   || ChargeAccountGas(ref gasAvailable, vmState, delegated, spec));

        bool ChargeAccountGas(ref long gasAvailable, EvmState vmState, Address address, IReleaseSpec spec)
        {
            bool result = true;
            if (tracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
            {
                vmState.AccessTracker.WarmUp(address);
            }

            if (vmState.AccessTracker.IsCold(address) && !address.IsPrecompile(spec))
            {
                result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
                vmState.AccessTracker.WarmUp(address);
            }
            else if (chargeForWarm)
            {
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
            return result;
        }
    }

    public enum StorageAccessType
    {
        SLOAD,
        SSTORE
    }

    static public bool ChargeStorageAccessGas(
        ref long gasAvailable,
        EvmState vmState,
        in StorageCell storageCell,
        StorageAccessType storageAccessType,
        IReleaseSpec spec,
        ITxTracer txTracer)
    {
        // Console.WriteLine($"Accessing {storageCell} {storageAccessType}");

        bool result = true;
        if (spec.UseHotAndColdStorage)
        {
            if (txTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
            {
                vmState.AccessTracker.WarmUp(in storageCell);
            }

            if (vmState.AccessTracker.IsCold(in storageCell))
            {
                result = UpdateGas(GasCostOf.ColdSLoad, ref gasAvailable);
                vmState.AccessTracker.WarmUp(in storageCell);
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

        bool wasCreated = _state.AddToBalanceAndCreateIfNotExists(state.Env.ExecutingAccount, transferValue, spec);

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
            return new(default, false, true, EvmExceptionType.OutOfGas);
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
            CallResult callResult = new(default, false, true);
            return callResult;
        }
    }

    /// <remarks>
    /// Struct generic parameter is used to burn out all the if statements and inner code
    /// by typeof(TTracingInstructions) == typeof(NotTracing) checks that are evaluated to constant
    /// values at compile time.
    /// </remarks>
    [SkipLocalsInit]
    private CallResult ExecuteCall<TTracingInstructions>(EvmState vmState, ReadOnlyMemory<byte>? previousCallResult, ZeroPaddedSpan previousCallOutput, scoped in UInt256 previousCallOutputDestination, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
    {
        ref readonly ExecutionEnvironment env = ref vmState.Env;
        if (!vmState.IsContinuation)
        {
            _state.AddToBalanceAndCreateIfNotExists(env.ExecutingAccount, env.TransferValue, spec);

            if (vmState.ExecutionType.IsAnyCreate() && spec.ClearEmptyAccountWhenTouched)
            {
                _state.IncrementNonce(env.ExecutingAccount);
            }

            if (typeof(IsPrecompiling) == typeof(TOptimizing) && _vmConfig.IlEvmEnabledMode is ILMode.DYNAMIC_AOT_MODE)
            {
#if ILVM_TESTING
                if (vmState.Env.CodeInfo.IlInfo.IsNotProcessed && vmState.Env.CodeInfo.Codehash is not null && vmState.Env.CodeInfo.MachineCode.Length < 24.KB())
                {
                    IlAnalyzer.Analyse(env.CodeInfo, ILMode.FULL_AOT_MODE, _vmConfig, _logger);
                }
#else
                if (vmState.Env.CodeInfo.IlInfo.IsNotProcessed)
                {
                    env.CodeInfo.NoticeExecution(_vmConfig, _logger, spec);
                }
#endif
            }

        }

        if (env.CodeInfo.MachineCode.Length == 0)
        {
            if (!vmState.IsTopLevel)
            {
                Metrics.IncrementEmptyCalls();
            }
            goto Empty;
        }

        vmState.InitializeStacks();
        EvmStack<TTracingInstructions> stack = new(vmState.DataStackHead, _txTracer, vmState.DataStack.AsSpan());
        long gasAvailable = vmState.GasAvailable;

        if (previousCallResult is not null)
        {
            stack.PushBytes(previousCallResult.Value.Span);
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

        if (typeof(IsPrecompiling) == typeof(TOptimizing))
        {
            if (env.CodeInfo.IlInfo.IsPrecompiled)
            {
                Metrics.IlvmAotPrecompiledCalls++; // this will treat continuations as new calls

                if (_logger.IsDebug)
                {
                    _logger.Debug($"{env.CodeInfo.Codehash} precompile is being called");
                }

                int programCounter = vmState.ProgramCounter;
                var codeAsSpan = env.CodeInfo.MachineCode.Span;
                ref ILChunkExecutionState chunkExecutionState = ref vmState.IlExecutionStepState;

                if (env.CodeInfo.IlInfo.PrecompiledContract(
                        in MemoryMarshal.GetReference(codeAsSpan),
                        spec, _specProvider,
                        _blockhashProvider,
                        vmState.Env.TxExecutionContext.CodeInfoRepository,
                        vmState,
                        _state,
                        _returnDataBuffer,
                        ref gasAvailable,
                        ref programCounter,
                        ref stack.Head,
                        ref Add(ref As<byte, Word>(ref MemoryMarshal.GetReference(stack.Bytes)), stack.Head),
                        _txTracer,
                        _logger,
                        ref chunkExecutionState)
                   )
                {
                    UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head - 1);
                    Metrics.IlvmAotPrecompiledCalls--; // this will treat continuations as new calls
                    return new CallResult(chunkExecutionState.CallResult);
                }

                UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);

                switch (chunkExecutionState.ContractState)
                {
                    case ContractState.Return or ContractState.Revert:
                        return new CallResult(chunkExecutionState.ReturnData.ToArray(), null, shouldRevert: chunkExecutionState.ContractState is ContractState.Revert);
                    case ContractState.Failed:
                        return GetFailureReturn<TTracingInstructions>(gasAvailable, chunkExecutionState.ExceptionType);
                    default:
                        return CallResult.Empty;
                }
            }
        }

        // Struct generic parameter is used to burn out all the if statements
        // and inner code by typeof(TTracing) == typeof(NotTracing)
        // checks that are evaluated to constant values at compile time.
        // This only works for structs, not for classes or interface types
        // which use shared generics.
        if (!_txTracer.IsTracingRefunds)
        {
            return _txTracer.IsTracingOpLevelStorage
                    ? ExecuteCode<TTracingInstructions, NotTracing, IsTracing>(vmState, ref stack, gasAvailable, spec)
                    : ExecuteCode<TTracingInstructions, NotTracing, NotTracing>(vmState, ref stack, gasAvailable, spec);
        }
        else
        {
            return _txTracer.IsTracingOpLevelStorage
                    ? ExecuteCode<TTracingInstructions, IsTracing, IsTracing>(vmState, ref stack, gasAvailable, spec)
                    : ExecuteCode<TTracingInstructions, IsTracing, NotTracing>(vmState, ref stack, gasAvailable, spec);
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
        ReadOnlySpan<byte> code = env.CodeInfo.MachineCode.Span;
        EvmExceptionType exceptionType = EvmExceptionType.None;
        IlInfo? ilInfo = env.CodeInfo.IlInfo;

        vmState.IlExecutionStepState.ContractState = ContractState.Running;

        bool isRevert = false;
#if DEBUG
        DebugTracer? debugger = _txTracer.GetTracer<DebugTracer>();
#endif
        SkipInit(out UInt256 a);
        SkipInit(out UInt256 b);
        SkipInit(out UInt256 c);
        SkipInit(out UInt256 result);
        SkipInit(out StorageCell storageCell);
        object returnData = null;
        ZeroPaddedSpan slice;
        bool isCancelable = _txTracer.IsCancelable;
        uint codeLength = (uint)code.Length;
        var chunkExecutionResult = new ILChunkExecutionState();
        while ((uint)programCounter < codeLength)
        {
#if DEBUG
            debugger?.TryWait(ref vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif

            Instruction instruction = (Instruction)code[programCounter];

            if (isCancelable && _txTracer.IsCancelled)
            {
                ThrowOperationCanceledException();
            }

            // Evaluated to constant at compile time and code elided if not tracing
            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartInstructionTrace(_txTracer, instruction, vmState, gasAvailable, programCounter, stack.Head);

            programCounter++;
            Span<byte> bytes;
            switch (instruction)
            {
                case Instruction.STOP:
                    {
                        goto EmptyReturn;
                    }
                case Instruction.ADD:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        UInt256.Add(in a, in b, out result);
                        stack.PushUInt256(result);

                        break;
                    }
                case Instruction.MUL:
                    {
                        gasAvailable -= GasCostOf.Low;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        UInt256.Multiply(in a, in b, out result);
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.SUB:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        UInt256.Subtract(in a, in b, out result);

                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.DIV:
                    {
                        gasAvailable -= GasCostOf.Low;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        UInt256.Mod(in a, in b, out result);
                        stack.PushUInt256(in result);
                        break;
                    }
                case Instruction.SMOD:
                    {
                        gasAvailable -= GasCostOf.Low;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        if (!stack.PopUInt256(out c)) goto StackUnderflow;

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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        if (!stack.PopUInt256(out c)) goto StackUnderflow;

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

                        Metrics.ExpOpcode++;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (a >= BigInt32)
                        {
                            if (!stack.EnsureDepth(1)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;

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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
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

                        ref byte bytesRef = ref stack.PopBytesByRef();
                        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
                        Vector256<byte> aVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);

                        bytesRef = ref stack.PopBytesByRef();
                        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
                        Vector256<byte> bVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);

                        WriteUnaligned(ref stack.PushBytesRef(), Vector256.BitwiseAnd(aVec, bVec));
                        break;
                    }
                case Instruction.OR:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        ref byte bytesRef = ref stack.PopBytesByRef();
                        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
                        Vector256<byte> aVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);

                        bytesRef = ref stack.PopBytesByRef();
                        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
                        Vector256<byte> bVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);

                        WriteUnaligned(ref stack.PushBytesRef(), Vector256.BitwiseOr(aVec, bVec));
                        break;
                    }
                case Instruction.XOR:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        ref byte bytesRef = ref stack.PopBytesByRef();
                        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
                        Vector256<byte> aVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);

                        bytesRef = ref stack.PopBytesByRef();
                        if (IsNullRef(ref bytesRef)) goto StackUnderflow;
                        Vector256<byte> bVec = ReadUnaligned<Vector256<byte>>(ref bytesRef);

                        WriteUnaligned(ref stack.PushBytesRef(), Vector256.Xor(aVec, bVec));
                        break;
                    }
                case Instruction.NOT:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        ref byte bytesRef = ref stack.PopBytesByRef();
                        if (IsNullRef(ref bytesRef)) goto StackUnderflow;

                        Vector256<byte> negVec = Vector256.OnesComplement(ReadUnaligned<Vector256<byte>>(ref bytesRef));

                        WriteUnaligned(ref stack.PushBytesRef(), negVec);
                        break;
                    }
                case Instruction.BYTE:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
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
                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        gasAvailable -= GasCostOf.Sha3 + GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(in b, out bool outOfGas);
                        if (outOfGas) goto OutOfGas;

                        if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, b)) goto OutOfGas;

                        bytes = vmState.Memory.LoadSpan(in a, b);

                        // Compute the KECCAK256 directly to the stack slot
                        KeccakCache.ComputeTo(bytes, out As<byte, ValueHash256>(ref stack.PushBytesRef()));
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
                        if (address is null) goto StackUnderflow;

                        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, false, _state, spec, _txTracer)) goto OutOfGas;

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

                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
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
                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
                        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result, out bool outOfGas);
                        if (outOfGas) goto OutOfGas;

                        if (!result.IsZero)
                        {
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, in result)) goto OutOfGas;

                            slice = env.InputData.SliceWithZeroPadding(b, (int)result);
                            vmState.Memory.Save(in a, in slice);
                            if (typeof(TTracingInstructions) == typeof(IsTracing))
                            {
                                _txTracer.ReportMemoryChange(a, slice);
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
                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
                        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result, out bool outOfGas);
                        if (outOfGas) goto OutOfGas;

                        if (!result.IsZero)
                        {
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) goto OutOfGas;

                            slice = code.SliceWithZeroPadding(in b, (int)result);
                            vmState.Memory.Save(in a, in slice);
                            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(a, in slice);
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
                        if (address is null) goto StackUnderflow;

                        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, false, _state, spec, _txTracer)) goto OutOfGas;

                        if (typeof(TTracingInstructions) != typeof(IsTracing) && programCounter < code.Length)
                        {
                            bool optimizeAccess = false;
                            Instruction nextInstruction = (Instruction)code[programCounter];
                            // Thrown away result
                            if (nextInstruction == Instruction.POP)
                            {
                                programCounter++;
                                // Add gas cost for POP
                                gasAvailable -= GasCostOf.Base;
                                break;
                            }
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

                        InstructionExtCodeSize(address, ref stack, txCtx.CodeInfoRepository, spec);
                        break;
                    }
                case Instruction.EXTCODECOPY:
                    {
                        Address address = stack.PopAddress();
                        if (address is null) goto StackUnderflow;
                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        if (!stack.PopUInt256(out result)) goto StackUnderflow;

                        gasAvailable -= spec.GetExtCodeCost() + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result, out bool outOfGas);
                        if (outOfGas) goto OutOfGas;

                        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, false, _state, spec, _txTracer)) goto OutOfGas;

                        if (!result.IsZero)
                        {
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) goto OutOfGas;

                            Address delegation;
                            ReadOnlyMemory<byte> externalCode = txCtx.CodeInfoRepository.GetCachedCodeInfo(_state, address, false, spec, out delegation).MachineCode;
                            slice = externalCode.SliceWithZeroPadding(b, (int)result);
                            vmState.Memory.Save(in a, in slice);
                            if (typeof(TTracingInstructions) == typeof(IsTracing))
                            {
                                _txTracer.ReportMemoryChange(a, in slice);
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

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
                        gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in result, out bool outOfGas);
                        if (outOfGas) goto OutOfGas;

                        if (UInt256.AddOverflow(result, b, out c) || c > _returnDataBuffer.Length)
                        {
                            goto AccessViolation;
                        }

                        if (!result.IsZero)
                        {
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, in a, result)) goto OutOfGas;

                            slice = _returnDataBuffer.Span.SliceWithZeroPadding(b, (int)result);
                            vmState.Memory.Save(in a, in slice);
                            if (typeof(TTracingInstructions) == typeof(IsTracing))
                            {
                                _txTracer.ReportMemoryChange(a, in slice);
                            }
                        }

                        break;
                    }
                case Instruction.BLOCKHASH:
                    {
                        Metrics.BlockhashOpcode++;

                        gasAvailable -= GasCostOf.BlockHash;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        long number = a.ToLong();

                        Hash256? blockHash = _blockhashProvider.GetBlockhash(blkCtx.Header, number);

                        stack.PushBytes(blockHash is not null ? blockHash.Bytes : BytesZero32);

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
                case Instruction.BLOBHASH:
                    {
                        if (!spec.IsEip4844Enabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.BlobHash;

                        if (!stack.PopUInt256(out result)) goto StackUnderflow;

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
                case Instruction.BLOBBASEFEE:
                    {
                        if (!spec.BlobBaseFeeEnabled || !blkCtx.BlobBaseFee.HasValue) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.Base;

                        result = blkCtx.BlobBaseFee.Value;
                        stack.PushUInt256(in result);
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

                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
                        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) goto OutOfGas;
                        bytes = vmState.Memory.LoadSpan(in result);
                        if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(result, bytes);

                        stack.PushBytes(bytes);
                        break;
                    }
                case Instruction.MSTORE:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        if (!stack.PopUInt256(out result)) goto StackUnderflow;

                        bytes = stack.PopWord256();
                        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, in BigInt32)) goto OutOfGas;
                        vmState.Memory.SaveWord(in result, bytes);
                        if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(result, bytes);

                        break;
                    }
                case Instruction.MSTORE8:
                    {
                        gasAvailable -= GasCostOf.VeryLow;

                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
                        byte data = stack.PopByte();
                        if (!UpdateMemoryCost(vmState, ref gasAvailable, in result, UInt256.One)) goto OutOfGas;
                        vmState.Memory.SaveByte(in result, data);
                        if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(result, data);

                        break;
                    }
                case Instruction.SLOAD:
                    {
                        exceptionType = InstructionSLoad<TTracingInstructions, TTracingStorage>(vmState, ref stack, ref gasAvailable, spec);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                        break;
                    }
                case Instruction.SSTORE:
                    {
                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
                        ReadOnlySpan<byte> bytesSpan = stack.PopWord256();
                        if (vmState.IsStatic) goto StaticCallViolation;

                        exceptionType = InstructionSStore<TTracingInstructions, TTracingRefunds, TTracingStorage>(vmState, _state, ref gasAvailable, ref result, ref bytesSpan, spec, _txTracer);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                        break;
                    }
                case Instruction.JUMP:
                    {
                        gasAvailable -= GasCostOf.Mid;

                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
                        if (!Jump(result, ref programCounter, in env)) goto InvalidJumpDestination;
                        break;
                    }
                case Instruction.JUMPI:
                    {
                        gasAvailable -= GasCostOf.High;

                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
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

                        if (!stack.Dup(instruction - Instruction.DUP1 + 1)) goto StackUnderflow;
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

                        if (!stack.Swap(instruction - Instruction.SWAP1 + 2)) goto StackUnderflow;
                        break;
                    }
                case Instruction.LOG0:
                case Instruction.LOG1:
                case Instruction.LOG2:
                case Instruction.LOG3:
                case Instruction.LOG4:
                    {
                        if (vmState.IsStatic) goto StaticCallViolation;

                        exceptionType = InstructionLog(vmState, ref stack, ref gasAvailable, instruction);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;
                        break;
                    }
                case Instruction.CREATE:
                case Instruction.CREATE2:
                    {
                        Metrics.IncrementCreates();
                        if (!spec.Create2OpcodeEnabled && instruction == Instruction.CREATE2) goto InvalidInstruction;

                        if (vmState.IsStatic) goto StaticCallViolation;

                        if (!UpdateGas(GasCostOf.Create, ref gasAvailable)) goto OutOfGas;
                        if (!stack.PopUInt256(out UInt256 value) ||
                            !stack.PopUInt256(out UInt256 memoryPositionOfInitCode) ||
                            !stack.PopUInt256(out UInt256 initCodeLength))
                            goto StackUnderflow;

                        Span<byte> salt = default;
                        if (instruction == Instruction.CREATE2)
                        {
                            salt = stack.PopWord256();
                        }


                        exceptionType = InstructionCreate<TTracingInstructions>(
                            vmState, _state, ref gasAvailable, spec, _txTracer, _logger,
                            instruction,
                            value, memoryPositionOfInitCode, initCodeLength, salt,
                            out UInt256? earlyStatusPush,
                            ref _returnDataBuffer,
                            out returnData);

                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                        if (earlyStatusPush is not null)
                        {
                            stack.PushUInt256(earlyStatusPush.Value);
                        }

                        if (returnData is null) break;

                        goto DataReturnNoTrace;
                    }
                case Instruction.RETURN:
                    {
                        exceptionType = InstructionReturn(vmState, ref stack, ref gasAvailable, out returnData);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                        vmState.IlExecutionStepState.ContractState = ContractState.Return;
                        goto DataReturn;
                    }
                case Instruction.CALL:
                case Instruction.CALLCODE:
                case Instruction.DELEGATECALL:
                case Instruction.STATICCALL:
                    {

                        if (instruction == Instruction.DELEGATECALL && !spec.DelegateCallEnabled ||
                            instruction == Instruction.STATICCALL && !spec.StaticCallEnabled)
                            goto InvalidInstruction;

                        if (!stack.PopUInt256(out UInt256 gasLimit)) goto StackUnderflow;

                        Address codeSource = stack.PopAddress();
                        if (codeSource is null) goto StackUnderflow;

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
                                if (!stack.PopUInt256(out callValue)) goto StackUnderflow;
                                break;
                        }

                        if (!stack.PopUInt256(out UInt256 dataOffset)) goto StackUnderflow;
                        if (!stack.PopUInt256(out UInt256 dataSize)) goto StackUnderflow;
                        if (!stack.PopUInt256(out UInt256 outputOffset)) goto StackUnderflow;
                        if (!stack.PopUInt256(out UInt256 outputSize)) goto StackUnderflow;


                        exceptionType = InstructionCall<TTracingInstructions, TTracingRefunds>(
                            vmState, _state, ref gasAvailable, spec, _txTracer, _logger,
                            instruction,
                            gasLimit,
                            codeSource,
                            callValue,
                            dataOffset,
                            dataSize,
                            outputOffset,
                            outputSize,
                            out UInt256? toPushToStack,
                            ref _returnDataBuffer,
                            out returnData);

                        if (toPushToStack is not null)
                        {
                            stack.PushUInt256(toPushToStack.Value);
                        }

                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                        if (returnData is null)
                        {
                            break;
                        }
                        if (ReferenceEquals(returnData, CallResult.BoxedEmpty))
                        {
                            // Non contract call continue rather than constructing a new frame
                            continue;
                        }

                        goto DataReturn;
                    }
                case Instruction.REVERT:
                    {
                        if (!spec.RevertOpcodeEnabled) goto InvalidInstruction;

                        exceptionType = InstructionRevert(vmState, ref stack, ref gasAvailable, out returnData);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                        isRevert = true;
                        vmState.IlExecutionStepState.ContractState = ContractState.Revert;
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

                        Address inheritor = stack.PopAddress();
                        if (inheritor is null) goto StackUnderflow;

                        exceptionType = InstructionSelfDestruct(vmState, _state, inheritor, ref gasAvailable, spec, _txTracer);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                        goto EmptyReturn;
                    }
                case Instruction.SHL:
                    {
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.VeryLow;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (a >= 256UL)
                        {
                            stack.PopLimbo();
                            stack.PushZero();
                        }
                        else
                        {
                            if (!stack.PopUInt256(out b)) goto StackUnderflow;
                            result = b << (int)a.u0;
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.SHR:
                    {
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.VeryLow;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (a >= 256)
                        {
                            stack.PopLimbo();
                            stack.PushZero();
                        }
                        else
                        {
                            if (!stack.PopUInt256(out b)) goto StackUnderflow;
                            result = b >> (int)a.u0;
                            stack.PushUInt256(in result);
                        }

                        break;
                    }
                case Instruction.SAR:
                    {
                        if (!spec.ShiftOpcodesEnabled) goto InvalidInstruction;

                        gasAvailable -= GasCostOf.VeryLow;

                        if (!stack.PopUInt256(out a)) goto StackUnderflow;
                        if (!stack.PopUInt256(out b)) goto StackUnderflow;
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
                        if (address is null) goto StackUnderflow;
                        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, address, false, _state, spec, _txTracer)) goto OutOfGas;
                        if (_state.AccountExists(address)
                            && !_state.IsDeadAccount(address))
                        {
                            stack.PushBytes(env.TxExecutionContext.CodeInfoRepository.GetExecutableCodeHash(_state, address).BytesAsSpan);
                        }
                        else
                        {
                            stack.PushZero();
                        }

                        break;
                    }
                case Instruction.TLOAD:
                    {
                        if (!spec.TransientStorageEnabled) goto InvalidInstruction;

                        Metrics.TloadOpcode++;
                        gasAvailable -= GasCostOf.TLoad;

                        if (!stack.PopUInt256(out result)) goto StackUnderflow;
                        storageCell = new(env.ExecutingAccount, result);

                        ReadOnlySpan<byte> value = _state.GetTransientState(in storageCell);
                        stack.PushBytes(value);

                        if (typeof(TTracingStorage) == typeof(IsTracing))
                        {
                            if (gasAvailable < 0) goto OutOfGas;
                            _txTracer.LoadOperationTransientStorage(storageCell.Address, result, value);
                        }

                        break;
                    }
                case Instruction.TSTORE:
                    {
                        if (!spec.TransientStorageEnabled) goto InvalidInstruction;
                        {
                            Metrics.TstoreOpcode++;

                            if (vmState.IsStatic) goto StaticCallViolation;

                            gasAvailable -= GasCostOf.TStore;

                            if (!stack.PopUInt256(out result)) goto StackUnderflow;
                            storageCell = new(env.ExecutingAccount, result);
                            bytes = stack.PopWord256();

                            _state.SetTransientState(in storageCell, !bytes.IsZero() ? bytes.ToArray() : BytesZero32);

                            if (typeof(TTracingStorage) == typeof(IsTracing))
                            {
                                if (gasAvailable < 0) goto OutOfGas;
                                ReadOnlySpan<byte> currentValue = _state.GetTransientState(in storageCell);
                                _txTracer.SetOperationTransientStorage(storageCell.Address, result, bytes, currentValue);
                            }

                            break;
                        }
                    }
                case Instruction.MCOPY:
                    {
                        if (!spec.MCopyIncluded) goto InvalidInstruction;
                        {
                            Metrics.MCopyOpcode++;

                            if (!stack.PopUInt256(out a)) goto StackUnderflow;
                            if (!stack.PopUInt256(out b)) goto StackUnderflow;
                            if (!stack.PopUInt256(out c)) goto StackUnderflow;

                            gasAvailable -= GasCostOf.VeryLow + GasCostOf.VeryLow * EvmPooledMemory.Div32Ceiling(c, out bool outOfGas);
                            if (outOfGas) goto OutOfGas;
                            if (!UpdateMemoryCost(vmState, ref gasAvailable, UInt256.Max(b, a), c)) goto OutOfGas;

                            bytes = vmState.Memory.LoadSpan(in b, c);
                            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(b, bytes);

                            vmState.Memory.Save(in a, bytes);
                            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportMemoryChange(a, bytes);

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
                EndInstructionTrace(_txTracer, gasAvailable, vmState.Memory.Size);
            }
        }

        goto EmptyReturnNoTrace;

    // Common exit errors, goto labels to reduce in loop code duplication and to keep loop body smaller
    EmptyReturn:
        if (typeof(TTracingInstructions) == typeof(IsTracing)) EndInstructionTrace(_txTracer, gasAvailable, vmState.Memory.Size);
        EmptyReturnNoTrace:
        // Ensure gas is positive before updating state
        if (gasAvailable < 0) goto OutOfGas;
        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);
#if DEBUG
        debugger?.TryWait(ref vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
        vmState.IlExecutionStepState.ContractState = ContractState.Finished;
        return CallResult.Empty;
    DataReturn:
        if (typeof(TTracingInstructions) == typeof(IsTracing)) EndInstructionTrace(_txTracer, gasAvailable, vmState.Memory.Size);
        DataReturnNoTrace:
        // Ensure gas is positive before updating state
        if (gasAvailable < 0) goto OutOfGas;
        UpdateCurrentState(vmState, programCounter, gasAvailable, stack.Head);

        if (returnData is EvmState state)
        {
            vmState.IlExecutionStepState.ContractState = ContractState.Halted;
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
    StackUnderflow:
        exceptionType = EvmExceptionType.StackUnderflow;
        goto ReturnFailure;
    InvalidJumpDestination:
        exceptionType = EvmExceptionType.InvalidJumpDestination;
        goto ReturnFailure;
    AccessViolation:
        exceptionType = EvmExceptionType.AccessViolation;
    ReturnFailure:
        vmState.IlExecutionStepState.ContractState = ContractState.Failed;
        return GetFailureReturn<TTracingInstructions>(gasAvailable, exceptionType);

        [DoesNotReturn]
        static void ThrowOperationCanceledException() =>
            throw new OperationCanceledException("Cancellation Requested");
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InstructionExtCodeSize<TTracingInstructions>(Address address, ref EvmStack<TTracingInstructions> stack, ICodeInfoRepository codeInfoRepository, IReleaseSpec spec) where TTracingInstructions : struct, IIsTracing
    {
        Address delegation;
        ReadOnlyMemory<byte> accountCode = codeInfoRepository.GetCachedCodeInfo(_state, address, false, spec, out delegation).MachineCode;
        UInt256 result = (UInt256)accountCode.Span.Length;
        stack.PushUInt256(in result);
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCall<TTracingInstructions, TTracingRefunds>(
        EvmState vmState, IWorldState state, ref long gasAvailable, IReleaseSpec spec, ITxTracer tracer, ILogger logger,
        Instruction instruction,
        UInt256 gasLimit,
        Address codeSource,
        UInt256 callValue,
        UInt256 dataOffset,
        UInt256 dataLength,
        UInt256 outputOffset,
        UInt256 outputLength,
        out UInt256? toPushInStack,
        ref ReadOnlyMemory<byte> returnBuffer,
        out object returnData)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
    {
        returnData = null;
        toPushInStack = null;

        ref readonly ExecutionEnvironment env = ref vmState.Env;

        Metrics.IncrementCalls();

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, codeSource, true, state, spec, tracer)) return EvmExceptionType.OutOfGas;

        UInt256 transferValue = instruction == Instruction.DELEGATECALL ? UInt256.Zero : callValue;

        if (vmState.IsStatic && !transferValue.IsZero && instruction != Instruction.CALLCODE) return EvmExceptionType.StaticCallViolation;

        Address caller = instruction == Instruction.DELEGATECALL ? env.Caller : env.ExecutingAccount;

        Address target = instruction == Instruction.CALL || instruction == Instruction.STATICCALL
            ? codeSource
            : env.ExecutingAccount;

        if (typeof(TLogger) == typeof(IsTracing))
        {
            TraceCallDetails(codeSource, ref callValue, ref transferValue, caller, target);
        }

        long gasExtra = 0L;

        if (!transferValue.IsZero)
        {
            gasExtra += GasCostOf.CallValue;
        }

        if (!spec.ClearEmptyAccountWhenTouched && !state.AccountExists(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }
        else if (spec.ClearEmptyAccountWhenTouched && transferValue != 0 && state.IsDeadAccount(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }

        if (!UpdateGas(spec.GetCallCost(), ref gasAvailable) ||
            !UpdateMemoryCost(vmState, ref gasAvailable, in dataOffset, dataLength) ||
            !UpdateMemoryCost(vmState, ref gasAvailable, in outputOffset, outputLength) ||
            !UpdateGas(gasExtra, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        CodeInfo codeInfo = vmState.Env.TxExecutionContext.CodeInfoRepository.GetCachedCodeInfo(state, codeSource, spec, out _);
        codeInfo.AnalyseInBackgroundIfRequired();

        if (spec.Use63Over64Rule)
        {
            gasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), gasLimit);
        }

        if (gasLimit >= long.MaxValue) return EvmExceptionType.OutOfGas;

        long gasLimitUl = gasLimit.ToLong();
        if (!UpdateGas(gasLimitUl, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (!transferValue.IsZero)
        {
            if (typeof(TTracingRefunds) == typeof(IsTracing)) tracer.ReportExtraGasPressure(GasCostOf.CallStipend);
            gasLimitUl += GasCostOf.CallStipend;
        }

        if (env.CallDepth >= MaxCallDepth || (!transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue))
        {
            returnBuffer = Array.Empty<byte>();
            toPushInStack = UInt256.Zero;

            if (typeof(TTracingInstructions) == typeof(IsTracing))
            {
                // very specific for Parity trace, need to find generalization - very peculiar 32 length...
                ReadOnlyMemory<byte>? memoryTrace = vmState.Memory.Inspect(in dataOffset, 32);
                tracer.ReportMemoryChange(dataOffset, memoryTrace is null ? ReadOnlySpan<byte>.Empty : memoryTrace.Value.Span);
            }

            if (typeof(TLogger) == typeof(IsTracing)) logger.Trace("FAIL - call depth");
            if (typeof(TTracingInstructions) == typeof(IsTracing)) tracer.ReportOperationRemainingGas(gasAvailable);
            if (typeof(TTracingInstructions) == typeof(IsTracing)) tracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);

            UpdateGasUp(gasLimitUl, ref gasAvailable);
            if (typeof(TTracingInstructions) == typeof(IsTracing)) tracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
            return EvmExceptionType.None;
        }

        Snapshot snapshot = state.TakeSnapshot();
        state.SubtractFromBalance(caller, transferValue, spec);

        if (codeInfo.IsEmpty && typeof(TTracingInstructions) != typeof(IsTracing) && !tracer.IsTracingActions)
        {
            // Non contract call, no need to construct call frame can just credit balance and return gas
            returnBuffer = default;
            toPushInStack = StatusCode.Success;

            UpdateGasUp(gasLimitUl, ref gasAvailable);
            return FastCall(spec, out returnData, in transferValue, target);
        }

        ReadOnlyMemory<byte> callData = vmState.Memory.Load(in dataOffset, dataLength);
        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: caller,
            codeSource: codeSource,
            executingAccount: target,
            transferValue: transferValue,
            value: callValue,
            inputData: callData,
            codeInfo: codeInfo
        );
        if (typeof(TLogger) == typeof(IsTracing)) logger.Trace($"Tx call gas {gasLimitUl}");
        if (outputLength == 0)
        {
            // TODO: when output length is 0 outputOffset can have any value really
            // and the value does not matter and it can cause trouble when beyond long range
            outputOffset = 0;
        }

        ExecutionType executionType = GetCallExecutionType(instruction, env.IsPostMerge());
        returnData = EvmState.RentFrame(
            gasLimitUl,
            outputOffset.ToLong(),
            outputLength.ToLong(),
            executionType,
            instruction == Instruction.STATICCALL || vmState.IsStatic,
            isCreateOnPreExistingAccount: false,
            snapshot: snapshot,
            env: callEnv,
            stateForAccessLists: vmState.AccessTracker);

        return EvmExceptionType.None;

        EvmExceptionType FastCall(IReleaseSpec spec, out object returnData, in UInt256 transferValue, Address target)
        {
            state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);
            Metrics.IncrementEmptyCalls();

            returnData = CallResult.BoxedEmpty;
            return EvmExceptionType.None;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCallDetails(Address codeSource, ref UInt256 callValue, ref UInt256 transferValue, Address caller, Address target)
        {
            logger.Trace($"caller {caller}");
            logger.Trace($"code source {codeSource}");
            logger.Trace($"target {target}");
            logger.Trace($"value {callValue}");
            logger.Trace($"transfer value {transferValue}");
        }
    }

    [SkipLocalsInit]
    private static EvmExceptionType InstructionRevert<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable, out object returnData)
        where TTracing : struct, IIsTracing
    {
        SkipInit(out returnData);

        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            return EvmExceptionType.StackUnderflow;

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, in length))
        {
            return EvmExceptionType.OutOfGas;
        }

        returnData = vmState.Memory.Load(in position, in length).ToArray();
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private static EvmExceptionType InstructionReturn<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable, out object returnData)
        where TTracing : struct, IIsTracing
    {
        SkipInit(out returnData);

        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
            return EvmExceptionType.StackUnderflow;

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, in length))
        {
            return EvmExceptionType.OutOfGas;
        }

        returnData = vmState.Memory.Load(in position, in length).ToArray();

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionSelfDestruct(EvmState vmState, IWorldState state, Address inheritor, ref long gasAvailable, IReleaseSpec spec, ITxTracer txTracer)
    {
        Metrics.IncrementSelfDestructs();

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, inheritor, false, state, spec, txTracer, false)) return EvmExceptionType.OutOfGas;

        Address executingAccount = vmState.Env.ExecutingAccount;
        bool createInSameTx = vmState.AccessTracker.CreateList.Contains(executingAccount);
        if (!spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
            vmState.AccessTracker.ToBeDestroyed(executingAccount);

        UInt256 result = state.GetBalance(executingAccount);
        if (txTracer.IsTracingActions) txTracer.ReportSelfDestruct(executingAccount, result, inheritor);
        if (spec.ClearEmptyAccountWhenTouched && !result.IsZero && state.IsDeadAccount(inheritor))
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        bool inheritorAccountExists = state.AccountExists(inheritor);
        if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        if (!inheritorAccountExists)
        {
            state.CreateAccount(inheritor, result);
        }
        else if (!inheritor.Equals(executingAccount))
        {
            state.AddToBalance(inheritor, result, spec);
        }

        if (spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
            return EvmExceptionType.None; // don't burn eth when contract is not destroyed per EIP clarification

        state.SubtractFromBalance(executingAccount, result, spec);
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    public static EvmExceptionType InstructionCreate<TTracing>(
        EvmState vmState, IWorldState state, ref long gasAvailable, IReleaseSpec spec, ITxTracer tracer, ILogger logger, Instruction instruction,
        UInt256 value, UInt256 memoryPositionOfInitCode, UInt256 initCodeLength, Span<byte> salt,
        out UInt256? statusReturn,
        ref ReadOnlyMemory<byte> returnDataBuffer,
        out object callState)
        where TTracing : struct, IIsTracing
    {
        ref readonly ExecutionEnvironment env = ref vmState.Env;
        callState = null;
        statusReturn = null;

        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
        if (!state.AccountExists(env.ExecutingAccount))
        {
            state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
        }

        //EIP-3860
        if (spec.IsEip3860Enabled)
        {
            if (initCodeLength > spec.MaxInitCodeSize) return EvmExceptionType.OutOfGas;
        }

        bool outOfGas = false;
        long gasCost = (spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmPooledMemory.Div32Ceiling(initCodeLength, out outOfGas) : 0) +
                       (instruction == Instruction.CREATE2
                           ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(initCodeLength, out outOfGas)
                           : 0);
        if (outOfGas || !UpdateGas(gasCost, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in memoryPositionOfInitCode, initCodeLength)) return EvmExceptionType.OutOfGas;

        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
        {
            // TODO: need a test for this
            returnDataBuffer = Array.Empty<byte>();
            statusReturn = UInt256.Zero;
            return EvmExceptionType.None;
        }

        ReadOnlyMemory<byte> initCode = vmState.Memory.Load(in memoryPositionOfInitCode, initCodeLength);

        UInt256 balance = state.GetBalance(env.ExecutingAccount);
        if (value > balance)
        {
            returnDataBuffer = Array.Empty<byte>();
            statusReturn = UInt256.Zero;
            return EvmExceptionType.None;
        }

        UInt256 accountNonce = state.GetNonce(env.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            returnDataBuffer = Array.Empty<byte>();
            statusReturn = UInt256.Zero;
            return EvmExceptionType.None;
        }

        if (typeof(TTracing) == typeof(IsTracing)) EndInstructionTrace(tracer, gasAvailable, vmState.Memory.Size);
        // todo: === below is a new call - refactor / move

        long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!UpdateGas(callGas, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        Address contractAddress = instruction == Instruction.CREATE
            ? ContractAddress.From(env.ExecutingAccount, state.GetNonce(env.ExecutingAccount))
            : ContractAddress.From(env.ExecutingAccount, salt, initCode.Span);

        if (spec.UseHotAndColdStorage)
        {
            // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
            vmState.AccessTracker.WarmUp(contractAddress);
        }

        state.IncrementNonce(env.ExecutingAccount);

        Snapshot snapshot = state.TakeSnapshot();

        bool accountExists = state.AccountExists(contractAddress);

        if (accountExists && contractAddress.IsNonZeroAccount(spec, env.TxExecutionContext.CodeInfoRepository, state))
        {
            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
            if (typeof(TLogger) == typeof(IsTracing)) logger.Trace($"Contract collision at {contractAddress}");
            returnDataBuffer = Array.Empty<byte>();
            statusReturn = UInt256.Zero;
            return EvmExceptionType.None;
        }

        if (state.IsDeadAccount(contractAddress))
        {
            state.ClearStorage(contractAddress);
        }

        state.SubtractFromBalance(env.ExecutingAccount, value, spec);

        // Do not add the initCode to the cache as it is
        // pointing to data in this tx and will become invalid
        // for another tx as returned to pool.
        CodeInfo codeInfo = new(initCode);
        codeInfo.AnalyseInBackgroundIfRequired();

        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: env.ExecutingAccount,
            executingAccount: contractAddress,
            codeSource: null,
            codeInfo: codeInfo,
            inputData: default,
            transferValue: value,
            value: value
        );

        callState = EvmState.RentFrame(
            callGas,
            0L,
            0L,
            instruction == Instruction.CREATE2 ? ExecutionType.CREATE2 : ExecutionType.CREATE,
            vmState.IsStatic,
            accountExists,
            snapshot,
            callEnv,
            vmState.AccessTracker);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private EvmExceptionType InstructionLog<TTracing>(EvmState vmState, ref EvmStack<TTracing> stack, ref long gasAvailable, Instruction instruction)
        where TTracing : struct, IIsTracing
    {
        if (!stack.PopUInt256(out UInt256 position)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 length)) return EvmExceptionType.StackUnderflow;
        long topicsCount = instruction - Instruction.LOG0;
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in position, length)) return EvmExceptionType.OutOfGas;
        if (!UpdateGas(
                GasCostOf.Log + topicsCount * GasCostOf.LogTopic +
                length.ToLong() * GasCostOf.LogData, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        ReadOnlyMemory<byte> data = vmState.Memory.Load(in position, length);
        Hash256[] topics = new Hash256[topicsCount];
        for (int i = 0; i < topics.Length; i++)
        {
            topics[i] = new Hash256(stack.PopWord256());
        }

        LogEntry logEntry = new(
            vmState.Env.ExecutingAccount,
            data.ToArray(),
            topics);
        vmState.AccessTracker.Logs.Add(logEntry);

        if (_txTracer.IsTracingLogs)
        {
            _txTracer.ReportLog(logEntry);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private EvmExceptionType InstructionSLoad<TTracingInstructions, TTracingStorage>(EvmState vmState, ref EvmStack<TTracingInstructions> stack, ref long gasAvailable, IReleaseSpec spec)
        where TTracingInstructions : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        Metrics.IncrementSLoadOpcode();
        gasAvailable -= spec.GetSLoadCost();

        if (!stack.PopUInt256(out UInt256 result)) return EvmExceptionType.StackUnderflow;
        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);
        if (!ChargeStorageAccessGas(
            ref gasAvailable,
            vmState,
            in storageCell,
            StorageAccessType.SLOAD,
            spec,
            _txTracer)) return EvmExceptionType.OutOfGas;

        ReadOnlySpan<byte> value = _state.Get(in storageCell);
        stack.PushBytes(value);
        if (typeof(TTracingStorage) == typeof(IsTracing))
        {
            _txTracer.LoadOperationStorage(storageCell.Address, result, value);
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    static public EvmExceptionType InstructionSStore<TTracingInstructions, TTracingRefunds, TTracingStorage>(EvmState vmState, IWorldState state, ref long gasAvailable, ref UInt256 result, ref ReadOnlySpan<byte> bytes, IReleaseSpec spec, ITxTracer txTracer)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {
        Metrics.IncrementSStoreOpcode();

        // fail fast before the first storage read if gas is not enough even for reset
        if (!spec.UseNetGasMetering && !UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

        if (spec.UseNetGasMeteringWithAStipendFix)
        {
            if (typeof(TTracingRefunds) == typeof(IsTracing))
                txTracer.ReportExtraGasPressure(GasCostOf.CallStipend - spec.GetNetMeteredSStoreCost() + 1);
            if (gasAvailable <= GasCostOf.CallStipend) return EvmExceptionType.OutOfGas;
        }

        bool newIsZero = bytes.IsZero();
        bytes = !newIsZero ? bytes.WithoutLeadingZeros() : BytesZero;

        StorageCell storageCell = new(vmState.Env.ExecutingAccount, result);

        if (!ChargeStorageAccessGas(
                ref gasAvailable,
                vmState,
                in storageCell,
                StorageAccessType.SSTORE,
                spec,
                txTracer)) return EvmExceptionType.OutOfGas;

        ReadOnlySpan<byte> currentValue = state.Get(in storageCell);
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
                    if (typeof(TTracingRefunds) == typeof(IsTracing)) txTracer.ReportRefund(sClearRefunds);
                }
            }
            else if (currentIsZero)
            {
                if (!UpdateGas(GasCostOf.SSet - GasCostOf.SReset, ref gasAvailable)) return EvmExceptionType.OutOfGas;
            }
        }
        else // net metered
        {
            if (newSameAsCurrent)
            {
                if (!UpdateGas(spec.GetNetMeteredSStoreCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;
            }
            else // net metered, C != N
            {
                Span<byte> originalValue = state.GetOriginal(in storageCell);
                bool originalIsZero = originalValue.IsZero();

                bool currentSameAsOriginal = Bytes.AreEqual(originalValue, currentValue);
                if (currentSameAsOriginal)
                {
                    if (currentIsZero)
                    {
                        if (!UpdateGas(GasCostOf.SSet, ref gasAvailable)) return EvmExceptionType.OutOfGas;
                    }
                    else // net metered, current == original != new, !currentIsZero
                    {
                        if (!UpdateGas(spec.GetSStoreResetCost(), ref gasAvailable)) return EvmExceptionType.OutOfGas;

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (typeof(TTracingRefunds) == typeof(IsTracing)) txTracer.ReportRefund(sClearRefunds);
                        }
                    }
                }
                else // net metered, new != current != original
                {
                    long netMeteredStoreCost = spec.GetNetMeteredSStoreCost();
                    if (!UpdateGas(netMeteredStoreCost, ref gasAvailable)) return EvmExceptionType.OutOfGas;

                    if (!originalIsZero) // net metered, new != current != original != 0
                    {
                        if (currentIsZero)
                        {
                            vmState.Refund -= sClearRefunds;
                            if (typeof(TTracingRefunds) == typeof(IsTracing)) txTracer.ReportRefund(-sClearRefunds);
                        }

                        if (newIsZero)
                        {
                            vmState.Refund += sClearRefunds;
                            if (typeof(TTracingRefunds) == typeof(IsTracing)) txTracer.ReportRefund(sClearRefunds);
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
                        if (typeof(TTracingRefunds) == typeof(IsTracing)) txTracer.ReportRefund(refundFromReversal);
                    }
                }
            }
        }

        if (!newSameAsCurrent)
        {
            state.Set(in storageCell, newIsZero ? BytesZero : bytes.ToArray());

            if (typeof(TTracingInstructions) == typeof(IsTracing))
            {
                ReadOnlySpan<byte> valueToStore = newIsZero ? BytesZero.AsSpan() : bytes;
                byte[] storageBytes = new byte[32]; // do not stackalloc here
                storageCell.Index.ToBigEndian(storageBytes);
                txTracer.ReportStorageChange(storageBytes, valueToStore);
            }

            if (typeof(TTracingStorage) == typeof(IsTracing))
            {
                txTracer.SetOperationStorage(storageCell.Address, result, bytes, currentValue);
            }
        }

        return EvmExceptionType.None;
    }

    private CallResult GetFailureReturn<TTracingInstructions>(long gasAvailable, EvmExceptionType exceptionType)
        where TTracingInstructions : struct, IIsTracing
    {
        if (typeof(TTracingInstructions) == typeof(IsTracing)) EndInstructionTraceError(_txTracer, gasAvailable, exceptionType);

        return exceptionType switch
        {
            EvmExceptionType.OutOfGas => CallResult.OutOfGasException,
            EvmExceptionType.BadInstruction => CallResult.InvalidInstructionException,
            EvmExceptionType.StaticCallViolation => CallResult.StaticCallViolationException,
            EvmExceptionType.InvalidSubroutineEntry => CallResult.InvalidSubroutineEntry,
            EvmExceptionType.InvalidSubroutineReturn => CallResult.InvalidSubroutineReturn,
            EvmExceptionType.StackOverflow => CallResult.StackOverflowException,
            EvmExceptionType.StackUnderflow => CallResult.StackUnderflowException,
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

    public static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (outOfGas) return false;
        return memoryCost == 0L || UpdateGas(memoryCost, ref gasAvailable);
    }

    private static bool Jump(in UInt256 jumpDest, ref int programCounter, in ExecutionEnvironment env)
    {
        if (jumpDest > int.MaxValue)
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
            return false;
        }

        int jumpDestInt = (int)jumpDest;
        if (!env.CodeInfo.ValidateJump(jumpDestInt))
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
            return false;
        }

        programCounter = jumpDestInt;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void StartInstructionTrace(ITxTracer tracer, Instruction instruction, EvmState vmState, long gasAvailable, int programCounter, int stackHead)
    {
        tracer.StartOperation(programCounter, instruction, gasAvailable, vmState.Env);
        if (tracer.IsTracingMemory)
        {
            tracer.SetOperationMemory(vmState.Memory.GetTrace());
            tracer.SetOperationMemorySize(vmState.Memory.Size);
        }

        if (tracer.IsTracingStack)
        {
            Memory<byte> stackMemory = vmState.DataStack.AsMemory().Slice(0, stackHead * EvmStack<VirtualMachine.IsTracing>.WordSize);
            tracer.SetOperationStack(new TraceStack(stackMemory));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EndInstructionTrace(ITxTracer tracer, long gasAvailable, ulong memorySize)
    {
        tracer.ReportOperationRemainingGas(gasAvailable);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void EndInstructionTraceError(ITxTracer tracer, long gasAvailable, EvmExceptionType evmExceptionType)
    {
        tracer.ReportOperationRemainingGas(gasAvailable);
        tracer.ReportOperationError(evmExceptionType);
    }

    private static ExecutionType GetCallExecutionType(Instruction instruction, bool isPostMerge = false) =>
        instruction switch
        {
            Instruction.CALL => ExecutionType.CALL,
            Instruction.DELEGATECALL => ExecutionType.DELEGATECALL,
            Instruction.STATICCALL => ExecutionType.STATICCALL,
            Instruction.CALLCODE => ExecutionType.CALLCODE,
            _ => throw new NotSupportedException($"Execution type is undefined for {instruction.GetName(isPostMerge)}")
        };
}
