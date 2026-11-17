// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

public partial class DbOnTheRocks
{
    private const int MinPrefetchedBatch = 16;

    /// <inheritdoc/>
    void IBatchReadOnlyDb.GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder, ReadFlags flags) =>
        GetManyWithColumnFamily(keys, keyLength, values, decoder, _columnFamily, flags);

    /// <remarks>
    /// The synchronous native batch reads its missing blocks one after another. On Linux an asynchronous
    /// <see cref="Prefetch"/> of the same keys starts first and overlaps those disk reads, so the batch finds most
    /// blocks in the block cache. Other platforms run the prefetch synchronously, which would only read twice.
    /// Starting a prefetch costs a few microseconds, so small batches and batches whose last key is already cached
    /// skip it.
    /// </remarks>
    internal void GetManyWithColumnFamily<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values,
        TDecoder decoder, IColumnFamilyHandle? family, ReadFlags flags) where TDecoder : IValueDecoder<T>
    {
        ThrowIfDisposing();
        ArgumentNullException.ThrowIfNull(decoder);
        UpdateReadMetrics(values.Length);
        if (OperatingSystem.IsLinux() && values.Length >= MinPrefetchedBatch && keys.Length == values.Length * keyLength
            && !IsCached(keys[^keyLength..], family))
        {
            Prefetch(keys, keyLength, family, _defaultReadOptions);
        }
        try
        {
            _db.MultiGet<T, ValueDeserializer<T, TDecoder>>(keys, keyLength, values,
                new ValueDeserializer<T, TDecoder>(decoder), family,
                (flags & ReadFlags.HintCacheMiss) != 0 ? _hintCacheMissOptions : _defaultReadOptions);
        }
        catch (RocksDbException e)
        {
            HandleFatalDbError(e);
            throw;
        }
    }

    private struct ValueDeserializer<T, TDecoder>(TDecoder decoder) : ISpanDeserializer<T>
        where TDecoder : IValueDecoder<T>
    {
        private TDecoder _decoder = decoder;

        public T Deserialize(ReadOnlySpan<byte> buffer) => _decoder.Decode(buffer);
    }
}
