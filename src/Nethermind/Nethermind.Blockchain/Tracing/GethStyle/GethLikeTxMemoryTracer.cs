// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Blockchain.Tracing.GethStyle;

public class GethLikeTxMemoryTracer : GethLikeTxTracer<GethTxMemoryTraceEntry>
{
    private readonly Transaction? _transaction;
    private readonly long _limit;
    private long _resultSize;
    private int _storageUpdates;

    private bool LimitReached => _limit != 0 && _resultSize > _limit;

    public GethLikeTxMemoryTracer(Transaction? transaction, GethTraceOptions options, long destroyRefund = 0) : base(options, destroyRefund)
    {
        _transaction = transaction;
        _limit = options.Limit;
        IsTracingMemory = IsTracingFullMemory;
        IsTracingRefunds = true;
        IsTracingActions = true;
    }

    /// <inheritdoc/>
    public override void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
        if (LimitReached) return;
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

    public override void SetOperationReturnData(ReadOnlySpan<byte> returnData)
    {
        if (CurrentTraceEntry is not null && !returnData.IsEmpty)
            CurrentTraceEntry.ReturnData = returnData.ToHexString(true);
    }
}
