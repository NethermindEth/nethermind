// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed partial class NativeExecutionTables
{
    private Dictionary<(int Entry, ValueHash256 Hash, ValueHash256 Fold), (bool Success, ReadOnlyMemory<byte> Data)>? _staticSourceResults;

    private void ExecuteStatic(int index)
    {
        NativeCallTree.Node call = tree.Calls[index];
        ValueHash256 fold = default;
        for (int i = 0; i < call.Children.Count; i++)
        {
            NativeCallTree.Node child = tree.Calls[call.Children[i]];
            fold = RollingHash.StaticResult(fold, child.Result.Success, child.Result.ReturnData.Span);
        }
        Entry? source = Active(call.Encoded.SourceRollupId);
        Row? row = source?.StaticRow(call, tree, fold, version);
        bool lookup = row is not null || call.Encoded.SourceRollupId == Origin;
        if (row is not null) source!.Trace.Add(new(TableEventKind.StaticLookup, row.Index, call.Hash));
        else if (lookup)
        {
            CrossChainCall[] callbacks = new CrossChainCall[call.Children.Count];
            for (int i = 0; i < callbacks.Length; i++) callbacks[i] = tree.Calls[call.Children[i]].Encoded;
            L2StaticExecutionEntry entry = new((UInt256)_sourceCursor, call.Hash, callbacks, fold,
                call.Result.Success, call.Result.ReturnData.ToArray());
            if (call.Encoded.SourceRollupId == rollupId)
            {
                _staticSourceResults ??= [];
                CheckStaticOutcome(_staticSourceResults, (_sourceCursor, call.Hash, fold), call);
                _statics.Add(entry);
            }
            else l1?.AppendStatic(entry);
        }
        if (lookup)
        {
            if (row is not null) row.Complete = false;
            Reads(call.Encoded.SourceRollupId).Push(row);
        }
        Entry? direct = Reads(call.Target).Count == 0 ? Active(call.Target) : null;
        if (direct is not null)
        {
            Append(call, direct);
            direct.Begin(call.Hash);
        }
        foreach (int child in call.Children) ExecuteStatic(child);
        direct?.End(call.Result);
        if (lookup)
        {
            Reads(call.Encoded.SourceRollupId).Pop();
            if (row is not null) row.Complete = true;
        }
    }
    private static void CheckStaticOutcome<TKey>(Dictionary<TKey, (bool Success, ReadOnlyMemory<byte> Data)> results,
        TKey key, NativeCallTree.Node call) where TKey : notnull
    {
        if (results.TryGetValue(key, out (bool Success, ReadOnlyMemory<byte> Data) first))
        {
            if (first.Success != call.Result.Success || !first.Data.Span.SequenceEqual(call.Result.ReturnData.Span))
                throw NativeCallTree.Invalid("static lookup repeats a commitment with a different result");
        }
        else results.Add(key, (call.Result.Success, call.Result.ReturnData));
    }

    private static bool SameChildren(List<CrossChainCall> calls, NativeCallTree.Node call, NativeCallTree tree)
    {
        if (calls.Count != call.Children.Count) return false;
        for (int i = 0; i < calls.Count; i++)
            if (!SameCall(calls[i], tree.Calls[call.Children[i]].Encoded)) return false;
        return true;
    }
}
