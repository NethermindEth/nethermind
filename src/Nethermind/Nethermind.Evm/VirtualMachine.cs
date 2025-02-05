
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.EvmObjectFormat.Handlers;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

using static Nethermind.Evm.EvmObjectFormat.EofValidator;
using static Nethermind.Evm.VirtualMachine;

#if DEBUG
using Nethermind.Evm.Tracing.Debugger;
#endif

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]
namespace Nethermind.Evm;

using unsafe OpCode = delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType>;
using Int256;

public sealed unsafe class VirtualMachine : IVirtualMachine
{
    public const int MaxCallDepth = Eof1.RETURN_STACK_MAX_HEIGHT;
    private readonly static UInt256 P255Int = (UInt256)System.Numerics.BigInteger.Pow(2, 255);
    internal readonly static byte[] EofHash256 = KeccakHash.ComputeHashBytes(EofValidator.MAGIC);
    internal static ref readonly UInt256 P255 => ref P255Int;
    internal static readonly UInt256 BigInt256 = 256;
    internal static readonly UInt256 BigInt32 = 32;

    internal static readonly byte[] BytesZero = [0];

    internal static readonly byte[] BytesZero32 =
    {
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0
    };

    internal static readonly byte[] BytesMax32 =
    {
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255
    };

    internal static readonly PrecompileExecutionFailureException PrecompileExecutionFailureException = new();
    internal static readonly OutOfGasException PrecompileOutOfGasException = new();

    private readonly byte[] _chainId;

    private readonly IBlockhashProvider _blockHashProvider;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private readonly Stack<EvmState> _stateStack = new();

    private IWorldState _worldState = null!;
    private (Address Address, bool ShouldDelete) _parityTouchBugAccount = (Address.FromNumber(3), false);
    private ReadOnlyMemory<byte> _returnDataBuffer = Array.Empty<byte>();
    private ITxTracer _txTracer = NullTxTracer.Instance;
    private IReleaseSpec _spec;

    private ICodeInfoRepository _codeInfoRepository;
    public ICodeInfoRepository CodeInfoRepository => _codeInfoRepository;
    public IReleaseSpec Spec => _spec;
    public ITxTracer TxTracer => _txTracer;
    public IWorldState WorldState => _worldState;
    public ReadOnlySpan<byte> ChainId => _chainId;
    public ReadOnlyMemory<byte> ReturnDataBuffer { get => _returnDataBuffer; set => _returnDataBuffer = value; }
    public object ReturnData { get => _returnData; set => _returnData = value; }
    private object _returnData;
    public IBlockhashProvider BlockHashProvider => _blockHashProvider;

    public EvmState EvmState => _vmState;
    private EvmState _vmState;
    public int SectionIndex { get => _sectionIndex; set => _sectionIndex = value; }
    private int _sectionIndex;

    private OpCode[] _opcodeMethods;
    private static long _txCount;

    private EvmState _currentState;
    private ReadOnlyMemory<byte>? _previousCallResult;
    private UInt256 _previousCallOutputDestination;

    public VirtualMachine(
        IBlockhashProvider? blockHashProvider,
        ISpecProvider? specProvider,
        ICodeInfoRepository codeInfoRepository,
        ILogManager? logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _blockHashProvider = blockHashProvider ?? throw new ArgumentNullException(nameof(blockHashProvider));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _codeInfoRepository = codeInfoRepository ?? throw new ArgumentNullException(nameof(codeInfoRepository));
        _chainId = ((UInt256)specProvider.ChainId).ToBigEndian();
    }

