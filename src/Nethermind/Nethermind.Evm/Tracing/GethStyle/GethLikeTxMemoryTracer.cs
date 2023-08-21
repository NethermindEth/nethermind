// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeTxMemoryTracer : GethLikeTxTracer<GethTxMemoryTraceEntry>
{
    public GethLikeTxMemoryTracer(GethTraceOptions options) : base(options) => IsTracingMemory = IsTracingFullMemory;

    public override void MarkAsSuccess(Address recipient, long gasSpent, byte[] output, LogEntry[] logs, Keccak? stateRoot = null)
    {
        base.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);

        Trace.Gas = gasSpent;
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

    public override void StartOperation(int depth, long gas, Instruction opcode, int pc, bool isPostMerge)
    {
        var previousTraceEntry = CurrentTraceEntry;
        var previousDepth = CurrentTraceEntry?.Depth ?? 0;

        base.StartOperation(depth, gas, opcode, pc, isPostMerge);

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

    protected override void AddTraceEntry(GethTxMemoryTraceEntry entry) => Trace.Entries.Add(entry);

    protected override GethTxMemoryTraceEntry CreateTraceEntry(Instruction opcode) => new();
}
