// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.RocksDbBindings;

namespace Nethermind.Db.Rocks;

public partial class DbOnTheRocks
{
    /// <inheritdoc/>
    void IBatchReadOnlyDb.GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder) =>
        ((IBatchReadOnlyDb)_reader).GetMany(keys, keyLength, values, decoder);

    internal void GetManyWithColumnFamily<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values,
        TDecoder decoder, IColumnFamilyHandle? family, ReadOptions readOptions) where TDecoder : IValueDecoder<T>
    {
        ThrowIfDisposing();
        if (decoder is null) throw new ArgumentNullException(nameof(decoder));
        UpdateReadMetrics(values.Length);
        try
        {
            _db.MultiGet<T, ValueDeserializer<T, TDecoder>>(keys, keyLength, values,
                new ValueDeserializer<T, TDecoder>(decoder), family, readOptions);
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
