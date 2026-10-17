// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.State.Flat.Persistence;

public interface IBatchPersistenceReader
{
    bool TryGetCachedSlot(Address address, in UInt256 slot, out UInt256? value)
    {
        value = null;
        return false;
    }

    void GetSlots(ReadOnlySpan<StorageCell> cells, Span<UInt256?> values);

    /// <summary>Reads slots already found absent from this reader's cache.</summary>
    /// <remarks>The reader must retain the same persistence snapshot as the preceding cache probe.</remarks>
    void GetSlotsAfterCacheMiss(ReadOnlySpan<StorageCell> cells, Span<UInt256?> values) => GetSlots(cells, values);
}

public static class BatchPersistenceReaderExtensions
{
    internal const int BatchSize = 64;
    internal const int MinBatchSize = 8;

    internal static void GetSlotsAfterCacheMiss(this IPersistence.IPersistenceReader reader,
        ReadOnlySpan<StorageCell> cells, Span<UInt256?> values)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, cells.Length, nameof(values));
        if (reader is IBatchPersistenceReader batch) batch.GetSlotsAfterCacheMiss(cells, values);
        else reader.GetSlots(cells, values);
    }

    public static void GetSlots(this IPersistence.IPersistenceReader reader, ReadOnlySpan<StorageCell> cells, Span<UInt256?> values)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(values.Length, cells.Length, nameof(values));
        if (cells.Length >= MinBatchSize && reader is IBatchPersistenceReader batch)
        {
            batch.GetSlots(cells, values);
            return;
        }

        int completed = 0;
        try
        {
            for (; completed < cells.Length; completed++)
            {
                ref readonly StorageCell cell = ref cells[completed];
                UInt256 value = default;
                values[completed] = reader.TryGetSlot(cell.Address, in cell.Index, ref value) ? value : null;
            }
        }
        catch
        {
            values[completed..].Clear();
            throw;
        }
    }
}
