
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Evm.EOF;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;
using static Nethermind.Evm.EvmInstructions;

#if DEBUG
using Nethermind.Evm.Tracing.Debugger;
#endif

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]

namespace Nethermind.Evm;

using Int256;

public class VirtualMachine : IVirtualMachine
{
    public const int MaxCallDepth = EvmObjectFormat.Eof1.RETURN_STACK_MAX_HEIGHT;
    private readonly static UInt256 P255Int = (UInt256)System.Numerics.BigInteger.Pow(2, 255);
    internal readonly static byte[] EofHash256 = KeccakHash.ComputeHashBytes(EvmObjectFormat.MAGIC);
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

    private readonly IEvm _evm;
    internal ICodeInfoRepository CodeInfoRepository { get; }
    internal IReleaseSpec Spec => _evm.Spec;

    public VirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider,
        ICodeInfoRepository codeInfoRepository,
        ILogManager? logManager)
    {
        ILogger logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        CodeInfoRepository = codeInfoRepository;
        _evm = logger.IsTrace
            ? new VirtualMachine<IsTracing>(blockhashProvider, specProvider, codeInfoRepository, logger)
            : new VirtualMachine<NotTracing>(blockhashProvider, specProvider, codeInfoRepository, logger);
    }

    public TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
        where TTracingActions : struct, VirtualMachine.IIsTracing
        => _evm.Run<TTracingActions>(state, worldState, txTracer);

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
        public static object BoxedEmpty { get; } = new object();
        public static CallResult Empty(int fromVersion) => new(default, null, fromVersion);

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

        public CallResult(ICodeInfo container, ReadOnlyMemory<byte> output, bool? precompileSuccess, int fromVersion, bool shouldRevert = false, EvmExceptionType exceptionType = EvmExceptionType.None)
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

    public interface IIsTracing { }
    public readonly struct NotTracing : IIsTracing { }
    public readonly struct IsTracing : IIsTracing { }
}

internal sealed class VirtualMachine<TLogger> : IEvm where TLogger : struct, IIsTracing
{
    private readonly byte[] _chainId;

    private readonly IBlockhashProvider _blockhashProvider;
    private readonly ISpecProvider _specProvider;
    private readonly ILogger _logger;
    private IWorldState _state;
    private readonly Stack<EvmState> _stateStack = new();
    private (Address Address, bool ShouldDelete) _parityTouchBugAccount = (Address.FromNumber(3), false);
    private ReadOnlyMemory<byte> _returnDataBuffer = Array.Empty<byte>();
    private ITxTracer _txTracer = NullTxTracer.Instance;
    private readonly ICodeInfoRepository _codeInfoRepository;
    private IReleaseSpec _spec;

    public ICodeInfoRepository CodeInfoRepository => _codeInfoRepository;
    public IReleaseSpec Spec => _spec;
    public ITxTracer TxTracer => _txTracer;
    public IWorldState WorldState => _state;
    public ReadOnlySpan<byte> ChainId => _chainId;

    public VirtualMachine(
        IBlockhashProvider? blockhashProvider,
        ISpecProvider? specProvider,
        ICodeInfoRepository codeInfoRepository,
        ILogger? logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _blockhashProvider = blockhashProvider ?? throw new ArgumentNullException(nameof(blockhashProvider));
        _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
        _codeInfoRepository = codeInfoRepository ?? throw new ArgumentNullException(nameof(codeInfoRepository));
        _chainId = ((UInt256)specProvider.ChainId).ToBigEndian();
    }

