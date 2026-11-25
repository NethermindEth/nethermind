// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class NativeExecutionTables
{
    private enum TableEventKind { CallBegin, CallEnd, NestedBegin, NestedEnd, NestedRevert, StaticLookup, RegionBegin, RegionRestore }
    private readonly record struct TableEvent(TableEventKind Kind, int Row, ValueHash256 Hash);

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
        public List<TableEvent> Trace { get; } = [];
        private readonly Dictionary<RowKey, List<int>> _positions = [];
        private Dictionary<(ValueHash256 Position, ValueHash256 Fold), (bool Success, ReadOnlyMemory<byte> Data)>? _staticResults;

        public void Begin(in ValueHash256 hash)
        {
            Hash = RollingHash.CallBegin(Hash, hash);
            Trace.Add(new(TableEventKind.CallBegin, 0, hash));
        }

        public void End(StreamMessage.Return result)
        {
            Hash = RollingHash.CallEnd(Hash, result.Success, result.ReturnData.Span);
            Trace.Add(new(TableEventKind.CallEnd, 0, Hash));
        }

        public void NestedBegin(in ValueHash256 hash, int row)
        {
            Hash = RollingHash.NestedBegin(Hash, hash);
            Trace.Add(new(TableEventKind.NestedBegin, row, hash));
        }

        public void NestedEnd()
        {
            Hash = RollingHash.NestedEnd(Hash);
            Trace.Add(new(TableEventKind.NestedEnd, 0, default));
        }

        public void CheckOutcome(NativeCallTree.Node call)
        {
            if (Proxy != call.Hash || Success != call.Result.Success || !ReturnData.AsSpan().SequenceEqual(call.Result.ReturnData.Span))
                throw NativeCallTree.Invalid("outbound cursor reused with a different call or result");
        }

        public (Row Row, bool Reused) MutableRow(NativeCallTree.Node call)
        {
            ValueHash256 position = Position(call.Hash, Hash);
            List<int> indices = Indices(new(position, false, false, default, default));
            int first = FirstAtOrAfter(indices, Cursor);
            int index = first < indices.Count ? indices[first] : Rows.Count;
            Cursor = index + 1;
            if (index < Rows.Count)
            {
                Row reused = Rows[index];
                if (reused.Success != call.Result.Success || !reused.ReturnData.AsSpan().SequenceEqual(call.Result.ReturnData.Span))
                    throw NativeCallTree.Invalid("reentrant position reused with a different result");
                return (reused, true);
            }
            Row row = new(index, position, call.Result.Success, call.Result.ReturnData.ToArray());
            indices.Add(index);
            Rows.Add(row);
            return (row, false);
        }

        public Row StaticRow(NativeCallTree.Node call, NativeCallTree tree, ValueHash256 fold, OperationsVersion version)
        {
            ValueHash256 position = Position(call.Hash, Hash);
            _staticResults ??= [];
            CheckStaticOutcome(_staticResults, (position, fold), call);
            List<int>? indices = null;
            if (version == OperationsVersion.V1)
            {
                ValueHash256 program = default;
                foreach (int child in call.Children) program = RollingHash.CallBegin(program, tree.Calls[child].Hash);
                indices = Indices(new(position, true, call.Result.Success, ValueKeccak.Compute(call.Result.ReturnData.Span), Position(program, fold)));
                for (int i = FirstAtOrAfter(indices, Cursor); i < indices.Count; i++)
                {
                    Row candidate = Rows[indices[i]];
                    if (candidate.Complete && candidate.RevertedHash == fold && SameChildren(candidate.Calls, call, tree) &&
                        candidate.ReturnData.AsSpan().SequenceEqual(call.Result.ReturnData.Span)) return candidate;
                }
            }
            Row row = new(Rows.Count, position, call.Result.Success, call.Result.ReturnData.ToArray()) { RevertedHash = fold };
            foreach (int child in call.Children) row.Calls.Add(tree.Calls[child].Encoded);
            indices?.Add(row.Index);
            Rows.Add(row);
            return row;
        }

        private List<int> Indices(RowKey key)
        {
            if (!_positions.TryGetValue(key, out List<int>? indices))
            {
                indices = [];
                _positions.Add(key, indices);
            }
            return indices;
        }

        public L2ExecutionEntry Finish()
        {
            if (Frames.Count != 0) throw NativeCallTree.Invalid("unfinished reentrant frame");
            ExpectedCall[] rows = new ExpectedCall[Rows.Count];
            for (int i = 0; i < rows.Length; i++)
            {
                Row row = Rows[i];
                rows[i] = new(row.ExpectedHash, row.Calls.ToArray(), row.RevertedHash, row.Success, row.ReturnData);
            }
            return new(Proxy, Calls.ToArray(), rows, Hash, Success, ReturnData);
        }

        internal static bool Same(Entry left, Entry right)
        {
            if (left.Trace.Count != right.Trace.Count) return false;
            for (int i = 0; i < left.Trace.Count; i++)
                if (left.Trace[i] != right.Trace[i]) return false;
            if (left.Proxy != right.Proxy || left.Success != right.Success || left.Hash != right.Hash ||
                !left.ReturnData.AsSpan().SequenceEqual(right.ReturnData) || !SameCalls(left.Calls, right.Calls) ||
                left.Rows.Count != right.Rows.Count) return false;
            for (int i = 0; i < left.Rows.Count; i++)
            {
                Row a = left.Rows[i], b = right.Rows[i];
                if (a.ExpectedHash != b.ExpectedHash || a.RevertedHash != b.RevertedHash || a.Success != b.Success ||
                    !a.ReturnData.AsSpan().SequenceEqual(b.ReturnData) || !SameCalls(a.Calls, b.Calls)) return false;
            }
            return true;
        }

        private static ValueHash256 Position(in ValueHash256 call, in ValueHash256 rolling)
        {
            Span<byte> encoded = stackalloc byte[64];
            call.Bytes.CopyTo(encoded);
            rolling.Bytes.CopyTo(encoded[32..]);
            return ValueKeccak.Compute(encoded);
        }

        private readonly record struct RowKey(ValueHash256 Position, bool Static, bool Success, ValueHash256 Result, ValueHash256 Callbacks);
    }

    private static int FirstAtOrAfter(List<int> indices, int cursor)
    {
        int index = indices.BinarySearch(cursor);
        return index < 0 ? ~index : index;
    }

    private sealed class Row(int index, ValueHash256 expectedHash, bool success, byte[] returnData)
    {
        public int Index { get; } = index;
        public ValueHash256 ExpectedHash { get; } = expectedHash;
        public bool Success { get; } = success;
        public byte[] ReturnData { get; } = returnData;
        public List<CrossChainCall> Calls { get; } = [];
        public ValueHash256 RevertedHash { get; set; }
        public bool Complete { get; set; }
    }
}
