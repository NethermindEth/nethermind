// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxMemoryTracer : GethLikeTxTracer<GethTxMemoryTraceEntry>, ITxTracer
{
    private readonly Transaction? _transaction;
    private readonly long _limit;
    private readonly long _stepLimit;
    private long _resultSize;
    private int _storageUpdates;
    private Instruction _opcode;
    private bool _actionOnlyDetailReported;

    private bool LimitReached => (_limit != 0 && _resultSize > _limit)
        || (_stepLimit > 0 && Trace.Entries.Count >= _stepLimit);

    public GethLikeTxMemoryTracer(Transaction? transaction, GethTraceOptions options, long destroyRefund = 0) : base(options, destroyRefund)
    {
        _transaction = transaction;
        _limit = options.Limit;
        _stepLimit = options.OpcodeStepLimit;
        IsTracingMemory = IsTracingFullMemory;
        IsTracingRefunds = true;
        IsTracingActions = true;
        options.ResolveTimeout();
    }

    /// <inheritdoc/>
    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (LimitReached) return;
        _opcode = opcode;
        _actionOnlyDetailReported = false;
        base.StartOperation(pc, opcode, gas, in env);
        if (LimitReached)
            CurrentTraceEntry = null;
    }

    protected override void AddTraceEntry(GethTxMemoryTraceEntry entry)
    {
        base.AddTraceEntry(entry);
        if (_limit <= 0) return;

        int? storageCount = entry.StorageDelta.HasValue ? ++_storageUpdates : null;
        _resultSize += GethLikeTxTraceConverter.EstimateEntrySize(entry, storageCount);
    }

    /// <inheritdoc/>
    /// <remarks>As in Geth, a step that halts exceptionally carries its error and a zero gas cost.</remarks>
    public override void ReportOperationError(EvmExceptionType error)
    {
        if (CurrentTraceEntry is null || IsRejectedActionPrecheck(error))
            return;

        if (error == EvmExceptionType.BadInstruction && _actionOnlyDetailReported)
        {
            CurrentTraceEntry.Opcode = OpcodeJsonNames.GetUndefinedName(_opcode);
            CurrentTraceEntry.Error ??= $"invalid opcode: {CurrentTraceEntry.Opcode}";
        }
        base.ReportOperationError(error);
        CurrentTraceEntry.GasCost = 0;
    }

    /// <remarks>
    /// Only the handler for opcodes the active fork does not define sends an action-only detail on a step that fails as a
    /// bad instruction; defined opcodes that fail as one, and code deposit errors, do not.
    /// </remarks>
    void ITxTracer.ReportActionErrorDetails(string error, bool actionOnly)
    {
        if (actionOnly)
            _actionOnlyDetailReported = true;
        else
            ReportActionErrorDetails(error);
    }

    public override GethLikeTxTrace BuildResult()
    {
        GethLikeTxTrace trace = base.BuildResult();

        trace.TxHash = _transaction?.Hash;

        return trace;
    }

    public override void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);

        Trace.Gas = gasSpent.SpentGas;
    }

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        base.LoadOperationStorage(address, storageIndex, value);

        RecordStorageSnapshot(address, storageIndex, value);
    }

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        base.SetOperationStorage(address, storageIndex, newValue, currentValue);

        RecordStorageSnapshot(address, storageIndex, newValue);
    }

    private void RecordStorageSnapshot(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        if (CurrentTraceEntry is null)
            return;

        CurrentTraceEntry.StorageDelta = (address, storageIndex, new UInt256(value, isBigEndian: true));
    }
}