    public TransactionSubstate Run<TTracingActions>(EvmState state, IWorldState worldState, ITxTracer txTracer)
        where TTracingActions : struct, IIsTracing
    {
        _txTracer = txTracer;
        _state = worldState;

        _spec = _specProvider.GetSpec(state.Env.TxExecutionContext.BlockExecutionContext.Header.Number, state.Env.TxExecutionContext.BlockExecutionContext.Header.Timestamp);
        EvmState currentState = state;
        ReadOnlyMemory<byte>? previousCallResult = null;
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

                    callResult = ExecutePrecompile(currentState);

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
                        ? ExecuteCall<NotTracing>(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination)
                        : ExecuteCall<IsTracing>(currentState, previousCallResult, previousCallOutput, previousCallOutputDestination);

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

                        RevertParityTouchBugAccount();

                        if (currentState.IsTopLevel)
                        {
                            return new TransactionSubstate(callResult.ExceptionType, isTracing);
                        }

                        previousCallResult = currentState.ExecutionType.IsAnyCallEof() ? EofStatusCode.FailureBytes : StatusCode.FailureBytes;
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
                        long codeDepositGasCost = CodeDepositHandler.CalculateCost(_spec, callResult.Output.Bytes.Length);

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
                        else
                        {
                            if (currentState.ExecutionType.IsAnyCreate() && currentState.GasAvailable < codeDepositGasCost)
                            {
                                if (_spec.ChargeForTopLevelCreate)
                                {
                                    _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                                }
                                else
                                {
                                    _txTracer.ReportActionEnd(currentState.GasAvailable, currentState.To, callResult.Output.Bytes);
                                }
                            }
                            // Reject code starting with 0xEF if EIP-3541 is enabled.
                            else if (currentState.ExecutionType.IsAnyCreate() && CodeDepositHandler.CodeIsInvalid(_spec, callResult.Output.Bytes, callResult.FromVersion))
                            {
                                _txTracer.ReportActionError(EvmExceptionType.InvalidCode);
                            }
                            else
                            {
                                if (currentState.ExecutionType.IsAnyCreate())
                                {
                                    _txTracer.ReportActionEnd(currentState.GasAvailable - codeDepositGasCost, currentState.To, callResult.Output.Bytes);
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
                        isTracerConnected: isTracing,
                        _logger);
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
                        if (previousState.ExecutionType.IsAnyCreateLegacy())
                        {
                            long codeDepositGasCost = CodeDepositHandler.CalculateCost(_spec, callResult.Output.Bytes.Length);
                            bool invalidCode = !CodeDepositHandler.IsValidWithLegacyRules(_spec, callResult.Output.Bytes.Span);
                            if (gasAvailableForCodeDeposit >= codeDepositGasCost && !invalidCode)
                            {
                                ReadOnlyMemory<byte> code = callResult.Output.Bytes;
                                _codeInfoRepository.InsertCode(_state, code, callCodeOwner, _spec);
                                currentState.GasAvailable -= codeDepositGasCost;

                                if (_txTracer.IsTracingActions)
                                {
                                    _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output.Bytes);
                                }
                            }
                            else if (_spec.FailOnOutOfGasCodeDeposit || invalidCode)
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
                                _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output.Bytes);
                            }
                        }
                        else if (previousState.ExecutionType.IsAnyCreateEof())
                        {
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
                                EvmObjectFormat.VERSION_OFFSET // magic + version
                                + EvmObjectFormat.Eof1.MINIMUM_HEADER_SECTION_SIZE // type section : (1 byte of separator + 2 bytes for size)
                                + EvmObjectFormat.ONE_BYTE_LENGTH + EvmObjectFormat.TWO_BYTE_LENGTH + EvmObjectFormat.TWO_BYTE_LENGTH * deployCodeInfo.Header.CodeSections.Count // code section :  (1 byte of separator + (CodeSections count) * 2 bytes for size)
                                + (deployCodeInfo.Header.ContainerSections is null
                                    ? 0 // container section :  (0 bytes if no container section is available)
                                    : EvmObjectFormat.ONE_BYTE_LENGTH + EvmObjectFormat.TWO_BYTE_LENGTH + EvmObjectFormat.TWO_BYTE_LENGTH * deployCodeInfo.Header.ContainerSections.Value.Count) // container section :  (1 byte of separator + (ContainerSections count) * 2 bytes for size)
                                + EvmObjectFormat.ONE_BYTE_LENGTH; // data section seperator

                            ushort dataSize = (ushort)(deployCodeInfo.DataSection.Length + auxExtraData.Length);
                            bytecodeResult[dataSubheaderSectionStart + 1] = (byte)(dataSize >> 8);
                            bytecodeResult[dataSubheaderSectionStart + 2] = (byte)(dataSize & 0xFF);

                            bytecodeResultArray = bytecodeResult.ToArray();

                            // 3 - if updated deploy container size exceeds MAX_CODE_SIZE instruction exceptionally aborts
                            bool invalidCode = bytecodeResultArray.Length > _spec.MaxCodeSize;
                            long codeDepositGasCost = CodeDepositHandler.CalculateCost(_spec, bytecodeResultArray?.Length ?? 0);
                            if (gasAvailableForCodeDeposit >= codeDepositGasCost && !invalidCode)
                            {
                                // 4 - set state[new_address].code to the updated deploy container
                                // push new_address onto the stack (already done before the ifs)
                                _codeInfoRepository.InsertCode(_state, bytecodeResultArray, callCodeOwner, _spec);
                                currentState.GasAvailable -= codeDepositGasCost;

                                if (_txTracer.IsTracingActions)
                                {
                                    _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, bytecodeResultArray);
                                }
                            }
                            else if (_spec.FailOnOutOfGasCodeDeposit || invalidCode)
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
                                _txTracer.ReportActionEnd(0L, callCodeOwner, bytecodeResultArray);
                            }
                        }
                    }
                    else
                    {
                        _returnDataBuffer = callResult.Output.Bytes;
                        previousCallResult = previousState.ExecutionType.IsAnyCallEof() ? EofStatusCode.SuccessBytes :
                            callResult.PrecompileSuccess.HasValue
                            ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes)
                            : StatusCode.SuccessBytes;
                        previousCallOutput = callResult.Output.Bytes.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Bytes.Length, (int)previousState.OutputLength));
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
                    _returnDataBuffer = callResult.Output.Bytes;
                    previousCallResult = previousState.ExecutionType.IsAnyCallEof() ? EofStatusCode.RevertBytes : StatusCode.FailureBytes;
                    previousCallOutput = callResult.Output.Bytes.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Bytes.Length, (int)previousState.OutputLength));
                    previousCallOutputDestination = (ulong)previousState.OutputDestination;


                    if (typeof(TTracingActions) == typeof(IsTracing))
                    {
                        _txTracer.ReportActionRevert(previousState.GasAvailable, callResult.Output.Bytes);
                    }
                }
            }
            catch (Exception ex) when (ex is EvmException or OverflowException)
            {
                if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"exception ({ex.GetType().Name}) in {currentState.ExecutionType} at depth {currentState.Env.CallDepth} - restoring snapshot");

                _state.Restore(currentState.Snapshot);

                RevertParityTouchBugAccount();

                if (txTracer.IsTracingInstructions)
                {
                    txTracer.ReportOperationRemainingGas(0);
                    txTracer.ReportOperationError(ex is EvmException evmException ? evmException.ExceptionType : EvmExceptionType.Other);
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

                previousCallResult = currentState.ExecutionType.IsAnyCallEof() ? EofStatusCode.FailureBytes : StatusCode.FailureBytes;
                previousCallOutputDestination = UInt256.Zero;
                _returnDataBuffer = Array.Empty<byte>();
                previousCallOutput = ZeroPaddedSpan.Empty;

                currentState.Dispose();
                currentState = _stateStack.Pop();
                currentState.IsContinuation = true;
            }
        }
    }

    private void RevertParityTouchBugAccount()
    {
        if (_parityTouchBugAccount.ShouldDelete)
        {
            if (_state.AccountExists(_parityTouchBugAccount.Address))
            {
                _state.AddToBalance(_parityTouchBugAccount.Address, UInt256.Zero, _spec);
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

    private static void UpdateGasUp(long refund, ref long gasAvailable)
    {
        gasAvailable += refund;
    }

    private bool ChargeAccountAccessGas(ref long gasAvailable, EvmState vmState, Address address, bool chargeForWarm = true)
    {
        // Console.WriteLine($"Accessing {address}");

        bool result = true;
        if (_spec.UseHotAndColdStorage)
        {
            if (_txTracer.IsTracingAccess) // when tracing access we want cost as if it was warmed up from access list
            {
                vmState.WarmUp(address);
            }

            if (vmState.IsCold(address) && !address.IsPrecompile(_spec))
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
        StorageAccessType storageAccessType)
    {
        // Console.WriteLine($"Accessing {storageCell} {storageAccessType}");

        bool result = true;
        if (_spec.UseHotAndColdStorage)
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

    private CallResult ExecutePrecompile(EvmState state)
    {
        ReadOnlyMemory<byte> callData = state.Env.InputData;
        UInt256 transferValue = state.Env.TransferValue;
        long gasAvailable = state.GasAvailable;

        IPrecompile precompile = state.Env.CodeInfo.Precompile;
        long baseGasCost = precompile.BaseGasCost(_spec);
        long blobGasCost = precompile.DataGasCost(callData, _spec);

        bool wasCreated = false;
        if (!_state.AccountExists(state.Env.ExecutingAccount))
        {
            wasCreated = true;
            _state.CreateAccount(state.Env.ExecutingAccount, transferValue);
        }
        else
        {
            _state.AddToBalance(state.Env.ExecutingAccount, transferValue, _spec);
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
            && _spec.ClearEmptyAccountWhenTouched)
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
                _state.AddToBalance(env.ExecutingAccount, env.TransferValue, _spec);
            }

            if (vmState.ExecutionType.IsAnyCreate() && _spec.ClearEmptyAccountWhenTouched)
            {
                _state.IncrementNonce(env.ExecutingAccount);
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

        vmState.InitStacks();
        EvmStack stack = new(vmState.DataStack.AsSpan(), vmState.DataStackHead, _txTracer);
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

        _vmState = vmState;
        // Struct generic parameter is used to burn out all the if statements
        // and inner code by typeof(TTracing) == typeof(NotTracing)
        // checks that are evaluated to constant values at compile time.
        // This only works for structs, not for classes or interface types
        // which use shared generics.
        if (!_txTracer.IsTracingRefunds)
        {
            return _txTracer.IsTracingOpLevelStorage ?
                ExecuteCode<TTracingInstructions, NotTracing, IsTracing>(ref stack, gasAvailable) :
                ExecuteCode<TTracingInstructions, NotTracing, NotTracing>(ref stack, gasAvailable);
        }
        else
        {
            return _txTracer.IsTracingOpLevelStorage ?
                ExecuteCode<TTracingInstructions, IsTracing, IsTracing>(ref stack, gasAvailable) :
                ExecuteCode<TTracingInstructions, IsTracing, NotTracing>(ref stack, gasAvailable);
        }
    Empty:
        return CallResult.Empty(vmState.Env.CodeInfo.Version);
    OutOfGas:
        return CallResult.OutOfGasException;
    }

    EvmState IEvm.State => _vmState;
    EvmState _vmState;

    [SkipLocalsInit]
    private unsafe CallResult ExecuteCode<TTracingInstructions, TTracingRefunds, TTracingStorage>(scoped ref EvmStack stack, long gasAvailable)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
        where TTracingStorage : struct, IIsTracing
    {

        ref readonly ExecutionEnvironment env = ref _vmState.Env;
        ref readonly TxExecutionContext txCtx = ref env.TxExecutionContext;
        ref readonly BlockExecutionContext blkCtx = ref txCtx.BlockExecutionContext;

        int programCounter = _vmState.ProgramCounter;
        int sectionIndex = _vmState.FunctionIndex;

        ReadOnlySpan<byte> codeSection = env.CodeInfo.CodeSection.Span;
        ReadOnlySpan<byte> dataSection = env.CodeInfo.DataSection.Span;

        EvmExceptionType exceptionType = EvmExceptionType.None;
        bool isRevert = false;
#if DEBUG
        DebugTracer? debugger = _txTracer.GetTracer<DebugTracer>();
#endif
        SkipInit(out UInt256 a);
        SkipInit(out UInt256 b);
        SkipInit(out UInt256 c);
        SkipInit(out UInt256 result);
        SkipInit(out StorageCell storageCell);
        object returnData;
        ZeroPaddedSpan slice;
        bool isCancelable = _txTracer.IsCancelable;
        uint codeLength = (uint)codeSection.Length;
        while ((uint)programCounter < codeLength)
        {
#if DEBUG
            debugger?.TryWait(ref _vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
            Instruction instruction = (Instruction)codeSection[programCounter];

            if (isCancelable && _txTracer.IsCancelled)
            {
                ThrowOperationCanceledException();
            }

            // Evaluated to constant at compile time and code elided if not tracing
            if (typeof(TTracingInstructions) == typeof(IsTracing))
                StartInstructionTrace(instruction, _vmState, gasAvailable, programCounter, in stack);

            programCounter++;

            if (instruction == Instruction.STOP)
            {
                if (_vmState.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
                {
                    goto InvalidInstruction;
                }
                goto EmptyReturn;
            }
            else if (instruction < Instruction.EXTCODESIZE)
            {
                exceptionType = CalliJmpTable[(int)instruction](this, ref stack, ref gasAvailable, ref programCounter);
            }
            else
            {
                switch (instruction)
                {
                    case Instruction.EXTCODESIZE:
                        {
                            gasAvailable -= _spec.GetExtCodeCost();

                            Address address = stack.PopAddress();
                            if (address is null) goto StackUnderflow;

                            if (!ChargeAccountAccessGas(ref gasAvailable, _vmState, address)) goto OutOfGas;

                            if (typeof(TTracingInstructions) != typeof(IsTracing) && programCounter < codeSection.Length)
                            {
                                bool optimizeAccess = false;
                                Instruction nextInstruction = (Instruction)codeSection[programCounter];
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
                                    if (!stack.PopLimbo()) goto StackUnderflow;
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

                            InstructionExtCodeSize(address, ref stack);
                            break;
                        }
                    case Instruction.EXTCODECOPY:
                        exceptionType = InstructionExtCodeCopy(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.RETURNDATASIZE:
                        {
                            if (!_spec.ReturnDataOpcodesEnabled) goto InvalidInstruction;

                            gasAvailable -= GasCostOf.Base;

                            result = (UInt256)_returnDataBuffer.Length;
                            stack.PushUInt256(in result);
                            break;
                        }
                    case Instruction.RETURNDATACOPY:
                        {
                            if (!_spec.ReturnDataOpcodesEnabled) goto InvalidInstruction;

                            if (!stack.PopUInt256(out a)) goto StackUnderflow;
                            if (!stack.PopUInt256(out b)) goto StackUnderflow;
                            if (!stack.PopUInt256(out c)) goto StackUnderflow;
                            gasAvailable -= GasCostOf.VeryLow + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in c);


                            if (env.CodeInfo.Version == 0 && (UInt256.AddOverflow(c, b, out result) || result > _returnDataBuffer.Length))
                            {
                                goto AccessViolation;
                            }

                            if (!c.IsZero)
                            {
                                if (!UpdateMemoryCost(_vmState, ref gasAvailable, in a, c)) goto OutOfGas;

                                slice = _returnDataBuffer.Span.SliceWithZeroPadding(b, (int)c);
                                _vmState.Memory.Save(in a, in slice);
                                if (typeof(TTracingInstructions) == typeof(IsTracing))
                                {
                                    _txTracer.ReportMemoryChange((long)a, in slice);
                                }
                            }
                            break;
                        }
                    case Instruction.EXTCODEHASH:
                        exceptionType = InstructionExtCodeHash(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.BLOCKHASH:
                        {
                            Metrics.BlockhashOpcode++;

                            gasAvailable -= GasCostOf.BlockHash;

                            if (!stack.PopUInt256(out a)) goto StackUnderflow;
                            long number = a > long.MaxValue ? long.MaxValue : (long)a;

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
                        exceptionType = InstructionEnvBytes<OpCoinbase>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.TIMESTAMP:
                        exceptionType = InstructionEnvUInt256<OpTimestamp>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.NUMBER:
                        exceptionType = InstructionEnvUInt256<OpNumber>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.PREVRANDAO:
                        exceptionType = InstructionPrevRandao(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.GASLIMIT:
                        exceptionType = InstructionEnvUInt256<OpGasLimit>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.CHAINID:
                        exceptionType = InstructionChainId(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.SELFBALANCE:
                        exceptionType = InstructionSelfBalance(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.BASEFEE:
                        exceptionType = InstructionEnvUInt256<OpBaseFee>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.BLOBHASH:
                        exceptionType = InstructionBlobHash(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.BLOBBASEFEE:
                        exceptionType = InstructionEnvUInt256<OpBlobBaseFee>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.POP:
                        exceptionType = InstructionPop(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.MLOAD:
                        exceptionType = InstructionMLoad(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.MSTORE:
                        exceptionType = InstructionMStore(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.MSTORE8:
                        exceptionType = InstructionMStore8(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.SLOAD:
                        exceptionType = InstructionSLoad(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.SSTORE:
                        exceptionType = InstructionSStore(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.JUMP:
                        exceptionType = InstructionJump(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.JUMPI:
                        exceptionType = InstructionJumpIf(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.PC:
                        exceptionType = InstructionProgramCounter(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.MSIZE:
                        exceptionType = InstructionEnvUInt256<OpMSize>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.GAS:
                        exceptionType = InstructionGas(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.JUMPDEST:
                        exceptionType = InstructionJumpDest(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.TLOAD:
                        exceptionType = InstructionTLoad(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.TSTORE:
                        exceptionType = InstructionTStore(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.MCOPY:
                        exceptionType = InstructionMCopy(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.PUSH0:
                        {
                            if (!_spec.IncludePush0Instruction) goto InvalidInstruction;
                            gasAvailable -= GasCostOf.Base;

                            stack.PushZero();
                            break;
                        }
                    case Instruction.PUSH1:
                        {
                            gasAvailable -= GasCostOf.VeryLow;

                            if (programCounter >= codeSection.Length)
                            {
                                stack.PushZero();
                            }
                            else
                            {
                                stack.PushByte(codeSection[programCounter]);
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
                            int usedFromCode = Math.Min(codeSection.Length - programCounter, length);
                            stack.PushLeftPaddedBytes(codeSection.Slice(programCounter, usedFromCode), length);

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
                    case Instruction.DUPN:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                                goto InvalidInstruction;

                            if (!UpdateGas(GasCostOf.Dupn, ref gasAvailable))
                                goto OutOfGas;

                            byte imm = codeSection[programCounter];
                            stack.Dup(imm + 1);

                            programCounter += 1;
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
                    case Instruction.SWAPN:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                                goto InvalidInstruction;

                            if (!UpdateGas(GasCostOf.Swapn, ref gasAvailable))
                                goto OutOfGas;

                            int n = 1 + codeSection[programCounter];
                            if (!stack.Swap(n + 1)) goto StackUnderflow;

                            programCounter += 1;
                            break;
                        }
                    case Instruction.EXCHANGE:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                                goto InvalidInstruction;

                            if (!UpdateGas(GasCostOf.Swapn, ref gasAvailable))
                                goto OutOfGas;

                            int n = 1 + (int)(codeSection[programCounter] >> 0x04);
                            int m = 1 + (int)(codeSection[programCounter] & 0x0f);

                            stack.Exchange(n + 1, m + n + 1);

                            programCounter += 1;
                            break;
                        }
                    case Instruction.LOG0:
                        exceptionType = InstructionLog<Op0>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.LOG1:
                        exceptionType = InstructionLog<Op1>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.LOG2:
                        exceptionType = InstructionLog<Op2>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.LOG3:
                        exceptionType = InstructionLog<Op3>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.LOG4:
                        exceptionType = InstructionLog<Op4>(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.DATALOAD:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                                goto InvalidInstruction;

                            if (!UpdateGas(GasCostOf.DataLoad, ref gasAvailable)) goto OutOfGas;

                            stack.PopUInt256(out a);
                            ZeroPaddedSpan zpbytes = dataSection.SliceWithZeroPadding(a, 32);
                            stack.PushBytes(zpbytes);
                            break;
                        }
                    case Instruction.DATALOADN:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                                goto InvalidInstruction;

                            if (!UpdateGas(GasCostOf.DataLoadN, ref gasAvailable)) goto OutOfGas;

                            var offset = codeSection.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
                            ZeroPaddedSpan zpbytes = dataSection.SliceWithZeroPadding(offset, 32);
                            stack.PushBytes(zpbytes);

                            programCounter += EvmObjectFormat.TWO_BYTE_LENGTH;
                            break;
                        }
                    case Instruction.DATASIZE:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                                goto InvalidInstruction;

                            if (!UpdateGas(GasCostOf.DataSize, ref gasAvailable)) goto OutOfGas;

                            stack.PushUInt32(dataSection.Length);
                            break;
                        }
                    case Instruction.DATACOPY:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                                goto InvalidInstruction;

                            stack.PopUInt256(out UInt256 memOffset);
                            stack.PopUInt256(out UInt256 offset);
                            stack.PopUInt256(out UInt256 size);

                            if (!UpdateGas(GasCostOf.DataCopy + GasCostOf.Memory * EvmPooledMemory.Div32Ceiling(in size), ref gasAvailable))
                                goto OutOfGas;

                            if (size > UInt256.Zero)
                            {
                                if (!UpdateMemoryCost(_vmState, ref gasAvailable, in memOffset, size))
                                    goto OutOfGas;

                                ZeroPaddedSpan dataSectionSlice = dataSection.SliceWithZeroPadding(offset, (int)size);
                                _vmState.Memory.Save(in memOffset, dataSectionSlice);
                                if (_txTracer.IsTracingInstructions)
                                {
                                    _txTracer.ReportMemoryChange((long)memOffset, dataSectionSlice);
                                }
                            }

                            break;
                        }
                    case Instruction.CREATE:
                    case Instruction.CREATE2:
                        {
                            Metrics.IncrementCreates();
                            if (!_spec.Create2OpcodeEnabled && instruction == Instruction.CREATE2) goto InvalidInstruction;

                            if (_vmState.IsStatic) goto StaticCallViolation;

                            (exceptionType, returnData) = InstructionCreate(_vmState, ref stack, ref gasAvailable, instruction);
                            if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                            if (returnData is null) break;

                            goto DataReturnNoTrace;
                        }
                    case Instruction.EOFCREATE:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                            {
                                goto InvalidInstruction;
                            }

                            if (_vmState.IsStatic) goto StaticCallViolation;

                            (exceptionType, returnData) = InstructionEofCreate(_vmState, ref programCounter, ref codeSection, ref stack, ref gasAvailable, instruction);

                            if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                            if (returnData is null) break;

                            goto DataReturnNoTrace;
                        }
                    case Instruction.RETURN:
                        {
                            if (_vmState.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
                            {
                                goto InvalidInstruction;
                            }
                            exceptionType = InstructionReturn(_vmState, ref stack, ref gasAvailable, out returnData);
                            if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                            goto DataReturn;
                        }
                    case Instruction.CALL:
                    case Instruction.CALLCODE:
                    case Instruction.DELEGATECALL:
                    case Instruction.STATICCALL:
                        {
                            exceptionType = InstructionCall<TTracingInstructions, TTracingRefunds>(_vmState, ref stack, ref gasAvailable, instruction, out returnData);
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
                            if (!_spec.RevertOpcodeEnabled) goto InvalidInstruction;

                            exceptionType = InstructionRevert(_vmState, ref stack, ref gasAvailable, out returnData);
                            if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                            isRevert = true;
                            goto DataReturn;
                        }
                    case Instruction.INVALID:
                        exceptionType = InstructionInvalid(this, ref stack, ref gasAvailable, ref programCounter);
                        break;
                    case Instruction.SELFDESTRUCT:
                        exceptionType = InstructionSelfDestruct(this, ref stack, ref gasAvailable, ref programCounter);
                        if (exceptionType != EvmExceptionType.None) goto ReturnFailure;
                        goto EmptyReturn;
                    case Instruction.RJUMP:
                        {
                            if (_spec.IsEofEnabled && env.CodeInfo.Version > 0)
                            {
                                if (!UpdateGas(GasCostOf.RJump, ref gasAvailable)) goto OutOfGas;
                                short offset = codeSection.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
                                programCounter += EvmObjectFormat.TWO_BYTE_LENGTH + offset;
                                break;
                            }
                            goto InvalidInstruction;
                        }
                    case Instruction.RJUMPI:
                        {
                            if (_spec.IsEofEnabled && env.CodeInfo.Version > 0)
                            {
                                if (!UpdateGas(GasCostOf.RJumpi, ref gasAvailable)) goto OutOfGas;
                                Span<byte> condition = stack.PopWord256();
                                short offset = codeSection.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
                                if (!condition.SequenceEqual(BytesZero32))
                                {
                                    programCounter += offset;
                                }
                                programCounter += EvmObjectFormat.TWO_BYTE_LENGTH;
                                break;
                            }
                            goto InvalidInstruction;
                        }
                    case Instruction.RJUMPV:
                        {
                            if (_spec.IsEofEnabled && env.CodeInfo.Version > 0)
                            {
                                if (!UpdateGas(GasCostOf.RJumpv, ref gasAvailable)) goto OutOfGas;

                                stack.PopUInt256(out a);
                                var count = codeSection[programCounter] + 1;
                                var immediates = (ushort)(count * EvmObjectFormat.TWO_BYTE_LENGTH + EvmObjectFormat.ONE_BYTE_LENGTH);
                                if (a < count)
                                {
                                    int case_v = programCounter + EvmObjectFormat.ONE_BYTE_LENGTH + (int)a * EvmObjectFormat.TWO_BYTE_LENGTH;
                                    int offset = codeSection.Slice(case_v, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthInt16();
                                    programCounter += offset;
                                }
                                programCounter += immediates;
                                break;
                            }
                            goto InvalidInstruction;
                        }
                    case Instruction.CALLF:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                            {
                                goto InvalidInstruction;
                            }

                            if (!UpdateGas(GasCostOf.Callf, ref gasAvailable)) goto OutOfGas;
                            var index = (int)codeSection.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
                            (int inputCount, _, int maxStackHeight) = env.CodeInfo.GetSectionMetadata(index);

                            if (EvmObjectFormat.Eof1.MAX_STACK_HEIGHT - maxStackHeight + inputCount < stack.Head)
                            {
                                goto StackOverflow;
                            }

                            if (_vmState.ReturnStackHead == EvmObjectFormat.Eof1.RETURN_STACK_MAX_HEIGHT)
                                goto InvalidSubroutineEntry;

                            _vmState.ReturnStack[_vmState.ReturnStackHead++] = new EvmState.ReturnState
                            {
                                Index = sectionIndex,
                                Height = stack.Head - inputCount,
                                Offset = programCounter + EvmObjectFormat.TWO_BYTE_LENGTH
                            };

                            sectionIndex = index;
                            programCounter = env.CodeInfo.CodeSectionOffset(index).Start;
                            break;
                        }

                    case Instruction.JUMPF:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                            {
                                goto InvalidInstruction;
                            }

                            if (!UpdateGas(GasCostOf.Jumpf, ref gasAvailable)) goto OutOfGas;
                            var index = (int)codeSection.Slice(programCounter, EvmObjectFormat.TWO_BYTE_LENGTH).ReadEthUInt16();
                            (int inputCount, _, int maxStackHeight) = env.CodeInfo.GetSectionMetadata(index);

                            if (EvmObjectFormat.Eof1.MAX_STACK_HEIGHT - maxStackHeight + inputCount < stack.Head)
                            {
                                goto StackOverflow;
                            }

                            sectionIndex = index;
                            programCounter = env.CodeInfo.CodeSectionOffset(index).Start;
                            break;
                        }
                    case Instruction.RETF:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                            {
                                goto InvalidInstruction;
                            }

                            if (!UpdateGas(GasCostOf.Retf, ref gasAvailable)) goto OutOfGas;
                            (_, int outputCount, _) = env.CodeInfo.GetSectionMetadata(sectionIndex);

                            var stackFrame = _vmState.ReturnStack[--_vmState.ReturnStackHead];
                            sectionIndex = stackFrame.Index;
                            programCounter = stackFrame.Offset;
                            break;
                        }
                    case Instruction.EXTCALL:
                    case Instruction.EXTDELEGATECALL:
                    case Instruction.EXTSTATICCALL:
                        {
                            exceptionType = InstructionEofCall<TTracingRefunds>(_vmState, ref stack, ref gasAvailable, instruction, out returnData);
                            if (exceptionType != EvmExceptionType.None) goto ReturnFailure;

                            if (returnData is null)
                            {
                                break;
                            }
                            if (ReferenceEquals(returnData, CallResult.BoxedEmpty))
                            {
                                // Result pushed to stack
                                continue;
                            }

                            goto DataReturn;
                        }
                    case Instruction.RETURNCONTRACT:
                        {
                            if (!_spec.IsEofEnabled || !_vmState.ExecutionType.IsAnyCreateEof())
                                goto InvalidInstruction;

                            if (!UpdateGas(GasCostOf.ReturnContract, ref gasAvailable)) goto OutOfGas;

                            byte sectionIdx = codeSection[programCounter++];
                            ReadOnlyMemory<byte> deployCode = env.CodeInfo.ContainerSection[(Range)env.CodeInfo.ContainerSectionOffset(sectionIdx)];
                            EofCodeInfo deploycodeInfo = (EofCodeInfo)CodeInfoFactory.CreateCodeInfo(deployCode, _spec, EvmObjectFormat.ValidationStrategy.ExractHeader);

                            stack.PopUInt256(out a);
                            stack.PopUInt256(out b);
                            ReadOnlyMemory<byte> auxData = ReadOnlyMemory<byte>.Empty;

                            if (!UpdateMemoryCost(_vmState, ref gasAvailable, in a, b)) goto OutOfGas;

                            int projectedNewSize = (int)b + deploycodeInfo.DataSection.Length;
                            if (projectedNewSize < deploycodeInfo.Header.DataSection.Size || projectedNewSize > UInt16.MaxValue)
                            {
                                goto AccessViolation;
                            }

                            auxData = _vmState.Memory.Load(a, b);

                            UpdateCurrentState(_vmState, programCounter, gasAvailable, stack.Head, sectionIndex);
                            return new CallResult(deploycodeInfo, auxData, null, env.CodeInfo.Version);
                        }
                    case Instruction.RETURNDATALOAD:
                        {
                            if (!_spec.IsEofEnabled || env.CodeInfo.Version == 0)
                                goto InvalidInstruction;

                            gasAvailable -= GasCostOf.VeryLow;

                            if (!stack.PopUInt256(out a)) goto StackUnderflow;

                            slice = _returnDataBuffer.Span.SliceWithZeroPadding(a, 32);
                            stack.PushBytes(slice);
                            break;
                        }
                    default:
                        {
                            goto InvalidInstruction;
                        }
                }
            }

            if (exceptionType != EvmExceptionType.None) goto ReturnFailure;
            if (gasAvailable < 0)
            {
                goto OutOfGas;
            }

            if (typeof(TTracingInstructions) == typeof(IsTracing))
            {
                EndInstructionTrace(gasAvailable, _vmState.Memory.Size);
            }
        }

        goto EmptyReturnNoTrace;

    // Common exit errors, goto labels to reduce in loop code duplication and to keep loop body smaller
    EmptyReturn:
        if (typeof(TTracingInstructions) == typeof(IsTracing)) EndInstructionTrace(gasAvailable, _vmState.Memory.Size);
        EmptyReturnNoTrace:
        // Ensure gas is positive before updating state
        if (gasAvailable < 0) goto OutOfGas;
        UpdateCurrentState(_vmState, programCounter, gasAvailable, stack.Head, sectionIndex);
#if DEBUG
        debugger?.TryWait(ref _vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
        return CallResult.Empty(env.CodeInfo.Version);
    DataReturn:
        if (typeof(TTracingInstructions) == typeof(IsTracing)) EndInstructionTrace(gasAvailable, _vmState.Memory.Size);
        DataReturnNoTrace:
        // Ensure gas is positive before updating state
        if (gasAvailable < 0) goto OutOfGas;
        UpdateCurrentState(_vmState, programCounter, gasAvailable, stack.Head, sectionIndex);

        if (returnData is EvmState state)
        {
            return new CallResult(state);
        }
        return new CallResult((byte[])returnData, null, env.CodeInfo.Version, shouldRevert: isRevert);

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
    AccessViolation:
        exceptionType = EvmExceptionType.AccessViolation;
    ReturnFailure:
        return GetFailureReturn<TTracingInstructions>(gasAvailable, exceptionType);
    InvalidSubroutineEntry:
        exceptionType = EvmExceptionType.InvalidSubroutineEntry;
        goto ReturnFailure;
    StackOverflow:
        exceptionType = EvmExceptionType.StackOverflow;
        goto ReturnFailure;
        [DoesNotReturn]
        static void ThrowOperationCanceledException() =>
            throw new OperationCanceledException("Cancellation Requested");
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InstructionExtCodeSize(Address address, ref EvmStack stack)
    {
        ReadOnlyMemory<byte> accountCode = _codeInfoRepository.GetCachedCodeInfo(_state, address, _spec).MachineCode;
        if (_spec.IsEofEnabled && EvmObjectFormat.IsEof(accountCode.Span, out _))
        {
            stack.PushUInt256(2);
        }
        else
        {
            UInt256 result = (UInt256)accountCode.Length;
            stack.PushUInt256(in result);
        }
    }

    [SkipLocalsInit]
    private EvmExceptionType InstructionCall<TTracingInstructions, TTracingRefunds>(EvmState vmState, ref EvmStack stack, ref long gasAvailable,
        Instruction instruction, out object returnData)
        where TTracingInstructions : struct, IIsTracing
        where TTracingRefunds : struct, IIsTracing
    {
        Metrics.IncrementCalls();

        returnData = null;
        ref readonly ExecutionEnvironment env = ref vmState.Env;

        if (instruction == Instruction.DELEGATECALL && !_spec.DelegateCallEnabled ||
            instruction == Instruction.STATICCALL && !_spec.StaticCallEnabled) return EvmExceptionType.BadInstruction;

        if (!stack.PopUInt256(out UInt256 gasLimit)) return EvmExceptionType.StackUnderflow;
        Address codeSource = stack.PopAddress();
        if (codeSource is null) return EvmExceptionType.StackUnderflow;

        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, codeSource)) return EvmExceptionType.OutOfGas;

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
                if (!stack.PopUInt256(out callValue)) return EvmExceptionType.StackUnderflow;
                break;
        }

        UInt256 transferValue = instruction == Instruction.DELEGATECALL ? UInt256.Zero : callValue;
        if (!stack.PopUInt256(out UInt256 dataOffset)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 dataLength)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 outputOffset)) return EvmExceptionType.StackUnderflow;
        if (!stack.PopUInt256(out UInt256 outputLength)) return EvmExceptionType.StackUnderflow;

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

        if (!_spec.ClearEmptyAccountWhenTouched && !_state.AccountExists(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }
        else if (_spec.ClearEmptyAccountWhenTouched && transferValue != 0 && _state.IsDeadAccount(target))
        {
            gasExtra += GasCostOf.NewAccount;
        }

        if (!UpdateGas(_spec.GetCallCost(), ref gasAvailable) ||
            !UpdateMemoryCost(vmState, ref gasAvailable, in dataOffset, dataLength) ||
            !UpdateMemoryCost(vmState, ref gasAvailable, in outputOffset, outputLength) ||
            !UpdateGas(gasExtra, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        ICodeInfo codeInfo = _codeInfoRepository.GetCachedCodeInfo(_state, codeSource, _spec);
        codeInfo.AnalyseInBackgroundIfRequired();

        if (_spec.Use63Over64Rule)
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
                ReadOnlyMemory<byte>? memoryTrace = vmState.Memory.Inspect(in dataOffset, 32);
                _txTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? ReadOnlySpan<byte>.Empty : memoryTrace.Value.Span);
            }

            if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace("FAIL - call depth");
            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportOperationRemainingGas(gasAvailable);
            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);

            UpdateGasUp(gasLimitUl, ref gasAvailable);
            if (typeof(TTracingInstructions) == typeof(IsTracing)) _txTracer.ReportGasUpdateForVmTrace(gasLimitUl, gasAvailable);
            return EvmExceptionType.None;
        }

        Snapshot snapshot = _state.TakeSnapshot();
        _state.SubtractFromBalance(caller, transferValue, _spec);

        if (codeInfo.IsEmpty && typeof(TTracingInstructions) != typeof(IsTracing) && !_txTracer.IsTracingActions)
        {
            // Non contract call, no need to construct call frame can just credit balance and return gas
            _returnDataBuffer = default;
            stack.PushBytes(StatusCode.SuccessBytes.Span);
            UpdateGasUp(gasLimitUl, ref gasAvailable);
            return FastCall(_spec, out returnData, in transferValue, target);
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
        if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Tx call gas {gasLimitUl}");
        if (outputLength == 0)
        {
            // TODO: when output length is 0 outputOffset can have any value really
            // and the value does not matter and it can cause trouble when beyond long range
            outputOffset = 0;
        }

        ExecutionType executionType = GetCallExecutionType(instruction, env.IsPostMerge());
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

        EvmExceptionType FastCall(IReleaseSpec spec, out object returnData, in UInt256 transferValue, Address target)
        {
            if (!_state.AccountExists(target))
            {
                _state.CreateAccount(target, transferValue);
            }
            else
            {
                _state.AddToBalance(target, transferValue, _spec);
            }
            Metrics.IncrementEmptyCalls();

            returnData = CallResult.BoxedEmpty;
            return EvmExceptionType.None;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void TraceCallDetails(Address codeSource, ref UInt256 callValue, ref UInt256 transferValue, Address caller, Address target)
        {
            _logger.Trace($"caller {caller}");
            _logger.Trace($"code source {codeSource}");
            _logger.Trace($"target {target}");
            _logger.Trace($"value {callValue}");
            _logger.Trace($"transfer value {transferValue}");
        }
    }

    [SkipLocalsInit]
    private EvmExceptionType InstructionEofCall<TTracingRefunds>(EvmState vmState, ref EvmStack stack, ref long gasAvailable,
        Instruction instruction, out object returnData)
        where TTracingRefunds : struct, IIsTracing
    {
        Metrics.IncrementCalls();

        const int MIN_RETAINED_GAS = 5000;

        returnData = null;
        ref readonly ExecutionEnvironment env = ref vmState.Env;

        // Instruction is undefined in legacy code and only available in EOF
        if (!_spec.IsEofEnabled ||
            env.CodeInfo.Version == 0 ||
            (instruction == Instruction.EXTDELEGATECALL && !_spec.DelegateCallEnabled) ||
            (instruction == Instruction.EXTSTATICCALL && !_spec.StaticCallEnabled))
            return EvmExceptionType.BadInstruction;

        // 1. Pop required arguments from stack, halt with exceptional failure on stack underflow.
        stack.PopWord256(out Span<byte> targetBytes);
        stack.PopUInt256(out UInt256 dataOffset);
        stack.PopUInt256(out UInt256 dataLength);

        UInt256 callValue;
        switch (instruction)
        {
            case Instruction.EXTSTATICCALL:
                callValue = UInt256.Zero;
                break;
            case Instruction.EXTDELEGATECALL:
                callValue = env.Value;
                break;
            default: // Instruction.EXTCALL
                stack.PopUInt256(out callValue);
                break;
        }

        // 3. If value is non-zero:
        //  a: Halt with exceptional failure if the current frame is in static-mode.
        if (vmState.IsStatic && !callValue.IsZero) return EvmExceptionType.StaticCallViolation;
        //  b. Charge CALL_VALUE_COST gas.
        if (!callValue.IsZero && !UpdateGas(GasCostOf.CallValue, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        // 4. If target_address has any of the high 12 bytes set to a non-zero value
        // (i.e. it does not contain a 20-byte address)
        if (!targetBytes[0..12].IsZero())
        {
            //  then halt with an exceptional failure.
            return EvmExceptionType.AddressOutOfRange;
        }

        Address caller = instruction == Instruction.EXTDELEGATECALL ? env.Caller : env.ExecutingAccount;
        Address codeSource = new Address(targetBytes[12..].ToArray());
        Address target = instruction == Instruction.EXTDELEGATECALL
            ? env.ExecutingAccount
            : codeSource;

        // 5. Perform (and charge for) memory expansion using [input_offset, input_size].
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in dataOffset, in dataLength)) return EvmExceptionType.OutOfGas;
        // 1. Charge WARM_STORAGE_READ_COST (100) gas.
        // 6. If target_address is not in the warm_account_list, charge COLD_ACCOUNT_ACCESS - WARM_STORAGE_READ_COST (2500) gas.
        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, codeSource)) return EvmExceptionType.OutOfGas;

        if ((!_spec.ClearEmptyAccountWhenTouched && !_state.AccountExists(codeSource))
            || (_spec.ClearEmptyAccountWhenTouched && callValue != 0 && _state.IsDeadAccount(codeSource)))
        {
            // 7. If target_address is not in the state and the call configuration would result in account creation,
            //    charge ACCOUNT_CREATION_COST (25000) gas. (The only such case in this EIP is if value is non-zero.)
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        // 8. Calculate the gas available to callee as caller’s remaining gas reduced by max(floor(gas/64), MIN_RETAINED_GAS).
        long callGas = gasAvailable - Math.Max(gasAvailable / 64, MIN_RETAINED_GAS);

        // 9. Fail with status code 1 returned on stack if any of the following is true (only gas charged until this point is consumed):
        //  a: Gas available to callee at this point is less than MIN_CALLEE_GAS.
        //  b: Balance of the current account is less than value.
        //  c: Current call stack depth equals 1024.
        if (callGas < GasCostOf.CallStipend ||
            (!callValue.IsZero && _state.GetBalance(env.ExecutingAccount) < callValue) ||
            env.CallDepth >= MaxCallDepth)
        {
            returnData = CallResult.BoxedEmpty;
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushOne();

            if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace("FAIL - call depth");
            if (_txTracer.IsTracingInstructions)
            {
                // very specific for Parity trace, need to find generalization - very peculiar 32 length...
                ReadOnlyMemory<byte> memoryTrace = vmState.Memory.Inspect(in dataOffset, 32);
                _txTracer.ReportMemoryChange(dataOffset, memoryTrace.Span);
                _txTracer.ReportOperationRemainingGas(gasAvailable);
                _txTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
                _txTracer.ReportGasUpdateForVmTrace(callGas, gasAvailable);
            }

            return EvmExceptionType.None;
        }

        if (typeof(TLogger) == typeof(IsTracing))
        {
            _logger.Trace($"caller {caller}");
            _logger.Trace($"target {codeSource}");
            _logger.Trace($"value {callValue}");
        }

        ICodeInfo targetCodeInfo = _codeInfoRepository.GetCachedCodeInfo(_state, codeSource, _spec);
        targetCodeInfo.AnalyseInBackgroundIfRequired();

        if (instruction is Instruction.EXTDELEGATECALL
            && targetCodeInfo.Version == 0)
        {
            // https://github.com/ipsilon/eof/blob/main/spec/eof.md#new-behavior
            // EXTDELEGATECALL to a non-EOF contract (legacy contract, EOA, empty account) is disallowed,
            // and it returns 1 (same as when the callee frame reverts) to signal failure.
            // Only initial gas cost of EXTDELEGATECALL is consumed (similarly to the call depth check)
            // and the target address still becomes warm.
            returnData = CallResult.BoxedEmpty;
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushOne();
            return EvmExceptionType.None;
        }

        // 10. Perform the call with the available gas and configuration.
        if (!UpdateGas(callGas, ref gasAvailable)) return EvmExceptionType.OutOfGas;

        ReadOnlyMemory<byte> callData = vmState.Memory.Load(in dataOffset, dataLength);

        Snapshot snapshot = _state.TakeSnapshot();
        _state.SubtractFromBalance(caller, callValue, _spec);

        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: caller,
            codeSource: codeSource,
            executingAccount: target,
            transferValue: callValue,
            value: callValue,
            inputData: callData,
            codeInfo: targetCodeInfo
        );
        if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Tx call gas {callGas}");

        ExecutionType executionType = GetCallExecutionType(instruction, env.IsPostMerge());
        returnData = new EvmState(
            callGas,
            callEnv,
            executionType,
            isTopLevel: false,
            snapshot,
            (long)0,
            (long)0,
            isStatic: instruction == Instruction.EXTSTATICCALL || vmState.IsStatic,
            vmState,
            isContinuation: false,
            isCreateOnPreExistingAccount: false);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private static EvmExceptionType InstructionRevert(EvmState vmState, ref EvmStack stack, ref long gasAvailable, out object returnData)
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
    private static EvmExceptionType InstructionReturn(EvmState vmState, ref EvmStack stack, ref long gasAvailable, out object returnData)
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
    private EvmExceptionType InstructionSelfDestruct(IEvm vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        Metrics.SelfDestructs++;

        EvmState vmState = vm.State;

        if (vmState.IsStatic) return EvmExceptionType.StaticCallViolation;

        if (_spec.UseShanghaiDDosProtection)
        {
            gasAvailable -= GasCostOf.SelfDestructEip150;
        }

        Address inheritor = stack.PopAddress();
        if (inheritor is null) return EvmExceptionType.StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vmState, inheritor, false)) return EvmExceptionType.OutOfGas;

        Address executingAccount = vmState.Env.ExecutingAccount;
        bool createInSameTx = vmState.CreateList.Contains(executingAccount);
        if (!_spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
            vmState.DestroyList.Add(executingAccount);

        UInt256 result = _state.GetBalance(executingAccount);
        if (_txTracer.IsTracingActions) _txTracer.ReportSelfDestruct(executingAccount, result, inheritor);
        if (_spec.ClearEmptyAccountWhenTouched && !result.IsZero && _state.IsDeadAccount(inheritor))
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        bool inheritorAccountExists = _state.AccountExists(inheritor);
        if (!_spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && _spec.UseShanghaiDDosProtection)
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable)) return EvmExceptionType.OutOfGas;
        }

        if (!inheritorAccountExists)
        {
            _state.CreateAccount(inheritor, result);
        }
        else if (!inheritor.Equals(executingAccount))
        {
            _state.AddToBalance(inheritor, result, _spec);
        }

        if (_spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
            return EvmExceptionType.None; // don't burn eth when contract is not destroyed per EIP clarification

        _state.SubtractFromBalance(executingAccount, result, _spec);
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private (EvmExceptionType exceptionType, EvmState? callState) InstructionEofCreate(EvmState vmState, ref int pc, ref ReadOnlySpan<byte> codeSection, ref EvmStack stack, ref long gasAvailable, Instruction instruction)
    {
        ref readonly ExecutionEnvironment env = ref vmState.Env;
        EofCodeInfo container = env.CodeInfo as EofCodeInfo;
        var currentContext = ExecutionType.EOFCREATE;

        // 1 - deduct TX_CREATE_COST gas
        if (!UpdateGas(GasCostOf.TxCreate, ref gasAvailable))
            return (EvmExceptionType.OutOfGas, null);

        // 2 - read immediate operand initcontainer_index, encoded as 8-bit unsigned value
        int initcontainerIndex = codeSection[pc++];

        // 3 - pop value, salt, input_offset, input_size from the operand stack
        // no stack checks becaue EOF guarantees no stack undeflows
        stack.PopUInt256(out UInt256 value);
        stack.PopWord256(out Span<byte> salt);
        stack.PopUInt256(out UInt256 dataOffset);
        stack.PopUInt256(out UInt256 dataSize);

        // 4 - perform (and charge for) memory expansion using [input_offset, input_size]
        if (!UpdateMemoryCost(vmState, ref gasAvailable, in dataOffset, dataSize)) return (EvmExceptionType.OutOfGas, null);

        // 5 - load initcode EOF subcontainer at initcontainer_index in the container from which EOFCREATE is executed
        // let initcontainer be that EOF container, and initcontainer_size its length in bytes declared in its parent container header
        ReadOnlySpan<byte> initContainer = container.ContainerSection.Span[(Range)container.ContainerSectionOffset(initcontainerIndex).Value];
        // Eip3860
        if (_spec.IsEip3860Enabled)
        {
            //if (!UpdateGas(GasCostOf.InitCodeWord * numberOfWordInInitcode, ref gasAvailable))
            //    return (EvmExceptionType.OutOfGas, null);
            if (initContainer.Length > _spec.MaxInitCodeSize) return (EvmExceptionType.OutOfGas, null);
        }

        // 6 - deduct GAS_KECCAK256_WORD * ((initcontainer_size + 31) // 32) gas (hashing charge)
        long numberOfWordsInInitCode = EvmPooledMemory.Div32Ceiling((UInt256)initContainer.Length);
        long hashCost = GasCostOf.Sha3Word * numberOfWordsInInitCode;
        if (!UpdateGas(hashCost, ref gasAvailable))
            return (EvmExceptionType.OutOfGas, null);

        // 7 - check that current call depth is below STACK_DEPTH_LIMIT and that caller balance is enough to transfer value
        // in case of failure return 0 on the stack, caller’s nonce is not updated and gas for initcode execution is not consumed.
        UInt256 balance = _state.GetBalance(env.ExecutingAccount);
        if (env.CallDepth >= MaxCallDepth || value > balance)
        {
            // TODO: need a test for this
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        // 8 - caller’s memory slice [input_offset:input_size] is used as calldata
        Span<byte> calldata = vmState.Memory.LoadSpan(dataOffset, dataSize);

        // 9 - execute the container and deduct gas for execution. The 63/64th rule from EIP-150 applies.
        long callGas = _spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!UpdateGas(callGas, ref gasAvailable)) return (EvmExceptionType.OutOfGas, null);

        // 10 - increment sender account’s nonce
        UInt256 accountNonce = _state.GetNonce(env.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }
        _state.IncrementNonce(env.ExecutingAccount);

        // 11 - calculate new_address as keccak256(0xff || sender || salt || keccak256(initcontainer))[12:]
        Address contractAddress = ContractAddress.From(env.ExecutingAccount, salt, initContainer);
        if (_spec.UseHotAndColdStorage)
        {
            // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
            vmState.WarmUp(contractAddress);
        }


        if (_txTracer.IsTracingInstructions) EndInstructionTrace(gasAvailable, vmState?.Memory.Size ?? 0);
        // todo: === below is a new call - refactor / move

        Snapshot snapshot = _state.TakeSnapshot();

        bool accountExists = _state.AccountExists(contractAddress);

        if (accountExists && contractAddress.IsNonZeroAccount(_spec, _codeInfoRepository, _state))
        {
            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
            if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Contract collision at {contractAddress}");
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        if (_state.IsDeadAccount(contractAddress))
        {
            _state.ClearStorage(contractAddress);
        }

        _state.SubtractFromBalance(env.ExecutingAccount, value, _spec);


        ICodeInfo codeinfo = CodeInfoFactory.CreateCodeInfo(initContainer.ToArray(), _spec, EvmObjectFormat.ValidationStrategy.ExractHeader);

        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: env.ExecutingAccount,
            executingAccount: contractAddress,
            codeSource: null,
            codeInfo: codeinfo,
            inputData: calldata.ToArray(),
            transferValue: value,
            value: value
        );
        EvmState callState = new(
            callGas,
            callEnv,
            currentContext,
            false,
            snapshot,
            0L,
            0L,
            vmState.IsStatic,
            vmState,
            false,
            accountExists);

        return (EvmExceptionType.None, callState);
    }

    [SkipLocalsInit]
    private (EvmExceptionType exceptionType, EvmState? callState) InstructionCreate(EvmState vmState, ref EvmStack stack, ref long gasAvailable, Instruction instruction)
    {
        ref readonly ExecutionEnvironment env = ref vmState.Env;

        // TODO: happens in CREATE_empty000CreateInitCode_Transaction but probably has to be handled differently
        if (!_state.AccountExists(env.ExecutingAccount))
        {
            _state.CreateAccount(env.ExecutingAccount, UInt256.Zero);
        }

        if (!stack.PopUInt256(out UInt256 value) ||
            !stack.PopUInt256(out UInt256 memoryPositionOfInitCode) ||
            !stack.PopUInt256(out UInt256 initCodeLength))
            return (EvmExceptionType.StackUnderflow, null);

        Span<byte> salt = default;
        if (instruction == Instruction.CREATE2)
        {
            salt = stack.PopWord256();
        }

        //EIP-3860
        if (_spec.IsEip3860Enabled)
        {
            if (initCodeLength > _spec.MaxInitCodeSize) return (EvmExceptionType.OutOfGas, null);
        }

        long gasCost = GasCostOf.Create +
                       (_spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmPooledMemory.Div32Ceiling(initCodeLength) : 0) +
                       (instruction == Instruction.CREATE2
                           ? GasCostOf.Sha3Word * EvmPooledMemory.Div32Ceiling(initCodeLength)
                           : 0);

        if (!UpdateGas(gasCost, ref gasAvailable)) return (EvmExceptionType.OutOfGas, null);

        if (!UpdateMemoryCost(vmState, ref gasAvailable, in memoryPositionOfInitCode, initCodeLength)) return (EvmExceptionType.OutOfGas, null);

        // TODO: copy pasted from CALL / DELEGATECALL, need to move it outside?
        if (env.CallDepth >= MaxCallDepth) // TODO: fragile ordering / potential vulnerability for different clients
        {
            // TODO: need a test for this
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        ReadOnlyMemory<byte> initCode = vmState.Memory.Load(in memoryPositionOfInitCode, initCodeLength);

        UInt256 balance = _state.GetBalance(env.ExecutingAccount);
        if (value > balance)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        UInt256 accountNonce = _state.GetNonce(env.ExecutingAccount);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        if (_txTracer.IsTracingInstructions) EndInstructionTrace(gasAvailable, vmState.Memory.Size);
        // todo: === below is a new call - refactor / move

        long callGas = _spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!UpdateGas(callGas, ref gasAvailable)) return (EvmExceptionType.OutOfGas, null);

        Address contractAddress = instruction == Instruction.CREATE
            ? ContractAddress.From(env.ExecutingAccount, _state.GetNonce(env.ExecutingAccount))
            : ContractAddress.From(env.ExecutingAccount, salt, initCode.Span);

        if (_spec.UseHotAndColdStorage)
        {
            // EIP-2929 assumes that warm-up cost is included in the costs of CREATE and CREATE2
            vmState.WarmUp(contractAddress);
        }

        // Do not add the initCode to the cache as it is
        // pointing to data in this tx and will become invalid
        // for another tx as returned to pool.
        if (_spec.IsEofEnabled && initCode.Span.StartsWith(EvmObjectFormat.MAGIC))
        {
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            UpdateGasUp(callGas, ref gasAvailable);
            return (EvmExceptionType.None, null);
        }

        _state.IncrementNonce(env.ExecutingAccount);

        CodeInfoFactory.CreateInitCodeInfo(initCode.ToArray(), _spec, out ICodeInfo codeinfo, out _);
        codeinfo.AnalyseInBackgroundIfRequired();

        Snapshot snapshot = _state.TakeSnapshot();

        bool accountExists = _state.AccountExists(contractAddress);

        if (accountExists && contractAddress.IsNonZeroAccount(_spec, _codeInfoRepository, _state))
        {
            /* we get the snapshot before this as there is a possibility with that we will touch an empty account and remove it even if the REVERT operation follows */
            if (typeof(TLogger) == typeof(IsTracing)) _logger.Trace($"Contract collision at {contractAddress}");
            _returnDataBuffer = Array.Empty<byte>();
            stack.PushZero();
            return (EvmExceptionType.None, null);
        }

        if (_state.IsDeadAccount(contractAddress))
        {
            _state.ClearStorage(contractAddress);
        }

        _state.SubtractFromBalance(env.ExecutingAccount, value, _spec);

        ExecutionEnvironment callEnv = new
        (
            txExecutionContext: in env.TxExecutionContext,
            callDepth: env.CallDepth + 1,
            caller: env.ExecutingAccount,
            executingAccount: contractAddress,
            codeSource: null,
            codeInfo: codeinfo,
            inputData: default,
            transferValue: value,
            value: value
        );
        EvmState callState = new(
            callGas,
            callEnv,
            instruction switch
            {
                Instruction.CREATE => ExecutionType.CREATE,
                Instruction.CREATE2 => ExecutionType.CREATE2,
                _ => throw new UnreachableException()
            },
            false,
            snapshot,
            0L,
            0L,
            vmState.IsStatic,
            vmState,
            false,
            accountExists);

        return (EvmExceptionType.None, callState);
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
            EvmExceptionType.StackUnderflow => CallResult.StackUnderflowException,
            EvmExceptionType.InvalidJumpDestination => CallResult.InvalidJumpDestination,
            EvmExceptionType.AccessViolation => CallResult.AccessViolationException,
            EvmExceptionType.AddressOutOfRange => CallResult.InvalidAddressRange,
            _ => throw new ArgumentOutOfRangeException(nameof(exceptionType), exceptionType, "")
        };
    }

    private static void UpdateCurrentState(EvmState state, int pc, long gas, int stackHead, int sectionId)
    {
        state.ProgramCounter = pc;
        state.GasAvailable = gas;
        state.DataStackHead = stackHead;
        state.FunctionIndex = sectionId;
    }

    private static bool UpdateMemoryCost(EvmState vmState, ref long gasAvailable, in UInt256 position, in UInt256 length)
    {
        long memoryCost = vmState.Memory.CalculateMemoryCost(in position, length);
        if (memoryCost != 0L)
        {
            if (!UpdateGas(memoryCost, ref gasAvailable))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Jump(CodeInfo codeinfo, in UInt256 jumpDest, ref int programCounter, in ExecutionEnvironment env)
    {
        if (jumpDest > int.MaxValue)
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 0xf435a354924097686ea88dab3aac1dd464e6a3b387c77aeee94145b0fa5a63d2 mainnet
            return false;
        }

        int jumpDestInt = (int)jumpDest;
        if (!codeinfo.ValidateJump(jumpDestInt))
        {
            // https://github.com/NethermindEth/nethermind/issues/140
            // TODO: add a test, validating inside the condition was not covered by existing tests and fails on 61363 Ropsten
            return false;
        }

        programCounter = jumpDestInt;
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void StartInstructionTrace(Instruction instruction, EvmState vmState, long gasAvailable, int programCounter, in EvmStack stackValue)
    {
        _txTracer.StartOperation(programCounter, instruction, gasAvailable, vmState.Env);
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
    private void EndInstructionTrace(long gasAvailable, ulong memorySize)
    {
        _txTracer.ReportOperationRemainingGas(gasAvailable);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EndInstructionTraceError(long gasAvailable, EvmExceptionType evmExceptionType)
    {
        _txTracer.ReportOperationRemainingGas(gasAvailable);
        _txTracer.ReportOperationError(evmExceptionType);
    }

    private static ExecutionType GetCallExecutionType(Instruction instruction, bool isPostMerge = false)
        => instruction switch
        {
            Instruction.CALL => ExecutionType.CALL,
            Instruction.DELEGATECALL => ExecutionType.DELEGATECALL,
            Instruction.STATICCALL => ExecutionType.STATICCALL,
            Instruction.CALLCODE => ExecutionType.CALLCODE,
            Instruction.EXTCALL => ExecutionType.EOFCALL,
            Instruction.EXTDELEGATECALL => ExecutionType.EOFDELEGATECALL,
            Instruction.EXTSTATICCALL => ExecutionType.EOFSTATICCALL,
            _ => throw new NotSupportedException($"Execution type is undefined for {instruction.GetName(isPostMerge)}")
        };
}
