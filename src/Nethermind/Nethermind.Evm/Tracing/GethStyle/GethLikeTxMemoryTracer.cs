// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeTxMemoryTracer : GethLikeTxTracer<GethTxMemoryTraceEntry>
{
    private readonly Transaction? _transaction;

    public GethLikeTxMemoryTracer(Transaction? transaction, GethTraceOptions options) : base(options)
    {
        _transaction = transaction;
        IsTracingMemory = IsTracingFullMemory;
    }

    public override GethLikeTxTrace BuildResult()
    {
        var trace = base.BuildResult();

        trace.TxHash = _transaction?.Hash;

        return trace;
    }

    public override void MarkAsSuccess(Address recipient, GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);

        Trace.Gas = gasSpent.SpentGas;
    }

    public override void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        base.SetOperationStorage(address, storageIndex, newValue, currentValue);

        byte[] bigEndian = new byte[32];

        storageIndex.ToBigEndian(bigEndian);

        CurrentTraceEntry.Storage[bigEndian.ToHexString(false)] = new ZeroPaddedSpan(newValue, 32 - newValue.Length, PadDirection.Left)
            .ToArray()
            .ToHexString(false);
    }

    public override void StartOperation(int pc, Instruction opcode, long gas, in ExecutionEnvironment env, int codeSection = 0, int functionDepth = 0)
    {
        GethTxMemoryTraceEntry previousTraceEntry = CurrentTraceEntry;
        var previousDepth = CurrentTraceEntry?.Depth ?? 0;

        base.StartOperation(pc, opcode, gas, env, codeSection, functionDepth);

        if (CurrentTraceEntry.Depth > previousDepth)
        {
            CurrentTraceEntry.Storage = new Dictionary<string, string>();

            Trace.StoragesByDepth.Push(previousTraceEntry is null ? new() : previousTraceEntry.Storage);
        }
        else if (CurrentTraceEntry.Depth < previousDepth)
        {
            if (previousTraceEntry is null)
                throw new InvalidOperationException("Missing the previous trace on leaving the call.");

            CurrentTraceEntry.Storage = new Dictionary<string, string>(Trace.StoragesByDepth.Pop());
        }
        else
        {
            if (previousTraceEntry is null)
                throw new InvalidOperationException("Missing the previous trace on continuation.");

            CurrentTraceEntry.Storage = new Dictionary<string, string>(previousTraceEntry.Storage);
        }
    }
}
