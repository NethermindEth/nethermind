// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

    // Cumulative storage touched within the current call frame, carried across opcodes. Per execution-apis#762 a
    // snapshot of it is attached to an opcode entry only when that opcode is an SLOAD or SSTORE; other entries omit
    // the storage field entirely.
    private Dictionary<string, string> _storage = [];

    public GethLikeTxMemoryTracer(Transaction? transaction, GethTraceOptions options) : base(options)
    {
        _transaction = transaction;
        IsTracingMemory = IsTracingFullMemory;
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

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        base.SetOperationStorage(address, storageIndex, newValue, currentValue);

        RecordStorage(storageIndex, newValue);
    }

    public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        base.LoadOperationStorage(address, storageIndex, value);

        RecordStorage(storageIndex, value);
    }

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env)
    {
        int previousDepth = CurrentTraceEntry?.Depth ?? 0;

        base.StartOperation(pc, opcode, gas, env);

        if (CurrentTraceEntry.Depth > previousDepth)
        {
            Trace.StoragesByDepth.Push(_storage);
            _storage = [];
        }
        else if (CurrentTraceEntry.Depth < previousDepth)
        {
            _storage = Trace.StoragesByDepth.Pop();
        }
    }

    private void RecordStorage(UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
        byte[] bigEndian = new byte[32];

        storageIndex.ToBigEndian(bigEndian);

        _storage[bigEndian.ToHexString(true)] = new ZeroPaddedSpan(value, 32 - value.Length, PadDirection.Left)
            .ToArray()
            .ToHexString(true);

        // Attach a snapshot to the current (SLOAD/SSTORE) entry only; non-storage opcodes leave Storage null so the
        // serializer omits the field, matching geth's struct logger.
        CurrentTraceEntry.Storage = new Dictionary<string, string>(_storage);
    }
}
