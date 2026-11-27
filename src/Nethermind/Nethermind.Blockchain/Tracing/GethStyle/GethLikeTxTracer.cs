// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public abstract class GethLikeTxTracer : TxTracer, ITraceImplicitStop, ITraceOperationStorage
{
    private readonly RefundTracker? _refundTracker;

    protected GethLikeTxTracer(GethTraceOptions options, long? destroyRefund = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (destroyRefund.HasValue)
            _refundTracker = new(destroyRefund.Value);

        IsTracingOpLevelStorage = !options.DisableStorage;
        IsTracingStack = !options.DisableStack;
        IsTracingFullMemory = options.EnableMemory;
        IsTracingReturnData = options.EnableReturnData;
        IsTracing = IsTracing || IsTracingFullMemory;
    }

    private GethLikeTxTrace? _trace;
    protected GethLikeTxTrace Trace => _trace ??= CreateTrace();
    protected virtual GethLikeTxTrace CreateTrace() => new();

    protected void ResetTrace() => _trace = null;
    public override bool IsTracingReceipt => true;
    public override bool IsCollectingLogs => false;
    public sealed override bool IsTracingOpLevelStorage { get; protected set; }
    public sealed override bool IsTracingMemory { get; protected set; }
    public override bool IsTracingInstructions => true;
    public sealed override bool IsTracingStack { get; protected set; }
    protected bool IsTracingFullMemory { get; }
    protected long CurrentRefund => _refundTracker?.Refund ?? 0;

    private static readonly JsonEncodedText PrevRandaoJsonName = JsonEncodedText.Encode(nameof(Instruction.PREVRANDAO));

    /// <summary>Whether the traced block is after the merge, where opcode 0x44 is <c>PREVRANDAO</c>.</summary>
    /// <remarks>EIP-4399. The VM selects the value of 0x44 by the same header flag.</remarks>
    internal bool IsPostMerge { get; set; }

    /// <summary>Returns the name of <paramref name="opcode"/> at the fork of the traced block.</summary>
    protected string GetOpcodeName(Instruction opcode) =>
        opcode == Instruction.PREVRANDAO && IsPostMerge ? nameof(Instruction.PREVRANDAO) : OpcodeJsonNames.GetName(opcode);

    /// <inheritdoc cref="GetOpcodeName"/>
    protected JsonEncodedText GetOpcodeJsonName(Instruction opcode) =>
        opcode == Instruction.PREVRANDAO && IsPostMerge ? PrevRandaoJsonName : OpcodeJsonNames.Get(opcode);

    public override void MarkAsSuccess(
        Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null) =>
        Trace.ReturnValue = output;

    public override void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
        Trace.Failed = true;
        Trace.Gas = gasSpent.SpentGas;
        Trace.ReturnValue = output ?? [];
    }

    // Geth reports a failed depth or balance precheck only in the rejected action's exit, not on its opcode.
    protected static bool IsRejectedActionPrecheck(EvmExceptionType error) => error is
        EvmExceptionType.NotEnoughBalance or EvmExceptionType.CallDepthExceeded;

    // No opcode grows the stack by more than one word, so only a full stack overflows, against a limit of 1023.
    private const string StackOverflowError = "stack limit reached 1024 (1023)";

    /// <summary>Returns the step error for a stack that cannot hold the opcode's inputs or growth, or null when it can.</summary>
    /// <remarks>
    /// Geth validates the stack before charging constant gas, so a stack failure outranks running out of gas, which
    /// Nethermind checks first for some opcodes. Before executing DUPN, SWAPN and EXCHANGE, Geth requires one, two and
    /// two items whatever their immediate selects, and room for the item DUPN pushes.
    /// </remarks>
    protected static string? GetStackFailure(Instruction opcode, int stackHead, int stackInputs, int stackGrowth)
    {
        stackInputs = Math.Max(stackInputs, opcode switch
        {
            Instruction.DUPN => 1,
            Instruction.SWAPN or Instruction.EXCHANGE => 2,
            _ => 0
        });
        // Geth also checks that DUPN has room for the item it pushes before executing it.
        if (opcode == Instruction.DUPN)
            stackGrowth = Math.Max(stackGrowth, 1);
        return stackHead < stackInputs ? $"stack underflow ({stackHead} <=> {stackInputs})"
            : stackGrowth > 0 && stackHead >= EvmStack.MaxStackSize - stackGrowth ? StackOverflowError
            : null;
    }

    /// <summary>Returns the immediate a DUPN, SWAPN or EXCHANGE step decodes, or zero for any other opcode.</summary>
    protected static byte ReadEip8024Immediate(Instruction opcode, int pc, in ExecutionEnvironment env)
    {
        if (opcode is not (Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE))
            return 0;

        ReadOnlySpan<byte> code = env.CodeInfo.CodeSpan;
        // As in Geth, a missing immediate reads as zero.
        return (uint)(pc + 1) < (uint)code.Length ? code[pc + 1] : (byte)0;
    }

    /// <summary>Returns the error Geth reports on a step that halts with <paramref name="error"/>.</summary>
    /// <param name="error">The halt.</param>
    /// <param name="opcode">The step's opcode.</param>
    /// <param name="immediate">The step's EIP-8024 immediate, from <see cref="ReadEip8024Immediate"/>.</param>
    /// <param name="stackHead">The stack depth before the step.</param>
    /// <param name="stackFailure">The stack failure <see cref="GetStackFailure"/> found before the step, if any.</param>
    /// <param name="gas">The gas available to the step.</param>
    /// <param name="gasCost">The cost the VM priced before executing the step, if any.</param>
    /// <param name="gasError">The error the VM found while pricing the step, if any.</param>
    /// <remarks>
    /// Geth reports every exceptional halt on the failing step, whether the opcode fails before or while executing,
    /// and checks the stack, then gas, then executes. Nethermind rejects some static-context writes before charging
    /// gas, so such a step that also cannot pay reports running out of gas.
    /// </remarks>
    protected static string? GetStepError(EvmExceptionType error, Instruction opcode, byte immediate, int stackHead,
        string? stackFailure, ulong gas, ulong? gasCost, string? gasError) =>
        stackFailure
        ?? (error == EvmExceptionType.StaticCallViolation && gas < gasCost ? "out of gas" : null)
        ?? gasError
        ?? error switch
        {
            EvmExceptionType.BadInstruction when opcode is Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE =>
                $"invalid opcode: {OpcodeJsonNames.GetName(opcode)} (operand: 0x{immediate:x2})",
            EvmExceptionType.BadInstruction => $"invalid opcode: {OpcodeJsonNames.GetName(opcode)}",
            // Geth gives INVALID no constant cost, so it never runs out of gas there.
            EvmExceptionType.OutOfGas when opcode == Instruction.INVALID => $"invalid opcode: {OpcodeJsonNames.GetName(opcode)}",
            EvmExceptionType.StackOverflow => StackOverflowError,
            EvmExceptionType.StackUnderflow when opcode is Instruction.DUPN or Instruction.SWAPN or Instruction.EXCHANGE =>
                $"stack underflow ({stackHead} <=> {GetEip8024StackDepth(opcode, immediate)})",
            // Geth rejects LOG and TSTORE while executing them; other writes fail in their gas function.
            EvmExceptionType.StaticCallViolation when opcode is not (>= Instruction.LOG0 and <= Instruction.LOG4 or Instruction.TSTORE) =>
                "out of gas: write protection",
            _ => error.GetEvmExceptionDescription()
        };

    /// <summary>Returns the stack depth an EIP-8024 immediate needs, decoded as Geth does.</summary>
    private static int GetEip8024StackDepth(Instruction opcode, byte immediate)
    {
        int single = (immediate + 145) & 0xFF;
        int k = immediate ^ 0x8F;
        int q = k >> 4, r = k & 0x0F;
        return opcode switch
        {
            Instruction.DUPN => single,
            Instruction.SWAPN => single + 1,
            // EXCHANGE swaps items n + 1 and m + 1.
            _ => q < r ? r + 2 : Math.Max(r + 1, 29 - q) + 1
        };
    }

    /// <summary>Returns the gas an opcode used, from the gas left before and after it.</summary>
    /// <param name="gasBefore">The gas left before the opcode.</param>
    /// <param name="gasAfter">The gas left after the opcode.</param>
    /// <param name="executionCost">The execution gas cost that the VM reported for the opcode, if any.</param>
    /// <remarks>
    /// EIP-8037: a step that refills state gas gives back to the gas left state gas that an earlier step took from it,
    /// so the gas left can drop by less than the execution gas cost, or grow. The step then reports its execution gas
    /// cost. Examples are an SSTORE that restores a slot, and a SELFDESTRUCT that empties the balance of an account
    /// that the same transaction created.
    /// </remarks>
    internal static ulong GetGasCost(ulong gasBefore, ulong gasAfter, ulong? executionCost)
    {
        ulong executionGas = executionCost ?? 0;
        return gasAfter <= gasBefore && gasBefore - gasAfter >= executionGas ? gasBefore - gasAfter : executionGas;
    }

    public override void ReportRefund(long refund) => _refundTracker?.Add(refund);

    public override void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) =>
        _refundTracker?.CreditSelfDestruct(address);

    public override void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false)
    {
        base.ReportAction(gas, value, from, to, input, callType, isPrecompileCall);
        _refundTracker?.TakeSnapshot();
    }

    public override void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output)
    {
        base.ReportActionEnd(gas, output);
        _refundTracker?.CommitSnapshot();
    }

    public override void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode)
    {
        base.ReportActionEnd(gas, deploymentAddress, deployedCode);
        _refundTracker?.CommitSnapshot();
    }

    public override void ReportActionRevert(ulong gasLeft, ReadOnlyMemory<byte> output)
    {
        base.ReportActionRevert(gasLeft, output);
        _refundTracker?.RestoreSnapshot();
    }

    public override void ReportActionError(EvmExceptionType evmExceptionType)
    {
        base.ReportActionError(evmExceptionType);
        _refundTracker?.RestoreSnapshot();
    }

    /// <inheritdoc/>
    public virtual void ReportStorageRefund(long refund) { }

    protected void ResetRefund() => _refundTracker?.Reset();

    public virtual GethLikeTxTrace BuildResult() => Trace;
}

