// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

/// <summary>Reconstructs execution tables for one two-chain native transaction.</summary>
/// <remarks>Materialization alone does not authorize prover signing.</remarks>
internal sealed class NativeExecutionTables(NativeCallTree tree, ulong rollupId, NativeL1Tables? l1 = null)
{
    private readonly List<Entry> _entries = [];
    private readonly List<L2StaticExecutionEntry> _statics = [];
    private Entry? _active;
    private Entry? _l1Active;
    private Region? _region;

    public ExecutionTable Outbound()
    {
        OpenL1(default, true, ReadOnlyMemory<byte>.Empty);
        List<(int Root, Entry? Entry)> assignments = PlanSource();
        foreach ((int root, Entry? entry) in assignments)
        {
            BeginRegion(root);
            _active = entry;
            Execute(root);
            if (_active != entry) throw NativeCallTree.Invalid("outbound callback opened a separate delivery");
            _active = null;
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
                l1?.AppendStatic(StaticEntry(call, 0));
                continue;
            }
            OpenL1(call.Hash, call.Result.Success, call.Result.ReturnData);
            if (root >= revertedUntil && call.Result.Success) Execute(root);
            CloseL1();
        }

        return Finish();
    }

    private void OpenL1(in ValueHash256 proxy, bool success, ReadOnlyMemory<byte> output)
    {
        if (l1 is null) return;
        if (_l1Active is not null) throw NativeCallTree.Invalid("L1 host already open");
        _l1Active = new Entry(proxy, success, output) { Hash = RollingHash.SeedL1(l1.NextUpdate, proxy) };
    }

    private void CloseL1()
    {
        if (_l1Active is null) return;
        l1!.Append(_l1Active.Finish());
        _l1Active = null;
    }

    private List<(int Root, Entry? Entry)> PlanSource()
    {
        List<(int, Entry?)> assignments = new(tree.Roots.Count);
        int cursor = 0;
        (int End, int Cursor)? region = null;
        foreach (int root in tree.Roots)
        {
            NativeCallTree.Node call = tree.Calls[root];
            if (call.Encoded.IsStatic)
            {
                _statics.Add(StaticEntry(call, cursor));
                assignments.Add((root, null));
                continue;
            }

            if (call.RevertEnd != 0) region = (call.RevertEnd, cursor);
            int slot = cursor++;
            if (slot == _entries.Count) _entries.Add(new Entry(call.Hash, call.Result.Success, call.Result.ReturnData));
            else _entries[slot].CheckReuse(call);
            assignments.Add((root, _entries[slot]));
            if (region is { } saved && call.SubtreeEnd == saved.End)
            {
                cursor = saved.Cursor;
                region = null;
            }
        }

        if (region is not null) throw NativeCallTree.Invalid("unclosed outbound source cursor region");
        return assignments;
    }

    private L2StaticExecutionEntry StaticEntry(NativeCallTree.Node call, int cursor)
    {
        CrossChainCall[] callbacks = new CrossChainCall[call.Children.Count];
        ValueHash256 hash = default;
        for (int i = 0; i < callbacks.Length; i++)
        {
            NativeCallTree.Node callback = tree.Calls[call.Children[i]];
            callbacks[i] = callback.Encoded;
            hash = RollingHash.StaticResult(hash, callback.Result.Success, callback.Result.ReturnData.Span);
        }

        return new L2StaticExecutionEntry((UInt256)cursor, call.Hash, callbacks, hash,
            call.Result.Success, call.Result.ReturnData.ToArray());
    }

    private void Execute(int index)
    {
        NativeCallTree.Node call = tree.Calls[index];
        bool destination = call.Target == rollupId;
        bool opened = destination && _active is null;
        if (opened)
        {
            _active = new Entry(call.Hash, true, call.Result.Success ? call.Result.ReturnData : ReadOnlyMemory<byte>.Empty);
            _entries.Add(_active);
        }

        if (l1 is not null && call.Target == 0 && !call.Encoded.IsStatic && call.Result.Success)
            l1.PendingEvents.Add(call.Hash);
        Entry? target = destination ? _active : _l1Active;
        if (target is not null)
        {
            Append(call, target);
            target.Hash = RollingHash.CallBegin(target.Hash, call.Hash);
        }
        foreach (int child in call.Children)
        {
            BeginRegion(child);
            Reentrant(child);
            CloseRegion(child);
        }
        if (target is not null) target.Hash = RollingHash.CallEnd(target.Hash, call.Result.Success, call.Result.ReturnData.Span);
        if (opened) _active = null;
    }

    private void Reentrant(int index)
    {
        NativeCallTree.Node call = tree.Calls[index];
        Entry? source = call.Encoded.SourceRollupId == rollupId ? _active : _l1Active;
        Row? row = null;
        ValueHash256 saved = default;
        if (source is not null)
        {
            row = source.NextRow(call);
            saved = source.Hash;
            if (!call.Encoded.IsStatic) source.Hash = RollingHash.NestedBegin(source.Hash, call.Hash);
            source.Frames.Push(row.Calls);
        }

        // Static-root callbacks execute from the root's static pool, not as deliveries.
        if (call.Parent < 0 || !tree.Calls[call.Parent].Encoded.IsStatic) Execute(index);

        if (source is not null)
        {
            source.Frames.Pop();
            if (!call.Encoded.IsStatic)
            {
                if (call.Result.Success) source.Hash = RollingHash.NestedEnd(source.Hash);
                else
                {
                    row!.RevertedHash = source.Hash;
                    source.Hash = saved;
                }
            }
        }
    }

    private void Append(NativeCallTree.Node call, Entry entry)
    {
        List<CrossChainCall> calls = entry.Frames.TryPeek(out List<CrossChainCall>? frame) ? frame : entry.Calls;
        calls.Add(call.Encoded);
        if (_region is not { } region || region.Host == call.Target) return;
        if (!region.FirstCalls.TryGetValue(calls, out int first))
        {
            first = calls.Count - 1;
            region.FirstCalls.Add(calls, first);
        }
        CrossChainCall marker = calls[first];
        if (marker.RevertNextNCalls == ushort.MaxValue) throw NativeCallTree.Invalid("revert call count exceeds uint16");
        calls[first] = marker with { RevertNextNCalls = (ushort)(marker.RevertNextNCalls + 1) };
    }

    private void BeginRegion(int index)
    {
        NativeCallTree.Node call = tree.Calls[index];
        if (call.RevertEnd == 0) return;
        if (_region is not null) throw NativeCallTree.Invalid("overlapping active revert regions");
        Entry? host = call.Parent < 0 ? null : call.Encoded.SourceRollupId == rollupId ? _active : _l1Active;
        _region = new Region(call.RevertEnd, call.Parent, call.Encoded.SourceRollupId, host, host?.Cursor ?? 0, host?.Hash ?? default,
            l1?.PendingEvents.Count ?? 0);
    }

    private void CloseRegion(int index)
    {
        if (_region is not { } region || tree.Calls[index].Parent != region.Parent || tree.Calls[index].SubtreeEnd < region.End) return;
        if (tree.Calls[index].SubtreeEnd != region.End) throw NativeCallTree.Invalid("revert region crosses a subtree boundary");
        if (l1 is not null) l1.PendingEvents.RemoveRange(region.Events, l1.PendingEvents.Count - region.Events);
        if (region.Entry is { } entry)
        {
            entry.Cursor = region.Cursor;
            entry.Hash = region.Hash;
        }
        _region = null;
    }

    private L2ExecutionEntry[] Finish()
    {
        if (_active is not null || _l1Active is not null || _region is not null) throw NativeCallTree.Invalid("unfinished table context");
        L2ExecutionEntry[] entries = new L2ExecutionEntry[_entries.Count];
        for (int i = 0; i < entries.Length; i++) entries[i] = _entries[i].Finish();
        return entries;
    }

    private sealed class Entry(ValueHash256 proxy, bool success, ReadOnlyMemory<byte> returnData)
    {
        public ValueHash256 Proxy { get; } = proxy;
        public bool Success { get; } = success;
        public byte[] ReturnData { get; } = returnData.ToArray();
        public ValueHash256 Hash { get; set; } = RollingHash.SeedL2(proxy);
        public int Cursor { get; set; }
        public List<CrossChainCall> Calls { get; } = [];
        public List<Row> Rows { get; } = [];
        public Stack<List<CrossChainCall>> Frames { get; } = new();

        public void CheckReuse(NativeCallTree.Node other)
        {
            if (Proxy != other.Hash || Success != other.Result.Success || !ReturnData.AsSpan().SequenceEqual(other.Result.ReturnData.Span))
            {
                throw NativeCallTree.Invalid("outbound cursor reused with a different call or result");
            }
        }

        public Row NextRow(NativeCallTree.Node next)
        {
            ValueHash256 position = Position(next.Hash, Hash);
            int index = Cursor++;
            if (index < Rows.Count)
            {
                Row reused = Rows[index];
                if (next.Children.Count != 0 || reused.ExpectedHash != position || reused.Success != next.Result.Success
                    || !reused.ReturnData.AsSpan().SequenceEqual(next.Result.ReturnData.Span))
                {
                    throw NativeCallTree.Invalid("reentrant cursor reused with descendants or a different position/result");
                }
                return reused;
            }

            Row row = new(position, next.Result.Success, next.Result.ReturnData.ToArray());
            Rows.Add(row);
            return row;
        }

        public L2ExecutionEntry Finish()
        {
            if (Frames.Count != 0) throw NativeCallTree.Invalid("unfinished reentrant frame");
            ExpectedCall[] expected = new ExpectedCall[Rows.Count];
            for (int i = 0; i < expected.Length; i++)
            {
                Row row = Rows[i];
                expected[i] = new ExpectedCall(row.ExpectedHash, row.Calls.ToArray(), row.RevertedHash, row.Success, row.ReturnData);
            }
            return new L2ExecutionEntry(Proxy, Calls.ToArray(), expected, Hash, Success, ReturnData);
        }

        private static ValueHash256 Position(in ValueHash256 callHash, in ValueHash256 rollingHash)
        {
            Span<byte> bytes = stackalloc byte[64];
            callHash.Bytes.CopyTo(bytes);
            rollingHash.Bytes.CopyTo(bytes[32..]);
            return ValueKeccak.Compute(bytes);
        }
    }

    private sealed class Row(ValueHash256 expectedHash, bool success, byte[] returnData)
    {
        public ValueHash256 ExpectedHash { get; } = expectedHash;
        public bool Success { get; } = success;
        public byte[] ReturnData { get; } = returnData;
        public List<CrossChainCall> Calls { get; } = [];
        public ValueHash256 RevertedHash { get; set; }
    }

    private sealed record Region(int End, int Parent, ulong Host, Entry? Entry, int Cursor, ValueHash256 Hash, int Events)
    {
        public Dictionary<List<CrossChainCall>, int> FirstCalls { get; } = [];
    }
}