    public TransactionSubstate Run<TTracingInstructions>(EvmState evmState, IWorldState worldState, ITxTracer txTracer)
        where TTracingInstructions : struct, IFlag
    {
        _txTracer = txTracer;
        _worldState = worldState;

        ref readonly TxExecutionContext txExecutionContext = ref evmState.Env.TxExecutionContext;
        IReleaseSpec spec = PrepareSpecAndOpcodes<TTracingInstructions>(txExecutionContext.BlockExecutionContext.Header);

        _codeInfoRepository = txExecutionContext.CodeInfoRepository;
        _currentState = evmState;
        _previousCallResult = null;
        _previousCallOutputDestination = UInt256.Zero;
        ZeroPaddedSpan previousCallOutput = ZeroPaddedSpan.Empty;

        while (true)
        {
            if (!_currentState.IsContinuation)
            {
                _returnDataBuffer = Array.Empty<byte>();
            }

            Exception? failure;
            try
            {
                CallResult callResult;
                if (_currentState.IsPrecompile)
                {
                    callResult = ExecutePrecompile(_currentState, _txTracer.IsTracingActions, out failure);
                    if (failure is not null)
                    {
                        goto Failure;
                    }
                }
                else
                {
                    if (_txTracer.IsTracingActions && !_currentState.IsContinuation)
                    {
                        TraceTransactionActionStart(_currentState);
                    }

                    if (_currentState.Env.CodeInfo is not null)
                    {
                        callResult = ExecuteCall<TTracingInstructions>(_currentState, _previousCallResult, previousCallOutput, _previousCallOutputDestination);
                    }
                    else
                    {
                        callResult = CallResult.InvalidCodeException;
                    }

                    if (!callResult.IsReturn)
                    {
                        PrepareNextCallFrame(in callResult, ref previousCallOutput);
                        continue;
                    }

                    if (callResult.IsException)
                    {
                        TransactionSubstate? substate = HandleException(in callResult, ref previousCallOutput);
                        if (substate is not null)
                        {
                            return substate;
                        }
                        continue;
                    }
                }

                if (_currentState.IsTopLevel)
                {
                    if (_txTracer.IsTracingActions)
                    {
                        TraceTransactionActionEnd(_currentState, spec, callResult);
                    }

                    return PrepareTopLevelSubstate(in callResult);
                }

                using (EvmState previousState = _currentState)
                {
                    _currentState = _stateStack.Pop();
                    _currentState.IsContinuation = true;
                    _currentState.GasAvailable += previousState.GasAvailable;
                    bool previousStateSucceeded = true;

                    if (!callResult.ShouldRevert)
                    {
                        long gasAvailableForCodeDeposit = previousState.GasAvailable;
                        if (previousState.ExecutionType.IsAnyCreate())
                        {
                            PrepareCreateData(previousState, ref previousCallOutput);
                            if (previousState.ExecutionType.IsAnyCreateLegacy())
                            {
                                HandleLegacyCreate(in callResult, previousState, gasAvailableForCodeDeposit, spec, ref previousStateSucceeded);
                            }
                            else if (previousState.ExecutionType.IsAnyCreateEof())
                            {
                                HandleEofCreate(in callResult, previousState, gasAvailableForCodeDeposit, spec, ref previousStateSucceeded);
                            }
                        }
                        else
                        {
                            previousCallOutput = HandleRegularReturn<TTracingInstructions>(in callResult, previousState);
                        }

                        if (previousStateSucceeded)
                        {
                            previousState.CommitToParent(_currentState);
                        }
                    }
                    else
                    {
                        HandleRevert(previousState, callResult, ref previousCallOutput);
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
            TransactionSubstate? failSubstate = HandleFailure<TTracingInstructions>(failure, ref previousCallOutput);
            if (failSubstate is not null)
            {
                return failSubstate;
            }
        }
    }

    private void PrepareCreateData(EvmState previousState, ref ZeroPaddedSpan previousCallOutput)
    {
        _previousCallResult = previousState.Env.ExecutingAccount.Bytes;
        _previousCallOutputDestination = UInt256.Zero;
        _returnDataBuffer = Array.Empty<byte>();
        previousCallOutput = ZeroPaddedSpan.Empty;
    }

    private ZeroPaddedSpan HandleRegularReturn<TTracingInstructions>(scoped in CallResult callResult, EvmState previousState)
        where TTracingInstructions : struct, IFlag
    {
        ZeroPaddedSpan previousCallOutput;
        _returnDataBuffer = callResult.Output.Bytes;
        _previousCallResult = previousState.ExecutionType.IsAnyCallEof() ? EofStatusCode.SuccessBytes :
            callResult.PrecompileSuccess.HasValue
            ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes)
            : StatusCode.SuccessBytes;
        previousCallOutput = callResult.Output.Bytes.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Bytes.Length, (int)previousState.OutputLength));
        _previousCallOutputDestination = (ulong)previousState.OutputDestination;
        if (previousState.IsPrecompile)
        {
            // parity induced if else for vmtrace
            if (TTracingInstructions.IsActive)
            {
                _txTracer.ReportMemoryChange(_previousCallOutputDestination, previousCallOutput);
            }
        }

        if (_txTracer.IsTracingActions)
        {
            _txTracer.ReportActionEnd(previousState.GasAvailable, _returnDataBuffer);
        }

        return previousCallOutput;
    }

