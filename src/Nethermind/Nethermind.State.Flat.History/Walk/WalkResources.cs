// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;

namespace Nethermind.State.Flat.History.Walk;

public readonly record struct WalkResources(int Workers, long RowsPerPartition, long HeadroomBytes)
{
    public const long DefaultRowsPerPartition = 5_000_000;
    public const int CoresReservedForTheNode = 2;
    public const long ReservedBytes = 2L << 30;
    public const long BytesPerRow = 400;
    public const long BytesPerWorkerTrie = 512L << 20;

    public static WalkResources Resolve(IFlatDbConfig config) =>
        Resolve(config, Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.WorkingSet);

    public static WalkResources Resolve(IFlatDbConfig config, int processorCount, long totalMemoryBytes, long workingSetBytes)
    {
        long rows = config.HistoryVerifyMaxRows > 0 ? config.HistoryVerifyMaxRows : DefaultRowsPerPartition;
        long headroom = Math.Max(0, totalMemoryBytes - workingSetBytes - ReservedBytes);
        long perWorker = rows * BytesPerRow + BytesPerWorkerTrie;
        int byCores = Math.Max(1, processorCount - CoresReservedForTheNode);
        int byMemory = (int)Math.Clamp(headroom / perWorker, 1, int.MaxValue);
        int workers = config.HistoryVerifySegments > 0 ? config.HistoryVerifySegments : Math.Min(byCores, byMemory);
        return new WalkResources(workers, rows, headroom);
    }

    public override string ToString() => $"{Workers} workers, {RowsPerPartition:N0} rows per subtree, {HeadroomBytes >> 20:N0} MB headroom";
}
