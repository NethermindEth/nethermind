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
}

public static class BatchPersistenceReaderExtensions
{
    internal const int BatchSize = 64;
    internal const int MinBatchSize = 8;

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