public abstract class GethLikeTxTracer<TEntry>(GethTraceOptions options, long? destroyRefund = null) : GethLikeTxTracer(options, destroyRefund), ITraceOperationGasCost, ITraceOperationStart where TEntry : GethTxTraceEntry, new()
{
    protected TEntry? CurrentTraceEntry { get; set; }

    private bool _gasCostAlreadySetForCurrentOp;
    private bool _gasCostReportedForCurrentOp;
    private ulong? _calculatedGasCost;
    private string? _gasError;
    private string? _stackFailure;
    private Instruction _opcode;
    private byte _immediate;
    private int _stackHead;
    private byte[]? _lastReturnData;

    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (CurrentTraceEntry is not null)
        {
            AddTraceEntry(CurrentTraceEntry);
        }

        CurrentTraceEntry = CreateTraceEntry(opcode);
        CurrentTraceEntry.Depth = env.GetGethTraceDepth();
        CurrentTraceEntry.Gas = gas;
        CurrentTraceEntry.Opcode = GetOpcodeName(opcode);
        CurrentTraceEntry.ProgramCounter = pc;
        CurrentTraceEntry.Refund = CurrentRefund != 0 ? CurrentRefund : null;
        _gasCostAlreadySetForCurrentOp = false;
        _gasCostReportedForCurrentOp = false;
        _calculatedGasCost = null;
        _gasError = null;
        _stackFailure = null;
        _opcode = opcode;
        _immediate = ReadEip8024Immediate(opcode, pc, in env);
    }

    /// <inheritdoc/>
    public void ReportOperationStart(ulong gasCost, int stackHead, int stackInputs, int stackGrowth)
    {
        _calculatedGasCost = gasCost;
        _stackHead = stackHead;
        _stackFailure = GetStackFailure(_opcode, stackHead, stackInputs, stackGrowth);
    }

    /// <inheritdoc/>
    public void ReportOperationReady(ulong gasCost, string? error)
    {
        _calculatedGasCost = gasCost;
        _gasError = error;
    }

    /// <inheritdoc/>
    public void ReportOperationGasCost(ulong gasCost)
    {
        if (CurrentTraceEntry is not null)
        {
            CurrentTraceEntry.GasCost = gasCost;
            // The remaining gas report is skipped once the cost is set; a tracer that shows the refund after the
            // opcode samples it here, while the struct logger keeps the snapshot taken when the step started.
            if (ReportsRefundAfterOperation)
                CurrentTraceEntry.Refund = CurrentRefund != 0 ? CurrentRefund : null;
            _gasCostAlreadySetForCurrentOp = true;
            _gasCostReportedForCurrentOp = true;
        }
    }

    /// <inheritdoc/>
    public override void ReportActionErrorDetails(string error)
    {
        if (CurrentTraceEntry is not null)
            CurrentTraceEntry.Error = error;
    }

    public override void ReportOperationError(EvmExceptionType error)
    {
        if (CurrentTraceEntry is null || IsRejectedActionPrecheck(error))
            return;

        CurrentTraceEntry.Error ??= GetStepError(error, _opcode, _immediate, _stackHead, _stackFailure,
            CurrentTraceEntry.Gas, _calculatedGasCost, _gasError);
        // As in Geth, an opcode that runs out of gas reports the cost it could not pay rather than the gas it had.
        if (error == EvmExceptionType.OutOfGas && !_gasCostReportedForCurrentOp && _calculatedGasCost is ulong gasCost)
            CurrentTraceEntry.GasCost = gasCost;
    }

    public override void ReportOperationRemainingGas(ulong gas)
    {
        if (!_gasCostAlreadySetForCurrentOp && CurrentTraceEntry is not null)
        {
            CurrentTraceEntry.GasCost = GetGasCost(CurrentTraceEntry.Gas, gas, _calculatedGasCost);
            if (ReportsRefundAfterOperation)
                CurrentTraceEntry.Refund = CurrentRefund != 0 ? CurrentRefund : null;
            _gasCostAlreadySetForCurrentOp = true;
        }
    }

    /// <inheritdoc/>
    public override void ReportStorageRefund(long refund)
    {
        if (ReportsRefundAfterOperation && CurrentTraceEntry is not null)
            CurrentTraceEntry.Refund = CurrentRefund + refund;
    }

    /// <summary>Whether an entry shows the refund counter after its opcode rather than before it.</summary>
    /// <remarks>
    /// The struct logger shows the counter before the opcode, as execution-apis specifies. Geth samples it after the
    /// opcode's dynamic gas, which includes the opcode's own refund changes, in its EIP-3155 traces and also in its
    /// struct logger. The struct logger differs from Geth here on purpose.
    /// </remarks>
    protected virtual bool ReportsRefundAfterOperation => false;

    public override void SetOperationMemorySize(ulong newSize) => CurrentTraceEntry?.UpdateMemorySize(newSize);

    public override void SetOperationStack(TraceStack stack)
    {
        if (CurrentTraceEntry is not null)
            CurrentTraceEntry.Stack = stack.ToRawBytes();
    }

    public override void SetOperationMemory(TraceMemory memoryTrace)
    {
        if (IsTracingFullMemory && CurrentTraceEntry is not null)
            CurrentTraceEntry.Memory = memoryTrace.ToRawWordBytes();
    }

    /// <inheritdoc/>
    public override void SetOperationReturnData(ReadOnlySpan<byte> returnData)
    {
        if (CurrentTraceEntry is not null && !returnData.IsEmpty)
            CurrentTraceEntry.ReturnData = GetReturnData(returnData);
    }

    // Return data only changes when a call returns, so consecutive opcodes share one copy. A change allocates a new
    // array instead of overwriting the old one, because earlier entries still point to it.
    private byte[] GetReturnData(ReadOnlySpan<byte> returnData)
    {
        if (_lastReturnData is null || !returnData.SequenceEqual(_lastReturnData))
            _lastReturnData = returnData.ToArray();

        return _lastReturnData;
    }

    public override GethLikeTxTrace BuildResult()
    {
        if (CurrentTraceEntry is not null)
            AddTraceEntry(CurrentTraceEntry);

        return base.BuildResult();
    }

    protected virtual void AddTraceEntry(TEntry entry) => Trace.Entries.Add(entry);

    protected virtual TEntry CreateTraceEntry(Instruction opcode) => new();
}