    private void HandleEofCreate(in CallResult callResult, EvmState previousState, long gasAvailableForCodeDeposit, IReleaseSpec spec, ref bool previousStateSucceeded)
    {
        Address callCodeOwner = previousState.Env.ExecutingAccount;
        // ReturnContract was called with a container index and auxdata
        // 1 - load deploy EOF subcontainer at deploy_container_index in the container from which RETURNCONTRACT is executed
        ReadOnlySpan<byte> auxExtraData = callResult.Output.Bytes.Span;
        EofCodeInfo deployCodeInfo = (EofCodeInfo)callResult.Output.Container;
        byte[] bytecodeResultArray = null;

        // 2 - concatenate data section with (aux_data_offset, aux_data_offset + aux_data_size) memory segment and update data size in the header
        Span<byte> bytecodeResult = new byte[deployCodeInfo.MachineCode.Length + auxExtraData.Length];
        // 2 - 1 - 1 - copy old container
        deployCodeInfo.MachineCode.Span.CopyTo(bytecodeResult);
        // 2 - 1 - 2 - copy aux data to dataSection
        auxExtraData.CopyTo(bytecodeResult[deployCodeInfo.MachineCode.Length..]);

        // 2 - 2 - update data section size in the header u16
        int dataSubheaderSectionStart =
            EofValidator.VERSION_OFFSET // magic + version
            + Eof1.MINIMUM_HEADER_SECTION_SIZE // type section : (1 byte of separator + 2 bytes for size)
            + ONE_BYTE_LENGTH + TWO_BYTE_LENGTH + TWO_BYTE_LENGTH * deployCodeInfo.EofContainer.Header.CodeSections.Count // code section :  (1 byte of separator + (CodeSections count) * 2 bytes for size)
            + (deployCodeInfo.EofContainer.Header.ContainerSections is null
                ? 0 // container section :  (0 bytes if no container section is available)
                : ONE_BYTE_LENGTH + TWO_BYTE_LENGTH + TWO_BYTE_LENGTH * deployCodeInfo.EofContainer.Header.ContainerSections.Value.Count) // container section :  (1 byte of separator + (ContainerSections count) * 2 bytes for size)
            + ONE_BYTE_LENGTH; // data section seperator

        ushort dataSize = (ushort)(deployCodeInfo.DataSection.Length + auxExtraData.Length);
        bytecodeResult[dataSubheaderSectionStart + 1] = (byte)(dataSize >> 8);
        bytecodeResult[dataSubheaderSectionStart + 2] = (byte)(dataSize & 0xFF);

        bytecodeResultArray = bytecodeResult.ToArray();

        // 3 - if updated deploy container size exceeds MAX_CODE_SIZE instruction exceptionally aborts
        bool invalidCode = bytecodeResultArray.Length > spec.MaxCodeSize;
        long codeDepositGasCost = CodeDepositHandler.CalculateCost(spec, bytecodeResultArray?.Length ?? 0);
        if (gasAvailableForCodeDeposit >= codeDepositGasCost && !invalidCode)
        {
            // 4 - set state[new_address].code to the updated deploy container
            // push new_address onto the stack (already done before the ifs)
            _codeInfoRepository.InsertCode(_worldState, bytecodeResultArray, callCodeOwner, spec);
            _currentState.GasAvailable -= codeDepositGasCost;

            if (_txTracer.IsTracingActions)
            {
                _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, bytecodeResultArray);
            }
        }
        else if (spec.FailOnOutOfGasCodeDeposit || invalidCode)
        {
            _currentState.GasAvailable -= gasAvailableForCodeDeposit;
            _worldState.Restore(previousState.Snapshot);
            if (!previousState.IsCreateOnPreExistingAccount)
            {
                _worldState.DeleteAccount(callCodeOwner);
            }

            _previousCallResult = BytesZero;
            previousStateSucceeded = false;

            if (_txTracer.IsTracingActions)
            {
                _txTracer.ReportActionError(invalidCode ? EvmExceptionType.InvalidCode : EvmExceptionType.OutOfGas);
            }
        }
        else if (_txTracer.IsTracingActions)
        {
            _txTracer.ReportActionEnd(0L, callCodeOwner, bytecodeResultArray);
        }
    }

    private void HandleLegacyCreate(in CallResult callResult, EvmState previousState, long gasAvailableForCodeDeposit, IReleaseSpec spec, ref bool previousStateSucceeded)
    {
        Address callCodeOwner = previousState.Env.ExecutingAccount;
        long codeDepositGasCost = CodeDepositHandler.CalculateCost(spec, callResult.Output.Bytes.Length);
        bool invalidCode = !CodeDepositHandler.IsValidWithLegacyRules(spec, callResult.Output.Bytes);
        if (gasAvailableForCodeDeposit >= codeDepositGasCost && !invalidCode)
        {
            ReadOnlyMemory<byte> code = callResult.Output.Bytes;
            _codeInfoRepository.InsertCode(_worldState, code, callCodeOwner, spec);
            _currentState.GasAvailable -= codeDepositGasCost;

            if (_txTracer.IsTracingActions)
            {
                _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output.Bytes);
            }
        }
        else if (spec.FailOnOutOfGasCodeDeposit || invalidCode)
        {
            _currentState.GasAvailable -= gasAvailableForCodeDeposit;
            _worldState.Restore(previousState.Snapshot);
            if (!previousState.IsCreateOnPreExistingAccount)
            {
                _worldState.DeleteAccount(callCodeOwner);
            }

            _previousCallResult = BytesZero;
            previousStateSucceeded = false;

            if (_txTracer.IsTracingActions)
            {
                _txTracer.ReportActionError(invalidCode ? EvmExceptionType.InvalidCode : EvmExceptionType.OutOfGas);
            }
        }
        else if (_txTracer.IsTracingActions)
        {
            _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output.Bytes);
        }
    }

    private TransactionSubstate PrepareTopLevelSubstate(in CallResult callResult)
    {
        return new TransactionSubstate(
            callResult.Output,
            _currentState.Refund,
            _currentState.AccessTracker.DestroyList,
            (IReadOnlyCollection<LogEntry>)_currentState.AccessTracker.Logs,
            callResult.ShouldRevert,
            isTracerConnected: _txTracer.IsTracing,
            _logger);
    }

    private void HandleRevert(EvmState previousState, in CallResult callResult, ref ZeroPaddedSpan previousCallOutput)
    {
        _worldState.Restore(previousState.Snapshot);
        _returnDataBuffer = callResult.Output.Bytes;
        _previousCallResult = previousState.ExecutionType.IsAnyCallEof()
            ? (callResult.PrecompileSuccess is not null ? EofStatusCode.FailureBytes : EofStatusCode.RevertBytes)
            : StatusCode.FailureBytes;
        previousCallOutput = callResult.Output.Bytes.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Bytes.Length, (int)previousState.OutputLength));
        _previousCallOutputDestination = (ulong)previousState.OutputDestination;

        if (_txTracer.IsTracingActions)
        {
            _txTracer.ReportActionRevert(previousState.GasAvailable, callResult.Output.Bytes);
        }
    }

    private TransactionSubstate? HandleFailure<TTracingInstructions>(Exception failure, ref ZeroPaddedSpan previousCallOutput)
        where TTracingInstructions : struct, IFlag
    {
        if (_logger.IsTrace) _logger.Trace($"exception ({failure.GetType().Name}) in {_currentState.ExecutionType} at depth {_currentState.Env.CallDepth} - restoring snapshot");

        _worldState.Restore(_currentState.Snapshot);

        RevertParityTouchBugAccount();

        ITxTracer txTracer = _txTracer;
        if (TTracingInstructions.IsActive)
        {
            txTracer.ReportOperationRemainingGas(0);
            txTracer.ReportOperationError(failure is EvmException evmException ? evmException.ExceptionType : EvmExceptionType.Other);
        }

        if (txTracer.IsTracingActions)
        {
            EvmException evmException = failure as EvmException;
            txTracer.ReportActionError(evmException?.ExceptionType ?? EvmExceptionType.Other);
        }

        if (_currentState.IsTopLevel)
        {
            return new TransactionSubstate(failure is OverflowException ? EvmExceptionType.Other : (failure as EvmException).ExceptionType, txTracer.IsTracing);
        }

        _previousCallResult = _currentState.ExecutionType.IsAnyCallEof() ? EofStatusCode.FailureBytes : StatusCode.FailureBytes;
        _previousCallOutputDestination = UInt256.Zero;
        _returnDataBuffer = Array.Empty<byte>();
        previousCallOutput = ZeroPaddedSpan.Empty;

        _currentState.Dispose();
        _currentState = _stateStack.Pop();
        _currentState.IsContinuation = true;
        return null;
    }

    private void PrepareNextCallFrame(in CallResult callResult, ref ZeroPaddedSpan previousCallOutput)
    {
        _stateStack.Push(_currentState);
        _currentState = callResult.StateToExecute;
        _previousCallResult = null;
        _returnDataBuffer = Array.Empty<byte>();
        previousCallOutput = ZeroPaddedSpan.Empty;
    }

    private TransactionSubstate? HandleException(in CallResult callResult, ref ZeroPaddedSpan previousCallOutput)
    {
        if (_txTracer.IsTracingActions) _txTracer.ReportActionError(callResult.ExceptionType);
        _worldState.Restore(_currentState.Snapshot);

        RevertParityTouchBugAccount();

        if (_currentState.IsTopLevel)
        {
            return new TransactionSubstate(callResult.ExceptionType, _txTracer.IsTracing);
        }

        _previousCallResult = _currentState.ExecutionType.IsAnyCallEof() ? EofStatusCode.FailureBytes : StatusCode.FailureBytes;
        _previousCallOutputDestination = UInt256.Zero;
        _returnDataBuffer = Array.Empty<byte>();
        previousCallOutput = ZeroPaddedSpan.Empty;

        _currentState.Dispose();
        _currentState = _stateStack.Pop();
        _currentState.IsContinuation = true;
        return null;
    }

    private CallResult ExecutePrecompile(EvmState currentState, bool isTracingActions, out Exception? failure)
    {
        CallResult callResult;
        if (isTracingActions)
        {
            _txTracer.ReportAction(currentState.GasAvailable, currentState.Env.Value, currentState.From, currentState.To, currentState.Env.InputData, currentState.ExecutionType, true);
        }

        callResult = ExecutePrecompile(currentState);

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

        failure = null;
        return callResult;
    Failure:
        return default;
    }

    private void TraceTransactionActionStart(EvmState currentState)
    {
        _txTracer.ReportAction(currentState.GasAvailable,
            currentState.Env.Value,
            currentState.From,
            currentState.To,
            currentState.ExecutionType.IsAnyCreate()
                ? currentState.Env.CodeInfo.MachineCode
                : currentState.Env.InputData,
            currentState.ExecutionType);

        if (_txTracer.IsTracingCode) _txTracer.ReportByteCode(currentState.Env.CodeInfo?.MachineCode ?? default);
    }

    private IReleaseSpec PrepareSpecAndOpcodes<TTracingInstructions>(BlockHeader header) where TTracingInstructions : struct, IFlag
    {
        IReleaseSpec spec = _specProvider.GetSpec(header.Number, header.Timestamp);
        if (!TTracingInstructions.IsActive)
        {
            if (_txCount < 500_000 && Interlocked.Increment(ref _txCount) % 10_000 == 0)
            {
                if (_logger.IsDebug) _logger.Debug("Resetting EVM instructions cache");
                // Flush the cache every 10_000 transactions to directly point at any PGO optimized methods rather than via pre-stubs
                // May be a few cycles to pick up pointers to the optimized methods depending on what's in the blocks,
                // however the the refreshes don't take long.
                spec.EvmInstructions = EvmInstructions.GenerateOpCodes<TTracingInstructions>(spec);
            }
            _opcodeMethods = (OpCode[])(spec.EvmInstructions ??= EvmInstructions.GenerateOpCodes<TTracingInstructions>(spec));
        }
        else
        {
            _opcodeMethods = (OpCode[])(spec.EvmTracedInstructions ??= EvmInstructions.GenerateOpCodes<TTracingInstructions>(spec));
        }

        return (_spec = spec);
    }

    private void TraceTransactionActionEnd(EvmState currentState, IReleaseSpec spec, in CallResult callResult)
    {
        long codeDepositGasCost = CodeDepositHandler.CalculateCost(spec, callResult.Output.Bytes.Length);

        if (callResult.IsException)
        {
            _txTracer.ReportActionError(callResult.ExceptionType);
        }
        else if (callResult.ShouldRevert)
        {
            _txTracer.ReportActionRevert(currentState.ExecutionType.IsAnyCreate()
                    ? currentState.GasAvailable - codeDepositGasCost
                    : currentState.GasAvailable,
                callResult.Output.Bytes);
        }
        else if (currentState.ExecutionType.IsAnyCreate())
        {
            if (currentState.GasAvailable < codeDepositGasCost)
            {
                if (spec.ChargeForTopLevelCreate)
                {
                    _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                }
                else
                {
                    _txTracer.ReportActionEnd(currentState.GasAvailable, currentState.To, callResult.Output.Bytes);
                }
            }
            // Reject code starting with 0xEF if EIP-3541 is enabled.
            else if (CodeDepositHandler.CodeIsInvalid(spec, callResult.Output.Bytes, callResult.FromVersion))
            {
                _txTracer.ReportActionError(EvmExceptionType.InvalidCode);
            }
            else
            {
                _txTracer.ReportActionEnd(currentState.GasAvailable - codeDepositGasCost, currentState.To, callResult.Output.Bytes);
            }
        }
        else
        {
            _txTracer.ReportActionEnd(currentState.GasAvailable, _returnDataBuffer);
        }
    }

    private void RevertParityTouchBugAccount()
    {
        if (_parityTouchBugAccount.ShouldDelete)
        {
            if (_worldState.AccountExists(_parityTouchBugAccount.Address))
            {
                _worldState.AddToBalance(_parityTouchBugAccount.Address, UInt256.Zero, _spec);
            }

            _parityTouchBugAccount.ShouldDelete = false;
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

    private enum StorageAccessType
    {
        SLOAD,
        SSTORE
    }

    private CallResult ExecutePrecompile(EvmState state)
    {
        ReadOnlyMemory<byte> callData = state.Env.InputData;
        UInt256 transferValue = state.Env.TransferValue;
        long gasAvailable = state.GasAvailable;

        IPrecompile precompile = state.Env.CodeInfo.Precompile;
        long baseGasCost = precompile.BaseGasCost(_spec);
        long blobGasCost = precompile.DataGasCost(callData, _spec);

        bool wasCreated = _worldState.AddToBalanceAndCreateIfNotExists(state.Env.ExecutingAccount, transferValue, _spec);

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
            && _spec.ClearEmptyAccountWhenTouched)
        {
            _parityTouchBugAccount.ShouldDelete = true;
        }

        if (!UpdateGas(checked(baseGasCost + blobGasCost), ref gasAvailable))
        {
            return new(default, false, 0, true, EvmExceptionType.OutOfGas);
        }

        state.GasAvailable = gasAvailable;

        try
        {
            (ReadOnlyMemory<byte> output, bool success) = precompile.Run(callData, _spec);
            CallResult callResult = new(output, success, 0, !success);
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
            CallResult callResult = new(default, false, 0, true);
            return callResult;
        }
    }

    /// <remarks>
    /// Struct generic parameter is used to burn out all the if statements and inner code
    /// by typeof(TTracingInstructions) == typeof(NotTracing) checks that are evaluated to constant
    /// values at compile time.
    /// </remarks>
    [SkipLocalsInit]
    private CallResult ExecuteCall<TTracingInstructions>(EvmState vmState, ReadOnlyMemory<byte>? previousCallResult, ZeroPaddedSpan previousCallOutput, scoped in UInt256 previousCallOutputDestination)
        where TTracingInstructions : struct, IFlag
    {
        ref readonly ExecutionEnvironment env = ref vmState.Env;
        if (!vmState.IsContinuation)
        {
            _worldState.AddToBalanceAndCreateIfNotExists(env.ExecutingAccount, env.TransferValue, _spec);

            if (vmState.ExecutionType.IsAnyCreate() && _spec.ClearEmptyAccountWhenTouched)
            {
                _worldState.IncrementNonce(env.ExecutingAccount);
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
        EvmStack stack = new(vmState.DataStackHead, _txTracer, vmState.DataStack.AsSpan());
        long gasAvailable = vmState.GasAvailable;

        if (previousCallResult is not null)
        {
            stack.PushBytes(previousCallResult.Value.Span);
            if (TTracingInstructions.IsActive) _txTracer.ReportOperationRemainingGas(vmState.GasAvailable);
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

        _vmState = vmState;
        // Struct generic parameter is used to burn out all the if statements
        // and inner code by using static property on generic IFlag using
        // OnFlag or OffFlag. These checks that are evaluated to constant values at compile time.
        // This only works for structs, not for classes or interface types
        // which use shared generics.
        return _txTracer.IsCancelable switch
        {
            false => ExecuteCode<TTracingInstructions, OffFlag>(ref stack, gasAvailable),
            true => ExecuteCode<TTracingInstructions, OnFlag>(ref stack, gasAvailable),
        };
    Empty:
        return CallResult.Empty(vmState.Env.CodeInfo.Version);
    OutOfGas:
        return CallResult.OutOfGasException;
    }

    [SkipLocalsInit]
    private unsafe CallResult ExecuteCode<TTracingInstructions, TCancelable>(scoped ref EvmStack stack, long gasAvailable)
        where TTracingInstructions : struct, IFlag
        where TCancelable : struct, IFlag
    {
        _returnData = null;
        _sectionIndex = _vmState.FunctionIndex;

        ICodeInfo codeInfo = _vmState.Env.CodeInfo;
        ReadOnlySpan<Instruction> codeSection = GetInstructions(codeInfo);

        EvmExceptionType exceptionType = EvmExceptionType.None;
#if DEBUG
        DebugTracer? debugger = _txTracer.GetTracer<DebugTracer>();
#endif

        // Initialize program counter to the current state's value.
        // Entry point is not always 0 as we may be returning to code after a call.
        int programCounter = _vmState.ProgramCounter;
        // Use fixed pointer or we loose the type when trying skip bounds check,
        // and have to cast for each call (delegate*<...> can't be used as a generic arg)
        fixed (OpCode* opcodeMethods = &_opcodeMethods[0])
        {
            // We use a while loop rather than a for loop as some
            // opcodes can change the program counter (e.g. Push, Jump, etc)
            while ((uint)programCounter < (uint)codeSection.Length)
            {
#if DEBUG
                debugger?.TryWait(ref _vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
                // Get the opcode at the current program counter
                Instruction instruction = codeSection[programCounter];

                if (TCancelable.IsActive && _txTracer.IsCancelled) ThrowOperationCanceledException();
                if (TTracingInstructions.IsActive)
                    StartInstructionTrace(instruction, gasAvailable, programCounter, in stack);

                // Advance the program counter one instruction
                programCounter++;

                if (Instruction.POP == instruction)
                {
                    // Very commonly called opcode and minimal implementation, so we inline here
                    exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gasAvailable, ref programCounter);
                }
                else
                {
                    // Get the opcode delegate* from the opcode array
                    OpCode opcodeMethod = opcodeMethods[(int)instruction];
                    // Execute opcode delegate* via calli (see: C# function pointers https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code#function-pointers)
                    // Stack, gas, and program counter may be modified by call (also instance variables on the vm)
                    exceptionType = opcodeMethod(this, ref stack, ref gasAvailable, ref programCounter);
                }

                // Exit loop if run out of gas
                if (gasAvailable < 0) goto OutOfGas;
                // Exit loop if exception occurred
                if (exceptionType != EvmExceptionType.None) break;
                // Exit loop if returning data
                if (_returnData is not null) break;

                if (TTracingInstructions.IsActive)
                    EndInstructionTrace(gasAvailable);
            }
        }

        if (exceptionType is EvmExceptionType.None or EvmExceptionType.Stop or EvmExceptionType.Revert)
        {
            UpdateCurrentState(programCounter, gasAvailable, stack.Head);
        }
        else
        {
            goto ReturnFailure;
        }

        if (exceptionType == EvmExceptionType.Revert) goto Revert;
        if (_returnData is not null) goto DataReturn;

        return CallResult.Empty(codeInfo.Version);

    DataReturn:
#if DEBUG
        debugger?.TryWait(ref _vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
        if (_returnData is EvmState state)
        {
            return new CallResult(state);
        }
        else if (_returnData is EofCodeInfo eofCodeInfo)
        {
            return new CallResult(eofCodeInfo, _returnDataBuffer, null, codeInfo.Version);
        }
        return new CallResult(null, (byte[])_returnData, null, codeInfo.Version);
    Revert:
        return new CallResult(null, (byte[])_returnData, null, codeInfo.Version, shouldRevert: true);
    OutOfGas:
        exceptionType = EvmExceptionType.OutOfGas;
    ReturnFailure:
        return GetFailureReturn(gasAvailable, exceptionType);

        static ReadOnlySpan<Instruction> GetInstructions(ICodeInfo codeInfo)
        {
            ReadOnlySpan<byte> codeBytes = codeInfo.CodeSection.Span;
            return MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<byte, Instruction>(ref MemoryMarshal.GetReference(codeBytes)),
                codeBytes.Length);
        }

        [DoesNotReturn]
        static void ThrowOperationCanceledException() => throw new OperationCanceledException("Cancellation Requested");
    }

    private CallResult GetFailureReturn(long gasAvailable, EvmExceptionType exceptionType)
    {
        if (_txTracer.IsTracingInstructions) EndInstructionTraceError(gasAvailable, exceptionType);

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
            EvmExceptionType.AddressOutOfRange => CallResult.InvalidAddressRange,
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionType), exceptionType, "")
        };
    }

    private void UpdateCurrentState(int pc, long gas, int stackHead)
    {
        EvmState state = _vmState;

        state.ProgramCounter = pc;
        state.GasAvailable = gas;
        state.DataStackHead = stackHead;
        state.FunctionIndex = _sectionIndex;
    }

    private static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length, out bool outOfGas);
        if (outOfGas) return false;
        return memoryCost == 0L || UpdateGas(memoryCost, ref gasAvailable);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartInstructionTrace(Instruction instruction, long gasAvailable, int programCounter, in EvmStack stackValue)
    {
        EvmState vmState = _vmState;
        int sectionIndex = _sectionIndex;

        bool isEofFrame = vmState.Env.CodeInfo.Version > 0;
        _txTracer.StartOperation(programCounter, instruction, gasAvailable, vmState.Env,
            isEofFrame ? sectionIndex : 0, isEofFrame ? vmState.ReturnStackHead + 1 : 0);
        if (_txTracer.IsTracingMemory)
        {
            _txTracer.SetOperationMemory(vmState.Memory.GetTrace());
            _txTracer.SetOperationMemorySize(vmState.Memory.Size);
        }

        if (_txTracer.IsTracingStack)
        {
            Memory<byte> stackMemory = vmState.DataStack.AsMemory().Slice(0, stackValue.Head * EvmStack.WordSize);
            _txTracer.SetOperationStack(new TraceStack(stackMemory));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void EndInstructionTrace(long gasAvailable)
    {
        _txTracer.ReportOperationRemainingGas(gasAvailable);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndInstructionTraceError(long gasAvailable, EvmExceptionType evmExceptionType)
    {
        _txTracer.ReportOperationRemainingGas(gasAvailable);
        _txTracer.ReportOperationError(evmExceptionType);
    }

    internal readonly ref struct CallResult
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
        public static CallResult InvalidAddressRange => new(EvmExceptionType.AddressOutOfRange);
        public static CallResult Empty(int fromVersion) => new(null, default, null, fromVersion);

        public CallResult(EvmState stateToExecute)
        {
            StateToExecute = stateToExecute;
            Output = (null, Array.Empty<byte>());
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = EvmExceptionType.None;
        }

        public CallResult(ReadOnlyMemory<byte> output, bool? precompileSuccess, int fromVersion, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
        {
            StateToExecute = null;
            Output = (null, output);
            PrecompileSuccess = precompileSuccess;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
            FromVersion = fromVersion;
        }

        public CallResult(ICodeInfo? container, ReadOnlyMemory<byte> output, bool? precompileSuccess, int fromVersion, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
        {
            StateToExecute = null;
            Output = (container, output);
            PrecompileSuccess = precompileSuccess;
            ShouldRevert = shouldRevert;
            ExceptionType = exceptionType;
            FromVersion = fromVersion;
        }

        private CallResult(EvmExceptionType exceptionType)
        {
            StateToExecute = null;
            Output = (null, StatusCode.FailureBytes);
            PrecompileSuccess = null;
            ShouldRevert = false;
            ExceptionType = exceptionType;
        }

        public EvmState? StateToExecute { get; }
        public (ICodeInfo Container, ReadOnlyMemory<byte> Bytes) Output { get; }
        public EvmExceptionType ExceptionType { get; }
        public bool ShouldRevert { get; }
        public bool? PrecompileSuccess { get; } // TODO: check this behaviour as it seems it is required and previously that was not the case
        public bool IsReturn => StateToExecute is null;
        public bool IsException => ExceptionType != EvmExceptionType.None;
        public int FromVersion { get; }
    }
}

public interface IFlag
{
    virtual static bool IsActive { get; }
}

public struct OffFlag : IFlag
{
    public static bool IsActive => false;
}
public struct OnFlag : IFlag
{
    public static bool IsActive => true;
}
