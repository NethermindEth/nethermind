// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.EraE.Proofs;

public struct HistoricalSummary
{
    public ValueHash256 BlockSummaryRoot { get; set; }
    public ValueHash256 StateSummaryRoot { get; set; }

    public HistoricalSummary(ValueHash256 blockSummaryRoot, ValueHash256 stateSummaryRoot)
    {
        BlockSummaryRoot = blockSummaryRoot;
        StateSummaryRoot = stateSummaryRoot;
    }

    public static HistoricalSummary From(string blockSummaryRootHex, string stateSummaryRootHex) =>
        new(new ValueHash256(blockSummaryRootHex), new ValueHash256(stateSummaryRootHex));
}

public interface IHistoricalSummariesProvider : IDisposable
{
    Task<HistoricalSummary?> GetHistoricalSummary(int index, CancellationToken cancellationToken = default, bool forceRefresh = false);
    Task<HistoricalSummary[]> GetHistoricalSummariesAsync(string stateId = "head", CancellationToken cancellationToken = default, bool forceRefresh = false);
}
