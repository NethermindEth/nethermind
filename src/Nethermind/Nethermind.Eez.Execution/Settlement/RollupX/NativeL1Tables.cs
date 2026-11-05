// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed class NativeL1Tables(ReadOnlyMemory<RollupUpdate> updates, ulong rollupId, ValueHash256 pin)
{
    public List<ExecutionEntry> Entries { get; } = [];
    public List<StaticExecutionEntry> StaticEntries { get; } = [];
    public List<ValueHash256[]> EntryEvents { get; } = [];
    public List<ValueHash256> PendingEvents { get; } = [];
    public ValueHash256 Pin { get; private set; } = pin;

    public RollupUpdate NextUpdate => Entries.Count < updates.Length
        ? updates.Span[Entries.Count] : throw NativeCallTree.Invalid("missing L1 root update");

    public void Append(L2ExecutionEntry entry)
    {
        RollupUpdate update = NextUpdate;
        Entries.Add(new ExecutionEntry([update], entry.ProxyEntryHash, entry.IncomingCalls, entry.ExpectedOutgoingCalls,
            entry.RollingHash, rollupId, entry.Success, entry.ReturnData));
        EntryEvents.Add(PendingEvents.ToArray());
        PendingEvents.Clear();
        Pin = update.NewRoot;
    }

    public void AppendStatic(L2StaticExecutionEntry entry) => StaticEntries.Add(new StaticExecutionEntry(
        [new ExpectedRoot(rollupId, Pin)], entry.ProxyEntryHash, entry.IncomingCalls,
        entry.RollingHash, rollupId, entry.Success, entry.ReturnData));

    public void Complete()
    {
        if (Entries.Count != updates.Length || PendingEvents.Count != 0)
            throw NativeCallTree.Invalid("L1 root updates do not cover the native stream");
    }
}
