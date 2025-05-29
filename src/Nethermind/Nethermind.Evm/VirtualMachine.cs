// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat.Handlers;
using Nethermind.Evm.Precompiles;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.State;

using static Nethermind.Evm.EvmObjectFormat.EofValidator;

#if DEBUG
using Nethermind.Evm.Tracing.Debugger;
#endif

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]
namespace Nethermind.Evm;

using Word = Vector256<byte>;
using unsafe OpCode = delegate*<VirtualMachine, ref EvmStack, ref long, ref int, EvmExceptionType>;
using Int256;

public sealed unsafe partial class VirtualMachine(
    IBlockhashProvider? blockHashProvider,
    ISpecProvider? specProvider,
    ILogManager? logManager) : IVirtualMachine
{
    public const int MaxCallDepth = Eof1.RETURN_STACK_MAX_HEIGHT;
    private readonly static UInt256 P255Int = (UInt256)System.Numerics.BigInteger.Pow(2, 255);
    internal readonly static byte[] EofHash256 = KeccakHash.ComputeHashBytes(MAGIC);
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

    private readonly ValueHash256 _chainId = ((UInt256)specProvider.ChainId).ToValueHash();

    private readonly IBlockhashProvider _blockHashProvider = blockHashProvider ?? throw new ArgumentNullException(nameof(blockHashProvider));
    private readonly ISpecProvider _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
    private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    private readonly Stack<EvmState> _stateStack = new();

    private IWorldState _worldState = null!;
    private (Address Address, bool ShouldDelete) _parityTouchBugAccount = (Address.FromNumber(3), false);
    private ITxTracer _txTracer = NullTxTracer.Instance;
    private IReleaseSpec _spec;

    private ICodeInfoRepository _codeInfoRepository;

    private OpCode[] _opcodeMethods;
    private static long _txCount;

    private EvmState _currentState;
    private ReadOnlyMemory<byte>? _previousCallResult;
    private UInt256 _previousCallOutputDestination;

    public ICodeInfoRepository CodeInfoRepository => _codeInfoRepository;
    public IReleaseSpec Spec => _spec;
    public ITxTracer TxTracer => _txTracer;
    public IWorldState WorldState => _worldState;
    public ref readonly ValueHash256 ChainId => ref _chainId;
    public ReadOnlyMemory<byte> ReturnDataBuffer { get; set; } = Array.Empty<byte>();
    public object ReturnData { get; set; }
    public IBlockhashProvider BlockHashProvider => _blockHashProvider;

    private EvmState _vmState;
    public EvmState EvmState { get => _vmState; private set => _vmState = value; }
    public int SectionIndex { get; set; }

    /// <summary>
    /// Executes a transaction by iteratively processing call frames until a top-level call returns
    /// or a failure condition is reached. This method handles both precompiled contracts and regular
    /// EVM calls, along with proper state management, tracing, and error handling.
    /// </summary>
    /// <typeparam name="TTracingInst">
    /// The type of tracing instructions flag used to conditionally trace execution actions.
    /// </typeparam>
    /// <param name="evmState">The initial EVM state to begin transaction execution.</param>
    /// <param name="worldState">The current world state that may be modified during execution.</param>
    /// <param name="txTracer">An object used to record execution details and trace transaction actions.</param>
    /// <returns>
    /// A <see cref="TransactionSubstate"/> representing the final state of the transaction execution.
    /// </returns>
    /// <exception cref="EvmException">
    /// Thrown when an EVM-specific error occurs during execution.
    /// </exception>
    public TransactionSubstate ExecuteTransaction<TTracingInst>(
        EvmState evmState,
        IWorldState worldState,
        ITxTracer txTracer)
        where TTracingInst : struct, IFlag
    {
        // Initialize dependencies for transaction tracing and state access.
        _txTracer = txTracer;
        _worldState = worldState;

        // Extract the transaction execution context from the EVM environment.
        ref readonly TxExecutionContext txExecutionContext = ref evmState.Env.TxExecutionContext;

        // Prepare the specification and opcode mapping based on the current block header.
        IReleaseSpec spec = PrepareSpecAndOpcodes<TTracingInst>(
            txExecutionContext.BlockExecutionContext.Header);

        // Initialize the code repository and set up the initial execution state.
        _codeInfoRepository = txExecutionContext.CodeInfoRepository;
        _currentState = evmState;
        _previousCallResult = null;
        _previousCallOutputDestination = UInt256.Zero;
        ZeroPaddedSpan previousCallOutput = ZeroPaddedSpan.Empty;

        // Main execution loop: processes call frames until the top-level transaction completes.
        while (true)
        {
            // For non-continuation frames, clear any previously stored return data.
            if (!_currentState.IsContinuation)
            {
                ReturnDataBuffer = Array.Empty<byte>();
            }

            Exception? failure;
            try
            {
                CallResult callResult;

                // If the current state represents a precompiled contract, handle it separately.
                if (_currentState.IsPrecompile)
                {
                    callResult = ExecutePrecompile(_currentState, _txTracer.IsTracingActions, out failure);
                    if (failure is not null)
                    {
                        // Jump to the failure handler if a precompile error occurred.
                        goto Failure;
                    }
                }
                else
                {
                    // Start transaction tracing for non-continuation frames if tracing is enabled.
                    if (_txTracer.IsTracingActions && !_currentState.IsContinuation)
                    {
                        TraceTransactionActionStart(_currentState);
                    }

                    // Execute the regular EVM call if valid code is present; otherwise, mark as invalid.
                    if (_currentState.Env.CodeInfo is not null)
                    {
                        callResult = ExecuteCall<TTracingInst>(
                            _currentState,
                            _previousCallResult,
                            previousCallOutput,
                            _previousCallOutputDestination);
                    }
                    else
                    {
                        callResult = CallResult.InvalidCodeException;
                    }

                    // If the call did not finish with a return, set up the next call frame and continue.
                    if (!callResult.IsReturn)
                    {
                        PrepareNextCallFrame(in callResult, ref previousCallOutput);
                        continue;
                    }

                    // Handle exceptions raised during the call execution.
                    if (callResult.IsException)
                    {
                        TransactionSubstate? substate = HandleException(in callResult, ref previousCallOutput);
                        if (substate is not null)
                        {
                            return substate;
                        }
                        // Continue execution if the exception did not immediately finalize the transaction.
                        continue;
                    }
                }

                // If the current execution state is the top-level call, finalize tracing and return the result.
                if (_currentState.IsTopLevel)
                {
                    if (_txTracer.IsTracingActions)
                    {
                        TraceTransactionActionEnd(_currentState, spec, callResult);
                    }
                    return PrepareTopLevelSubstate(in callResult);
                }

                // For nested call frames, merge the results and restore the previous execution state.
                using (EvmState previousState = _currentState)
                {
                    // Restore the previous state from the stack and mark it as a continuation.
                    _currentState = _stateStack.Pop();
                    _currentState.IsContinuation = true;
                    // Refund the remaining gas from the completed call frame.
                    _currentState.GasAvailable += previousState.GasAvailable;
                    bool previousStateSucceeded = true;

                    if (!callResult.ShouldRevert)
                    {
                        long gasAvailableForCodeDeposit = previousState.GasAvailable;

                        // Process contract creation calls differently from regular calls.
                        if (previousState.ExecutionType.IsAnyCreate())
                        {
                            PrepareCreateData(previousState, ref previousCallOutput);
                            if (previousState.ExecutionType.IsAnyCreateLegacy())
                            {
                                HandleLegacyCreate(
                                    in callResult,
                                    previousState,
                                    gasAvailableForCodeDeposit,
                                    spec,
                                    ref previousStateSucceeded);
                            }
                            else if (previousState.ExecutionType.IsAnyCreateEof())
                            {
                                HandleEofCreate(
                                    in callResult,
                                    previousState,
                                    gasAvailableForCodeDeposit,
                                    spec,
                                    ref previousStateSucceeded);
                            }
                        }
                        else
                        {
                            // Process a standard call return.
                            previousCallOutput = HandleRegularReturn<TTracingInst>(in callResult, previousState);
                        }

                        // Commit the changes from the completed call frame if execution was successful.
                        if (previousStateSucceeded)
                        {
                            previousState.CommitToParent(_currentState);
                        }
                    }
                    else
                    {
                        // Revert state changes for the previous call frame when a revert condition is signaled.
                        HandleRevert(previousState, callResult, ref previousCallOutput);
                    }
                }
            }
            // Handle specific EVM or overflow exceptions by routing to the failure handling block.
            catch (Exception ex) when (ex is EvmException or OverflowException)
            {
                failure = ex;
                goto Failure;
            }

            // Continue with the next iteration of the execution loop.
            continue;

        // Failure handling: attempts to process and possibly finalize the transaction after an error.
        Failure:
            TransactionSubstate? failSubstate = HandleFailure<TTracingInst>(failure, ref previousCallOutput);
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
        ReturnDataBuffer = Array.Empty<byte>();
        previousCallOutput = ZeroPaddedSpan.Empty;
    }

    private ZeroPaddedSpan HandleRegularReturn<TTracingInst>(scoped in CallResult callResult, EvmState previousState)
        where TTracingInst : struct, IFlag
    {
        ZeroPaddedSpan previousCallOutput;
        ReturnDataBuffer = callResult.Output.Bytes;
        _previousCallResult = previousState.ExecutionType.IsAnyCallEof() ? EofStatusCode.SuccessBytes :
            callResult.PrecompileSuccess.HasValue
            ? (callResult.PrecompileSuccess.Value ? StatusCode.SuccessBytes : StatusCode.FailureBytes)
            : StatusCode.SuccessBytes;
        previousCallOutput = callResult.Output.Bytes.Span.SliceWithZeroPadding(0, Math.Min(callResult.Output.Bytes.Length, (int)previousState.OutputLength));
        _previousCallOutputDestination = (ulong)previousState.OutputDestination;
        if (previousState.IsPrecompile)
        {
            // parity induced if else for vmtrace
            if (TTracingInst.IsActive)
            {
                _txTracer.ReportMemoryChange(_previousCallOutputDestination, previousCallOutput);
            }
        }

        if (_txTracer.IsTracingActions)
        {
            _txTracer.ReportActionEnd(previousState.GasAvailable, ReturnDataBuffer);
        }

        return previousCallOutput;
    }

    private void HandleEofCreate(in CallResult callResult, EvmState previousState, long gasAvailableForCodeDeposit, IReleaseSpec spec, ref bool previousStateSucceeded)
    {
        Address callCodeOwner = previousState.Env.ExecutingAccount;
        // ReturnCode was called with a container index and auxdata
        // 1 - load deploy EOF subcontainer at deploy_container_index in the container from which RETURNCODE is executed
        ReadOnlySpan<byte> auxExtraData = callResult.Output.Bytes.Span;
        EofCodeInfo deployCodeInfo = (EofCodeInfo)callResult.Output.Container;

        // 2 - concatenate data section with (aux_data_offset, aux_data_offset + aux_data_size) memory segment and update data size in the header
        Span<byte> bytecodeResult = new byte[deployCodeInfo.MachineCode.Length + auxExtraData.Length];
        // 2 - 1 - 1 - copy old container
        deployCodeInfo.MachineCode.Span.CopyTo(bytecodeResult);
        // 2 - 1 - 2 - copy aux data to dataSection
        auxExtraData.CopyTo(bytecodeResult[deployCodeInfo.MachineCode.Length..]);

        // 2 - 2 - update data section size in the header u16
        int dataSubHeaderSectionStart =
            // magic + version
            VERSION_OFFSET
            // type section : (1 byte of separator + 2 bytes for size)
            + Eof1.MINIMUM_HEADER_SECTION_SIZE
            // code section :  (1 byte of separator + (CodeSections count) * 2 bytes for size)
            + ONE_BYTE_LENGTH
            + TWO_BYTE_LENGTH
            + TWO_BYTE_LENGTH * deployCodeInfo.EofContainer.Header.CodeSections.Count
            // container section
            + (deployCodeInfo.EofContainer.Header.ContainerSections is null
                // bytes if no container section is available
                ? 0
                // 1 byte of separator + (ContainerSections count) * 2 bytes for size
                : ONE_BYTE_LENGTH + TWO_BYTE_LENGTH + TWO_BYTE_LENGTH * deployCodeInfo.EofContainer.Header.ContainerSections.Value.Count)
            // data section separator
            + ONE_BYTE_LENGTH;

        ushort dataSize = (ushort)(deployCodeInfo.DataSection.Length + auxExtraData.Length);
        bytecodeResult[dataSubHeaderSectionStart + 1] = (byte)(dataSize >> 8);
        bytecodeResult[dataSubHeaderSectionStart + 2] = (byte)(dataSize & 0xFF);

        byte[] bytecodeResultArray = bytecodeResult.ToArray();

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

    /// <summary>
    /// Handles the code deposit for a legacy contract creation operation.
    /// This method calculates the gas cost for depositing the contract code using legacy rules,
    /// validates the code, and either deposits the code or reverts the world state if the deposit fails.
    /// </summary>
    /// <param name="callResult">
    /// The result of the contract creation call, which includes the output code intended for deposit.
    /// </param>
    /// <param name="previousState">
    /// The EVM state prior to the current call frame. It provides access to the snapshot, the executing account,
    /// and flags indicating if the account pre-existed.
    /// </param>
    /// <param name="gasAvailableForCodeDeposit">
    /// The amount of gas available for covering the cost of code deposit.
    /// </param>
    /// <param name="spec">
    /// The release specification containing the rules and parameters that affect code deposit behavior.
    /// </param>
    /// <param name="previousStateSucceeded">
    /// A reference flag indicating whether the previous call frame executed successfully. This flag is set to false if the deposit fails.
    /// </param>
    private void HandleLegacyCreate(
        in CallResult callResult,
        EvmState previousState,
        long gasAvailableForCodeDeposit,
        IReleaseSpec spec,
        ref bool previousStateSucceeded)
    {
        // Cache whether transaction tracing is enabled to avoid multiple property lookups.
        bool isTracing = _txTracer.IsTracingActions;

        // Get the address of the account that initiated the contract creation.
        Address callCodeOwner = previousState.Env.ExecutingAccount;

        // Calculate the gas cost required to deposit the contract code using legacy cost rules.
        long codeDepositGasCost = CodeDepositHandler.CalculateCost(spec, callResult.Output.Bytes.Length);

        // Validate the code against legacy rules; mark it invalid if it fails these checks.
        bool invalidCode = !CodeDepositHandler.IsValidWithLegacyRules(spec, callResult.Output.Bytes);

        // Check if there is sufficient gas and the code is valid.
        if (gasAvailableForCodeDeposit >= codeDepositGasCost && !invalidCode)
        {
            // Deposit the contract code into the repository.
            ReadOnlyMemory<byte> code = callResult.Output.Bytes;
            _codeInfoRepository.InsertCode(_worldState, code, callCodeOwner, spec);

            // Deduct the gas cost for the code deposit from the current state's available gas.
            _currentState.GasAvailable -= codeDepositGasCost;

            // If tracing is enabled, report the successful code deposit operation.
            if (isTracing)
            {
                _txTracer.ReportActionEnd(previousState.GasAvailable - codeDepositGasCost, callCodeOwner, callResult.Output.Bytes);
            }
        }
        // If the code deposit should fail due to out-of-gas or invalid code conditions...
        else if (spec.FailOnOutOfGasCodeDeposit || invalidCode)
        {
            // Consume all remaining gas allocated for the code deposit.
            _currentState.GasAvailable -= gasAvailableForCodeDeposit;

            // Roll back the world state to its snapshot from before the creation attempt.
            _worldState.Restore(previousState.Snapshot);

            // If the contract creation did not target a pre-existing account, delete the account.
            if (!previousState.IsCreateOnPreExistingAccount)
            {
                _worldState.DeleteAccount(callCodeOwner);
            }

            // Reset the previous call result to indicate that no valid code was deployed.
            _previousCallResult = BytesZero;

            // Mark the previous state execution as failed.
            previousStateSucceeded = false;

            // Report an error via the tracer, indicating whether the failure was due to invalid code or gas exhaustion.
            if (isTracing)
            {
                _txTracer.ReportActionError(invalidCode ? EvmExceptionType.InvalidCode : EvmExceptionType.OutOfGas);
            }
        }
        // In scenarios where the code deposit does not strictly mandate a failure,
        // report the end of the action if tracing is enabled.
        else if (isTracing)
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

    /// <summary>
    /// Reverts the state changes made during the execution of a call frame.
    /// This method restores the world state to a previous snapshot, sets appropriate
    /// failure indicators and output data, and reports the revert action via the tracer.
    /// </summary>
    /// <param name="previousState">
    /// The EVM state prior to the current call, which contains the snapshot for restoration,
    /// output length, output destination, and execution type.
    /// </param>
    /// <param name="callResult">
    /// The result of the call that triggered the revert, containing output data and flags
    /// indicating precompile success.
    /// </param>
    /// <param name="previousCallOutput">
    /// A reference to the output data buffer that will be updated with the reverted call's output,
    /// padded to match the expected length.
    /// </param>
    private void HandleRevert(EvmState previousState, in CallResult callResult, ref ZeroPaddedSpan previousCallOutput)
    {
        // Restore the world state to the snapshot taken before the execution of the call.
        _worldState.Restore(previousState.Snapshot);

        // Cache the output bytes from the call result to avoid multiple property accesses.
        ReadOnlyMemory<byte> outputBytes = callResult.Output.Bytes;

        // Set the return data buffer to the output bytes from the failed call.
        ReturnDataBuffer = outputBytes;

        // Determine the appropriate failure status code.
        // For calls with EOF semantics, differentiate between precompile failure and regular revert.
        // Otherwise, use the standard failure status code.
        _previousCallResult = previousState.ExecutionType.IsAnyCallEof()
            ? (callResult.PrecompileSuccess is not null
                ? EofStatusCode.FailureBytes
                : EofStatusCode.RevertBytes)
            : StatusCode.FailureBytes;

        // Slice the output bytes, zero-padding if necessary, to match the expected output length.
        // This ensures that the returned data conforms to the caller's output length expectations.
        previousCallOutput = outputBytes.Span.SliceWithZeroPadding(0, Math.Min(outputBytes.Length, (int)previousState.OutputLength));

        // Record the output destination address for subsequent operations.
        _previousCallOutputDestination = (ulong)previousState.OutputDestination;

        // If transaction tracing is enabled, report the revert action along with the available gas and output bytes.
        if (_txTracer.IsTracingActions)
        {
            _txTracer.ReportActionRevert(previousState.GasAvailable, outputBytes);
        }
    }

    /// <summary>
    /// Handles a failure during transaction execution by restoring the world state,
    /// reporting error details via the tracer, and either finalizing the top-level transaction
    /// or preparing to revert to the parent call frame.
    /// </summary>
    /// <typeparam name="TTracingInst">
    /// A type parameter representing tracing instructions. It must be a struct implementing <see cref="IFlag"/>.
    /// </typeparam>
    /// <param name="failure">The exception that caused the failure during execution.</param>
    /// <param name="previousCallOutput">
    /// A reference to the zero-padded span that holds the previous call's output; it will be reset upon failure.
    /// </param>
    /// <returns>
    /// A <see cref="TransactionSubstate"/> if the failure occurs in the top-level call; otherwise, <c>null</c>
    /// to indicate that execution should continue with the parent call frame.
    /// </returns>
    private TransactionSubstate? HandleFailure<TTracingInst>(Exception failure, ref ZeroPaddedSpan previousCallOutput)
        where TTracingInst : struct, IFlag
    {
        // Log the exception if trace logging is enabled.
        if (_logger.IsTrace)
        {
            _logger.Trace($"exception ({failure.GetType().Name}) in {_currentState.ExecutionType} at depth {_currentState.Env.CallDepth} - restoring snapshot");
        }

        // Revert the world state to the snapshot taken at the start of the current state's execution.
        _worldState.Restore(_currentState.Snapshot);

        // Revert any modifications specific to the Parity touch bug, if applicable.
        RevertParityTouchBugAccount();

        // Cache the transaction tracer for local use.
        ITxTracer txTracer = _txTracer;

        // Attempt to cast the exception to EvmException to extract a specific error type.
        EvmException? evmException = failure as EvmException;
        EvmExceptionType errorType = evmException?.ExceptionType ?? EvmExceptionType.Other;

        // If the tracing instructions flag is active, report zero remaining gas and log the error.
        if (TTracingInst.IsActive)
        {
            txTracer.ReportOperationRemainingGas(0);
            txTracer.ReportOperationError(errorType);
        }

        // If action-level tracing is enabled, report the error associated with the action.
        if (txTracer.IsTracingActions)
        {
            txTracer.ReportActionError(errorType);
        }

        // For a top-level call, immediately return a final transaction substate.
        if (_currentState.IsTopLevel)
        {
            // For an OverflowException, force the error type to a generic Other error.
            EvmExceptionType finalErrorType = failure is OverflowException ? EvmExceptionType.Other : errorType;
            return new TransactionSubstate(finalErrorType, txTracer.IsTracing);
        }

        // For nested call frames, prepare to revert to the parent frame.
        // Set the previous call result to a failure code depending on the call type.
        _previousCallResult = _currentState.ExecutionType.IsAnyCallEof()
            ? EofStatusCode.FailureBytes
            : StatusCode.FailureBytes;

        // Reset output destination and return data.
        _previousCallOutputDestination = UInt256.Zero;
        ReturnDataBuffer = Array.Empty<byte>();
        previousCallOutput = ZeroPaddedSpan.Empty;

        // Dispose of the current failing state and restore the previous call frame from the stack.
        _currentState.Dispose();
        _currentState = _stateStack.Pop();
        _currentState.IsContinuation = true;

        return null;
    }

    /// <summary>
    /// Prepares the execution environment for the next call frame by updating the current state
    /// and resetting relevant output fields.
    /// </summary>
    /// <param name="callResult">
    /// The result object from the current call, which contains the state to be executed next.
    /// </param>
    /// <param name="previousCallOutput">
    /// A reference to the buffer holding the previous call's output, which is cleared in preparation for the new call.
    /// </param>
    private void PrepareNextCallFrame(in CallResult callResult, ref ZeroPaddedSpan previousCallOutput)
    {
        // Push the current execution state onto the state stack so it can be restored later.
        _stateStack.Push(_currentState);

        // Transition to the next call frame's state provided by the call result.
        _currentState = callResult.StateToExecute;

        // Clear the previous call result as the execution context is moving to a new frame.
        _previousCallResult = null;

        // Reset the return data buffer to ensure no residual data persists across call frames.
        ReturnDataBuffer = Array.Empty<byte>();

        // Clear the previous call output, preparing for new output data in the next call frame.
        previousCallOutput = ZeroPaddedSpan.Empty;
    }

    /// <summary>
    /// Handles exceptions that occur during the execution of a call frame by restoring the world state,
    /// reverting known side effects, and either finalizing the transaction (for top-level calls) or
    /// preparing to resume execution in a parent call frame (for nested calls).
    /// </summary>
    /// <param name="callResult">
    /// The result object that contains the exception type and any output data from the failed call.
    /// </param>
    /// <param name="previousCallOutput">
    /// A reference to the zero-padded span that holds the previous call's output, which is reset on exception.
    /// </param>
    /// <returns>
    /// A <see cref="TransactionSubstate"/> instance if the failure occurred in a top-level call,
    /// otherwise <c>null</c> to indicate that execution should continue in the parent frame.
    /// </returns>
    private TransactionSubstate? HandleException(in CallResult callResult, ref ZeroPaddedSpan previousCallOutput)
    {
        // Cache the tracer to minimize repeated field accesses.
        ITxTracer txTracer = _txTracer;

        // Report the error for action-level tracing if enabled.
        if (txTracer.IsTracingActions)
        {
            txTracer.ReportActionError(callResult.ExceptionType);
        }

        // Restore the world state to its snapshot before the current call execution.
        _worldState.Restore(_currentState.Snapshot);

        // Revert any modifications that might have been applied due to the Parity touch bug.
        RevertParityTouchBugAccount();

        // If this is the top-level call, return a final transaction substate encapsulating the error.
        if (_currentState.IsTopLevel)
        {
            return new TransactionSubstate(callResult.ExceptionType, txTracer.IsTracing);
        }

        // For nested calls, mark the previous call result as a failure code based on the call's EOF semantics.
        _previousCallResult = _currentState.ExecutionType.IsAnyCallEof()
            ? EofStatusCode.FailureBytes
            : StatusCode.FailureBytes;

        // Reset output destination and clear return data.
        _previousCallOutputDestination = UInt256.Zero;
        ReturnDataBuffer = Array.Empty<byte>();
        previousCallOutput = ZeroPaddedSpan.Empty;

        // Clean up the current failing state and pop the parent call frame from the state stack.
        _currentState.Dispose();
        _currentState = _stateStack.Pop();
        _currentState.IsContinuation = true;

        // Return null to indicate that the failure was handled and execution should continue in the parent frame.
        return null;
    }

    /// <summary>
    /// Executes a precompiled contract operation based on the current execution state. 
    /// If tracing is enabled, reports the precompile action. It then runs the precompile operation,
    /// checks for failure conditions, and adjusts the execution state accordingly.
    /// </summary>
    /// <param name="currentState">The current EVM state containing execution parameters for the precompile.</param>
    /// <param name="isTracingActions">
    /// A boolean indicating whether detailed tracing actions should be reported during execution.
    /// </param>
    /// <param name="failure">
    /// An output parameter that is set to the encountered exception if the precompile fails; otherwise, <c>null</c>.
    /// </param>
    /// <returns>
    /// A <see cref="CallResult"/> containing the results of the precompile execution. In case of a failure,
    /// returns the default value of <see cref="CallResult"/>.
    /// </returns>
    private CallResult ExecutePrecompile(EvmState currentState, bool isTracingActions, out Exception? failure)
    {
        // Report the precompile action if tracing is enabled.
        if (isTracingActions)
        {
            _txTracer.ReportAction(
                currentState.GasAvailable,
                currentState.Env.Value,
                currentState.From,
                currentState.To,
                currentState.Env.InputData,
                currentState.ExecutionType,
                true);
        }

        // Execute the precompile operation with the current state.
        CallResult callResult = RunPrecompile(currentState);

        // If the precompile did not succeed, handle the failure conditions.
        if (!callResult.PrecompileSuccess.Value)
        {
            // If the failure is due to an exception (e.g., out-of-gas), set the corresponding failure exception.
            if (callResult.IsException)
            {
                failure = PrecompileOutOfGasException;
                goto Failure;
            }

            // If running a precompile on a top-level call frame and it fails, assign a general execution failure.
            if (currentState.IsPrecompile && currentState.IsTopLevel)
            {
                failure = PrecompileExecutionFailureException;
                goto Failure;
            }

            // Otherwise, if no exception but precompile did not succeed, exhaust the remaining gas.
            currentState.GasAvailable = 0;
        }

        // If execution reaches here, the precompile operation is considered successful.
        failure = null;
        return callResult;

    Failure:
        // Return the default CallResult to signal failure, with the failure exception set via the out parameter.
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

    /// <summary>
    /// Prepares the release specification and opcode methods to be used during EVM execution,
    /// based on the provided block header and the tracing instructions flag.
    /// </summary>
    /// <typeparam name="TTracingInst">
    /// A value type implementing <see cref="IFlag"/> that indicates whether tracing-specific opcodes
    /// should be used.
    /// </typeparam>
    /// <param name="header">
    /// The block header containing the block number and timestamp, which are used to select the appropriate release specification.
    /// </param>
    /// <returns>
    /// The prepared <see cref="IReleaseSpec"/> instance, with its associated opcode methods cached for execution.
    /// </returns>
    private IReleaseSpec PrepareSpecAndOpcodes<TTracingInst>(BlockHeader header)
        where TTracingInst : struct, IFlag
    {
        // Retrieve the release specification based on the block's number and timestamp.
        IReleaseSpec spec = _specProvider.GetSpec(header.Number, header.Timestamp);

        // Check if tracing instructions are inactive.
        if (!TTracingInst.IsActive)
        {
            // Occasionally refresh the opcode cache for non-tracing opcodes.
            // The cache is flushed every 10,000 transactions until a threshold of 500,000 transactions.
            // This is to have the function pointers directly point at any PGO optimized methods rather than via pre-stubs
            // May be a few cycles to pick up pointers to the re-Jitted optimized methods depending on what's in the blocks,
            // however the the refreshes don't take long. (re-Jitting doesn't update prior captured function pointers)
            if (_txCount < 500_000 && Interlocked.Increment(ref _txCount) % 10_000 == 0)
            {
                if (_logger.IsDebug)
                {
                    _logger.Debug("Refreshing EVM instruction cache");
                }
                // Regenerate the non-traced opcode set to pick up any updated PGO optimized methods.
                spec.EvmInstructionsNoTrace = EvmInstructions.GenerateOpCodes<TTracingInst>(spec);
            }
            // Ensure the non-traced opcode set is generated and assign it to the _opcodeMethods field.
            _opcodeMethods = (OpCode[])(spec.EvmInstructionsNoTrace ??= EvmInstructions.GenerateOpCodes<TTracingInst>(spec));
        }
        else
        {
            // For tracing-enabled execution, generate (if necessary) and cache the traced opcode set.
            _opcodeMethods = (OpCode[])(spec.EvmInstructionsTraced ??= EvmInstructions.GenerateOpCodes<TTracingInst>(spec));
        }

        // Store the spec in field for future access and return it.
        return (_spec = spec);
    }

    /// <summary>
    /// Reports the final outcome of a transaction action to the transaction tracer, taking into account
    /// various conditions such as exceptions, reverts, and contract creation flows. For contract creation,
    /// the method adjusts the available gas by the code deposit cost and validates the deployed code.
    /// </summary>
    /// <param name="currentState">
    /// The current EVM state, which contains the available gas, execution type, and target address.
    /// </param>
    /// <param name="spec">
    /// The release specification that provides rules and parameters such as code deposit cost and code validation.
    /// </param>
    /// <param name="callResult">
    /// The result of the executed call, including output bytes, exception and revert flags, and additional metadata.
    /// </param>
    private void TraceTransactionActionEnd(EvmState currentState, IReleaseSpec spec, in CallResult callResult)
    {
        // Calculate the gas cost required for depositing the contract code based on the length of the output.
        long codeDepositGasCost = CodeDepositHandler.CalculateCost(spec, callResult.Output.Bytes.Length);

        // Cache the output bytes for reuse in the tracing reports.
        ReadOnlyMemory<byte> outputBytes = callResult.Output.Bytes;

        // If an exception occurred during execution, report the error immediately.
        if (callResult.IsException)
        {
            _txTracer.ReportActionError(callResult.ExceptionType);
        }
        // If the call is set to revert, report a revert action, adjusting the reported gas for creation operations.
        else if (callResult.ShouldRevert)
        {
            // For creation operations, subtract the code deposit cost from the available gas; otherwise, use full gas.
            long reportedGas = currentState.ExecutionType.IsAnyCreate() ? currentState.GasAvailable - codeDepositGasCost : currentState.GasAvailable;
            _txTracer.ReportActionRevert(reportedGas, outputBytes);
        }
        // Process contract creation flows.
        else if (currentState.ExecutionType.IsAnyCreate())
        {
            // If available gas is insufficient to cover the code deposit cost...
            if (currentState.GasAvailable < codeDepositGasCost)
            {
                // When the spec mandates charging for top-level creation, report an out-of-gas error.
                if (spec.ChargeForTopLevelCreate)
                {
                    _txTracer.ReportActionError(EvmExceptionType.OutOfGas);
                }
                // Otherwise, report a successful action end with the remaining gas.
                else
                {
                    _txTracer.ReportActionEnd(currentState.GasAvailable, currentState.To, outputBytes);
                }
            }
            // If the generated code is invalid (e.g., violates EIP-3541 by starting with 0xEF), report an invalid code error.
            else if (CodeDepositHandler.CodeIsInvalid(spec, outputBytes, callResult.FromVersion))
            {
                _txTracer.ReportActionError(EvmExceptionType.InvalidCode);
            }
            // In the successful contract creation case, deduct the code deposit gas cost and report a normal action end.
            else
            {
                _txTracer.ReportActionEnd(currentState.GasAvailable - codeDepositGasCost, currentState.To, outputBytes);
            }
        }
        // For non-creation calls, report the action end using the current available gas and the standard return data.
        else
        {
            _txTracer.ReportActionEnd(currentState.GasAvailable, ReturnDataBuffer);
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

    private CallResult RunPrecompile(EvmState state)
    {
        ReadOnlyMemory<byte> callData = state.Env.InputData;
        UInt256 transferValue = state.Env.TransferValue;
        long gasAvailable = state.GasAvailable;

        IPrecompile precompile = ((PrecompileInfo)state.Env.CodeInfo).Precompile;
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
            CallResult callResult = new(output, precompileSuccess: success, fromVersion: 0, shouldRevert: !success);
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
            CallResult callResult = new(output: default, precompileSuccess: false, fromVersion: 0, shouldRevert: true);
            return callResult;
        }
    }

    /// <summary>
    /// Executes an EVM call by preparing the execution environment, including account balance adjustments,
    /// stack initialization, and memory updates. It then dispatches the bytecode execution using a
    /// specialized interpreter that is optimized at compile time based on the tracing instructions flag.
    /// </summary>
    /// <typeparam name="TTracingInst">
    /// A struct implementing <see cref="IFlag"/> that indicates, via a compile-time constant,
    /// whether tracing-specific opcodes and behavior should be used.
    /// </typeparam>
    /// <param name="vmState">
    /// The current EVM state containing the execution environment, gas, memory, and stack information.
    /// </param>
    /// <param name="previousCallResult">
    /// An optional read-only memory buffer containing the output of a previous call; if provided, its bytes
    /// will be pushed onto the stack for further processing.
    /// </param>
    /// <param name="previousCallOutput">
    /// A zero-padded span containing output from the previous call used for updating the memory state.
    /// </param>
    /// <param name="previousCallOutputDestination">
    /// The memory destination address where the previous call's output should be stored.
    /// </param>
    /// <returns>
    /// A <see cref="CallResult"/> that encapsulates the result of executing the EVM call, including success,
    /// failure, or out-of-gas conditions.
    /// </returns>
    /// <remarks>
    /// The generic struct parameter is used to eliminate runtime if-statements via compile-time evaluation
    /// of <c>TTracingInst.IsActive</c>.
    /// </remarks>
    [SkipLocalsInit]
    private CallResult ExecuteCall<TTracingInst>(
        EvmState vmState,
        ReadOnlyMemory<byte>? previousCallResult,
        ZeroPaddedSpan previousCallOutput,
        scoped in UInt256 previousCallOutputDestination)
        where TTracingInst : struct, IFlag
    {
        // Obtain a reference to the execution environment for convenience.
        ref readonly ExecutionEnvironment env = ref vmState.Env;

        // If this is the first call frame (not a continuation), adjust account balances and nonces.
        if (!vmState.IsContinuation)
        {
            // Ensure the executing account has sufficient balance and exists in the world state.
            _worldState.AddToBalanceAndCreateIfNotExists(env.ExecutingAccount, env.TransferValue, _spec);

            // For contract creation calls, increment the nonce if the specification requires it.
            if (vmState.ExecutionType.IsAnyCreate() && _spec.ClearEmptyAccountWhenTouched)
            {
                _worldState.IncrementNonce(env.ExecutingAccount);
            }
        }

        // If no machine code is present, treat the call as empty.
        if (env.CodeInfo.MachineCode.Length == 0)
        {
            // Increment a metric for empty calls if this is a nested call.
            if (!vmState.IsTopLevel)
            {
                Metrics.IncrementEmptyCalls();
            }
            goto Empty;
        }

        // Initialize the internal stacks for the current call frame.
        vmState.InitializeStacks();

        // Create an EVM stack using the current stack head, tracer, and data stack slice.
        EvmStack stack = new(vmState.DataStackHead, _txTracer, AsAlignedSpan(vmState.DataStack, alignment: EvmStack.WordSize, size: StackPool.StackLength));

        // Cache the available gas from the state for local use.
        long gasAvailable = vmState.GasAvailable;

        // If a previous call result exists, push its bytes onto the stack.
        if (previousCallResult is not null)
        {
            stack.PushBytes<TTracingInst>(previousCallResult.Value.Span);

            // Report the remaining gas if tracing instructions are enabled.
            if (TTracingInst.IsActive)
            {
                _txTracer.ReportOperationRemainingGas(vmState.GasAvailable);
            }
        }

        // If there is previous call output, update the memory cost and save the output.
        if (previousCallOutput.Length > 0)
        {
            // Use a local variable for the destination to simplify passing it by reference.
            UInt256 localPreviousDest = previousCallOutputDestination;

            // Attempt to update the memory cost; if insufficient gas is available, jump to the out-of-gas handler.
            if (!UpdateMemoryCost(vmState, ref gasAvailable, in localPreviousDest, (ulong)previousCallOutput.Length))
            {
                goto OutOfGas;
            }

            // Save the previous call's output into the VM state's memory.
            vmState.Memory.Save(in localPreviousDest, previousCallOutput);
        }

        // Update the global EVM state with the current state.
        EvmState = vmState;

        // Dispatch the bytecode interpreter.
        // The second generic parameter is selected based on whether the transaction tracer is cancelable:
        // - OffFlag is used when cancelation is not needed.
        // - OnFlag is used when cancelation is enabled.
        // This leverages the compile-time evaluation of TTracingInst to optimize away runtime checks.
        return _txTracer.IsCancelable switch
        {
            false => RunByteCode<TTracingInst, OffFlag>(ref stack, gasAvailable),
            true => RunByteCode<TTracingInst, OnFlag>(ref stack, gasAvailable),
        };

    Empty:
        // Return an empty CallResult if there is no machine code to execute.
        return CallResult.Empty(vmState.Env.CodeInfo.Version);

    OutOfGas:
        // Return an out-of-gas CallResult if updating the memory cost fails.
        return CallResult.OutOfGasException;
    }

    /// <summary>
    /// Executes the EVM bytecode by iterating over the instruction set and invoking corresponding opcode methods
    /// via function pointers. The method leverages compile-time evaluation of tracing and cancellation flags to optimize
    /// conditional branches. It also updates the VM state as instructions are executed, handles exceptions,
    /// and returns an appropriate <see cref="CallResult"/>.
    /// </summary>
    /// <typeparam name="TTracingInst">
    /// A struct implementing <see cref="IFlag"/> that indicates at compile time whether tracing-specific logic should be enabled.
    /// </typeparam>
    /// <typeparam name="TCancelable">
    /// A struct implementing <see cref="IFlag"/> that indicates at compile time whether cancellation support is enabled.
    /// </typeparam>
    /// <param name="stack">
    /// A reference to the current EVM stack used for execution.
    /// </param>
    /// <param name="gasAvailable">
    /// The amount of gas available for executing the bytecode.
    /// </param>
    /// <returns>
    /// A <see cref="CallResult"/> that encapsulates the outcome of the execution, which can be a successful result,
    /// an empty result, a revert, or a failure due to an exception (such as out-of-gas).
    /// </returns>
    /// <remarks>
    /// The method uses an unsafe context and function pointers to invoke opcode implementations directly,
    /// which minimizes overhead and allows aggressive inlining and compile-time optimizations.
    /// </remarks>
    [SkipLocalsInit]
    private unsafe CallResult RunByteCode<TTracingInst, TCancelable>(
        scoped ref EvmStack stack,
        long gasAvailable)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        // Reset return data and set the current section index from the VM state.
        ReturnData = null;
        SectionIndex = EvmState.FunctionIndex;

        // Retrieve the code information and create a read-only span of instructions.
        ICodeInfo codeInfo = EvmState.Env.CodeInfo;
        ReadOnlySpan<Instruction> codeSection = GetInstructions(codeInfo);

        // Initialize the exception type to "None".
        EvmExceptionType exceptionType = EvmExceptionType.None;
#if DEBUG
        // In debug mode, retrieve a tracer for interactive debugging.
        DebugTracer? debugger = _txTracer.GetTracer<DebugTracer>();
#endif

        // Set the program counter from the current VM state; it may not be zero if resuming after a call.
        int programCounter = EvmState.ProgramCounter;

        // Pin the opcode methods array to obtain a fixed pointer, avoiding repeated bounds checks and casts.
        // If we don't use a pointer we have to cast for each call (delegate*<...> can't be used as a generic arg)
        // Or have bounds checks (however only 256 opcodes and opcode is a byte so know always in bounds).
        fixed (OpCode* opcodeMethods = &_opcodeMethods[0])
        {
            ref Instruction code = ref MemoryMarshal.GetReference(codeSection);
            // Iterate over the instructions using a while loop because opcodes may modify the program counter.
            while ((uint)programCounter < (uint)codeSection.Length)
            {
#if DEBUG
                // Allow the debugger to inspect and possibly pause execution for debugging purposes.
                debugger?.TryWait(ref _vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
                // Fetch the current instruction from the code section.
                Instruction instruction = Unsafe.Add(ref code, programCounter);

                // If cancellation is enabled and cancellation has been requested, throw an exception.
                if (TCancelable.IsActive && _txTracer.IsCancelled)
                    ThrowOperationCanceledException();

                // If tracing is enabled, start an instruction trace.
                if (TTracingInst.IsActive)
                    StartInstructionTrace(instruction, gasAvailable, programCounter, in stack);

                // Advance the program counter to point to the next instruction.
                programCounter++;

                // For the very common POP opcode, use an inlined implementation to reduce overhead.
                if (Instruction.POP == instruction)
                {
                    exceptionType = EvmInstructions.InstructionPop(this, ref stack, ref gasAvailable, ref programCounter);
                }
                else
                {
                    // Retrieve the opcode function pointer corresponding to the current instruction.
                    OpCode opcodeMethod = opcodeMethods[(int)instruction];
                    // Invoke the opcode method, which may modify the stack, gas, and program counter.
                    // Is executed using fast delegate* via calli (see: C# function pointers https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code#function-pointers)
                    exceptionType = opcodeMethod(this, ref stack, ref gasAvailable, ref programCounter);
                }

                // If gas is exhausted, jump to the out-of-gas handler.
                if (gasAvailable < 0)
                    goto OutOfGas;
                // If an exception occurred, exit the loop.
                if (exceptionType != EvmExceptionType.None)
                    break;

                // If tracing is enabled, complete the trace for the current instruction.
                if (TTracingInst.IsActive)
                    EndInstructionTrace(gasAvailable);

                // If return data has been set, exit the loop to process the returned value.
                if (ReturnData is not null)
                    break;
            }
        }

        // Update the current VM state if no fatal exception occurred, or if the exception is of type Stop or Revert.
        if (exceptionType is EvmExceptionType.None or EvmExceptionType.Stop or EvmExceptionType.Revert)
        {
            // If tracing is enabled, complete the trace for the current instruction.
            if (TTracingInst.IsActive)
                EndInstructionTrace(gasAvailable);
            UpdateCurrentState(programCounter, gasAvailable, stack.Head);
        }
        else
        {
            // For any other exception, jump to the failure handling routine.
            goto ReturnFailure;
        }

        // If the exception indicates a revert, handle it specifically.
        if (exceptionType == EvmExceptionType.Revert)
            goto Revert;
        // If return data was produced, jump to the return data processing block.
        if (ReturnData is not null)
            goto DataReturn;

        // If no return data is produced, return an empty call result.
        return CallResult.Empty(codeInfo.Version);

    DataReturn:
#if DEBUG
        // Allow debugging before processing the return data.
        debugger?.TryWait(ref _vmState, ref programCounter, ref gasAvailable, ref stack.Head);
#endif
        // Process the return data based on its runtime type.
        if (ReturnData is EvmState state)
        {
            return new CallResult(state);
        }
        else if (ReturnData is EofCodeInfo eofCodeInfo)
        {
            return new CallResult(eofCodeInfo, ReturnDataBuffer, null, codeInfo.Version);
        }
        // Fall back to returning a CallResult with a byte array as the return data.
        return new CallResult(null, (byte[])ReturnData, null, codeInfo.Version);

    Revert:
        // Return a CallResult indicating a revert.
        return new CallResult(null, (byte[])ReturnData, null, codeInfo.Version, shouldRevert: true);

    OutOfGas:
        gasAvailable = 0;
        // Set the exception type to OutOfGas if gas has been exhausted.
        exceptionType = EvmExceptionType.OutOfGas;
    ReturnFailure:
        // Return a failure CallResult based on the remaining gas and the exception type.
        return GetFailureReturn(gasAvailable, exceptionType);

        // Converts the code section bytes into a read-only span of instructions.
        // Lightest weight conversion as mostly just helpful when debugging to see what the opcodes are.
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
        EvmState state = EvmState;

        state.ProgramCounter = pc;
        state.GasAvailable = gas;
        state.DataStackHead = stackHead;
        state.FunctionIndex = SectionIndex;
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
        EvmState vmState = EvmState;
        int sectionIndex = SectionIndex;

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
            Memory<byte> stackMemory = AsAlignedMemory(vmState.DataStack, alignment: EvmStack.WordSize, size: StackPool.StackLength).Slice(0, stackValue.Head * EvmStack.WordSize);
            _txTracer.SetOperationStack(new TraceStack(stackMemory));
        }
    }

    private unsafe static int GetAlignmentOffset(byte[] array, uint alignment)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNotEqual(BitOperations.IsPow2(alignment), true, nameof(alignment));

        // The input array should be pinned and we are just using the Pointer to
        // calculate alignment, not using data so not creating memory hole.
        nuint address = (nuint)(byte*)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(array));

        uint mask = alignment - 1;
        // address & mask is misalignment, so (–address) & mask is exactly the adjustment
        uint adjustment = (uint)((-(nint)address) & mask);

        return (int)adjustment;
    }

    private unsafe static Span<byte> AsAlignedSpan(byte[] array, uint alignment, int size)
    {
        int offset = GetAlignmentOffset(array, alignment);
        return array.AsSpan(offset, size);
    }

    private unsafe static Memory<byte> AsAlignedMemory(byte[] array, uint alignment, int size)
    {
        int offset = GetAlignmentOffset(array, alignment);
        return array.AsMemory(offset, size);
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
}
