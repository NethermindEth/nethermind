// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Reconstructs execution tables for one two-chain native transaction.</summary>
/// <remarks>Materialization alone does not authorize prover signing.</remarks>
internal sealed partial class NativeExecutionTables(NativeCallTree tree, ulong rollupId, NativeL1Tables? l1 = null,
    OperationsVersion version = OperationsVersion.V0)
{
    private readonly List<Entry> _entries = [];
    private readonly List<L2StaticExecutionEntry> _statics = [];
    private readonly List<Region> _regions = [];
    private readonly Stack<Row?> _l1Reads = new();
    private readonly Stack<Row?> _l2Reads = new();
    private readonly List<ValueHash256> _events = [];
    private Dictionary<ValueHash256, List<int>>? _sourcePositions;
    private Entry? _active;
    private Entry? _l1Active;
    private int _sourceCursor;
    private int _sourceSlot;

    private List<ValueHash256> Events => l1?.PendingEvents ?? _events;
    private ulong Origin => tree.Calls[tree.Roots[0]].Encoded.SourceRollupId;
    private Entry? Active(ulong chain) => chain == rollupId ? _active : _l1Active;
    private Stack<Row?> Reads(ulong chain) => chain == rollupId ? _l2Reads : _l1Reads;

    public ExecutionTable Outbound()
    {
        OpenL1(default, true, ReadOnlyMemory<byte>.Empty);
        foreach (int root in tree.Roots)
        {
            BeginRegion(root);
            NativeCallTree.Node call = tree.Calls[root];
            if (call.Encoded.IsStatic) ExecuteStatic(root);
            else ExecuteSource(root);
            CloseRegion(root);
        }
        CloseL1();
        return new ExecutionTable(Finish(), _statics.ToArray());
    }

    public L2ExecutionEntry[] Inbound()
    {
        int revertedUntil = 0;
        foreach (int root in tree.Roots)
        {
            NativeCallTree.Node call = tree.Calls[root];
            revertedUntil = Math.Max(revertedUntil, call.RevertEnd);
            if (call.Encoded.IsStatic)
            {
                ExecuteStatic(root);
                continue;
            }
            OpenL1(call.Hash, call.Result.Success, call.Result.ReturnData);
            if (root >= revertedUntil && call.Result.Success)
            {
                BeginRegion(root);
                Execute(root);
                CloseRegion(root);
            }
            else
            {
                if (call.Result.Success && call.Children.Count != 0)
                    throw NativeCallTree.Invalid("a successful lookup has callbacks outside canonical execution");
                int entries = _entries.Count;
                int events = Events.Count;
                Execute(root);
                _entries.RemoveRange(entries, _entries.Count - entries);
                Events.RemoveRange(events, Events.Count - events);
            }
            CloseL1();
        }
        return Finish();
    }

    internal L2ExecutionEntry Lookup(int root)
    {
        NativeCallTree.Node call = tree.Calls[root];
        if (call.Parent >= 0 || call.Encoded.SourceRollupId != 0 || call.Target != rollupId || call.Encoded.IsStatic)
            throw NativeCallTree.Invalid("lookup execution requires a mutable L1 root");
        Execute(root);
        L2ExecutionEntry[] entries = Finish();
        if (entries.Length != 1 || _statics.Count != 0) throw NativeCallTree.Invalid("lookup is not one destination table");
        return entries[0];
    }

    private void ExecuteSource(int root)
    {
        NativeCallTree.Node call = tree.Calls[root];
        _sourcePositions ??= [];
        if (!_sourcePositions.TryGetValue(call.Hash, out List<int>? positions))
        {
            positions = [];
            _sourcePositions.Add(call.Hash, positions);
        }
        int found = FirstAtOrAfter(positions, _sourceCursor);
        int selected = found < positions.Count ? positions[found] : -1;
        Entry? previous = selected < 0 ? null : _entries[selected];
        previous?.CheckOutcome(call);
        int slot = selected;
        if (!call.Result.Success && call.Children.Count == 0)
        {
            slot = -1;
            if (previous is not null)
                for (int i = FirstAtOrAfter(positions, _sourceSlot); i < positions.Count; i++)
                    if (Entry.Same(_entries[positions[i]], previous)) { slot = positions[i]; break; }
        }
        if (slot < 0) slot = _entries.Count;
        Entry current = new(call.Hash, call.Result.Success, call.Result.ReturnData);
        _active = current;
        Execute(root);
        if (_active != current) throw NativeCallTree.Invalid("outbound callback opened a separate delivery");
        _active = null;
        if (previous is not null && !Entry.Same(previous, current))
            throw NativeCallTree.Invalid("reused outbound entry has a different callback program");
        if (slot == _entries.Count)
        {
            positions.Add(slot);
            _entries.Add(current);
        }
        else _entries[slot] = current;
        if (call.Result.Success) _sourceCursor = (selected < 0 ? slot : selected) + 1;
        _sourceSlot = call.Result.Success || call.Children.Count == 0 ? slot + 1 : slot;
    }

    private void OpenL1(in ValueHash256 proxy, bool success, ReadOnlyMemory<byte> output)
    {
        if (l1 is null) return;
        if (_l1Active is not null) throw NativeCallTree.Invalid("L1 host already open");
        _l1Active = new Entry(proxy, success, output) { Hash = RollingHash.SeedL1(l1.NextUpdate, proxy) };
    }

    private void CloseL1()
    {
        if (_l1Active is not null) l1!.Append(_l1Active.Finish());
        _l1Active = null;
        Events.Clear();
    }

    private void Execute(int index)
    {
        NativeCallTree.Node call = tree.Calls[index];
        bool opened = call.Target == rollupId && _active is null;
        if (opened)
        {
            _active = new Entry(call.Hash, true, call.Result.Success ? call.Result.ReturnData : ReadOnlyMemory<byte>.Empty);
            _entries.Add(_active);
        }
        if (call.Target == 0 && call.Result.Success) Events.Add(call.Hash);
        Entry? target = Active(call.Target);
        if (target is not null)
        {
            Append(call, target);
            target.Begin(call.Hash);
        }
        if (!call.Result.Success && call.Children.Count != 0)
            OpenRegion(call.SubtreeEnd, call.Index, call.Target, call.Encoded.SourceRollupId);
        foreach (int child in call.Children)
        {
            BeginRegion(child);
            Reentrant(child);
            CloseRegion(child);
        }
        target?.End(call.Result);
        if (opened) _active = null;
    }

    private void Reentrant(int index)
    {
        NativeCallTree.Node call = tree.Calls[index];
        if (call.Encoded.IsStatic) { ExecuteStatic(index); return; }
        Entry? source = Active(call.Encoded.SourceRollupId);
        if (source is null) { Execute(index); return; }
        int cursor = source.Cursor;
        ValueHash256 saved = source.Hash;
        (Row row, bool reused) = source.MutableRow(call);
        List<CrossChainCall> calls = reused ? [] : row.Calls;
        source.NestedBegin(call.Hash, row.Index);
        source.Frames.Push(calls);
        Execute(index);
        source.Frames.Pop();
        ValueHash256 failedHash = call.Result.Success ? default : source.Hash;
        if (reused && (!SameCalls(row.Calls, calls) || row.RevertedHash != failedHash))
            throw NativeCallTree.Invalid("reused row has a different callback program or failure commitment");
        row.RevertedHash = failedHash;
        if (call.Result.Success) source.NestedEnd();
        else
        {
            source.Trace.Add(new(TableEventKind.NestedRevert, row.Index, failedHash));
            source.Hash = saved;
            source.Cursor = cursor;
        }
    }

    private L2ExecutionEntry[] Finish()
    {
        if (_active is not null || _l1Active is not null || _regions.Count != 0 || _l1Reads.Count != 0 || _l2Reads.Count != 0)
            throw NativeCallTree.Invalid("unfinished table context");
        L2ExecutionEntry[] entries = new L2ExecutionEntry[_entries.Count];
        for (int i = 0; i < entries.Length; i++) entries[i] = _entries[i].Finish();
        return entries;
    }

    private static bool SameCalls(IReadOnlyList<CrossChainCall> left, IReadOnlyList<CrossChainCall> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!SameCall(left[i], right[i])) return false;
        return true;
    }
    private static bool SameCall(CrossChainCall a, CrossChainCall b) =>
        a.RevertNextNCalls == b.RevertNextNCalls && a.IsStatic == b.IsStatic && a.Gas == b.Gas &&
        a.SourceAddress == b.SourceAddress && a.SourceRollupId == b.SourceRollupId && a.TargetAddress == b.TargetAddress &&
        a.Value == b.Value && a.Data.AsSpan().SequenceEqual(b.Data);
}
