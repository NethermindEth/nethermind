// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc.TraceStore.Ots;

internal static class OtsTraceReader
{
    internal static SearchResult<List<ParityLikeTxTrace>> Read(IDb store, ITraceSerializer<ParityLikeTxTrace> serializer,
        ITraceStoreConfig config, BlockHeader header)
    {
        if ((config.TraceTypes & ParityTraceTypes.Trace) == 0)
            return new("OTS requires TraceStore.TraceTypes to include Trace.", ErrorCodes.ResourceUnavailable);

        Span<byte> data = store.GetSpan(header.Hash!);
        try
        {
            if (!data.IsEmpty && serializer.Deserialize(data) is { } traces) return new(traces);
            return new($"Stored traces for block {header.Number} ({header.Hash}) are unavailable; OTS cannot certify complete history.", ErrorCodes.ResourceUnavailable);
        }
        finally { store.DangerousReleaseMemory(data); }
    }

    internal static string? Validate(Block block, List<ParityLikeTxTrace> traces, CancellationToken ct)
    {
        bool[] seen = new bool[block.Transactions.Length];
        foreach (ParityLikeTxTrace trace in traces)
        {
            ct.ThrowIfCancellationRequested();
            if (trace.TransactionHash is null) continue; // Block rewards are not transactions.
            if (trace.TransactionPosition is not { } position || (uint)position >= (uint)seen.Length
                || seen[position] || trace.TransactionHash != block.Transactions[position].Hash || trace.Action is null
                || trace.BlockHash != block.Hash || trace.BlockNumber != block.Number)
                return $"Incomplete or inconsistent transaction traces for block {block.Number}.";
            seen[position] = true;
        }
        return Array.IndexOf(seen, false) >= 0 ? $"Incomplete transaction traces for block {block.Number}." : null;
    }
}
