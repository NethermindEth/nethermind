// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class NativeExecutionTables
{
    private void Append(NativeCallTree.Node call, Entry entry)
    {
        List<CrossChainCall> calls = entry.Frames.TryPeek(out List<CrossChainCall>? frame) ? frame : entry.Calls;
        int index = calls.Count;
        calls.Add(call.Encoded);
        foreach (Region region in _regions)
        {
            if (call.Target == region.Host || call.Target == region.OtherNatural) continue;
            if (region.Calls.TryGetValue(calls, out (int First, int End) previous))
                region.Calls[calls] = (previous.First, index + 1);
            else region.Calls.Add(calls, (index, index + 1));
        }
    }

    private void BeginRegion(int index)
    {
        NativeCallTree.Node call = tree.Calls[index];
        foreach (int end in call.RevertEnds)
        {
            bool natural = false;
            foreach (Region region in _regions)
            {
                if (call.Parent >= 0 && tree.Calls[call.Parent].Children[0] == index && region.OtherNatural is not null &&
                    region.Parent == call.Parent && region.Host == call.Encoded.SourceRollupId && region.End == end)
                { natural = true; break; }
            }
            if (!natural) OpenRegion(end, call.Parent, call.Encoded.SourceRollupId, null);
        }
    }

    private void OpenRegion(int end, int parent, ulong host, ulong? other)
    {
        Entry? entry = parent < 0 ? null : Active(host);
        _regions.Add(new Region(end, parent, host, other, entry, entry?.Cursor ?? 0, entry?.Hash ?? default,
            Events.Count, _sourceCursor, _sourceSlot));
        entry?.Trace.Add(new(TableEventKind.RegionBegin, 0, default));
    }

    private void CloseRegion(int index)
    {
        NativeCallTree.Node call = tree.Calls[index];
        while (_regions.Count != 0)
        {
            Region region = _regions[^1];
            if (call.Parent != region.Parent || call.SubtreeEnd < region.End) return;
            if (call.SubtreeEnd != region.End) throw NativeCallTree.Invalid("revert region crosses a subtree boundary");
            _regions.RemoveAt(_regions.Count - 1);
            foreach ((List<CrossChainCall> calls, (int firstAt, int end)) in region.Calls)
            {
                int first = firstAt;
                while (first < end && calls[first].RevertNextNCalls != 0) first += calls[first].RevertNextNCalls;
                if (first > end) throw NativeCallTree.Invalid("inner rollback crosses its outer scope");
                if (first < end)
                {
                    if (end - first > ushort.MaxValue) throw NativeCallTree.Invalid("revert call count exceeds uint16");
                    calls[first] = calls[first] with { RevertNextNCalls = (ushort)(end - first) };
                }
            }
            Events.RemoveRange(region.Events, Events.Count - region.Events);
            if (region.Parent < 0)
            {
                _sourceCursor = region.SourceCursor;
                _sourceSlot = region.SourceSlot;
            }
            if (region.Entry is { } entry)
            {
                entry.Cursor = region.Cursor;
                entry.Hash = region.Hash;
                entry.Trace.Add(new(TableEventKind.RegionRestore, 0, default));
            }
        }
    }

    private sealed record Region(int End, int Parent, ulong Host, ulong? OtherNatural, Entry? Entry,
        int Cursor, ValueHash256 Hash, int Events, int SourceCursor, int SourceSlot)
    {
        public Dictionary<List<CrossChainCall>, (int First, int End)> Calls { get; } = [];
    }
}
