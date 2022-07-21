//  Copyright (c) 2022 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing.GethStyle;

public class GethLikeTxMemoryTracer : GethLikeTxTracer<GethTxMemoryTraceEntry>
{
    public GethLikeTxMemoryTracer(GethTraceOptions options) : base(options) { }

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

            Trace.StoragesByDepth.Push(previousTraceEntry == null ? new() : previousTraceEntry.Storage);
        }
        else if (CurrentTraceEntry.Depth < previousDepth)
        {
            if (previousTraceEntry == null)
                throw new InvalidOperationException("Missing the previous trace on leaving the call.");

            CurrentTraceEntry.Storage = new Dictionary<string, string>(Trace.StoragesByDepth.Pop());
        }
        else
        {
            if (previousTraceEntry == null)
                throw new InvalidOperationException("Missing the previous trace on continuation.");

            CurrentTraceEntry.Storage = new Dictionary<string, string>(previousTraceEntry.Storage);
        }
    }

    protected override void AddTraceEntry(GethTxMemoryTraceEntry entry) => Trace.Entries.Add(entry);

    protected override GethTxMemoryTraceEntry CreateTraceEntry(Instruction opcode) => new();
}
