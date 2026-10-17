// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Core.Crypto;

namespace Nethermind.Db
{
    public static class DbExtensions
    {
        /// <summary>Decodes a batch of fixed-length keys. Stores without batch support read one key at a time.</summary>
        /// <remarks>Missing keys leave default output values. Empty values are passed to the decoder.
        /// The decoder must not retain the input span. The key and output buffers must not overlap.</remarks>
        public static void GetMany<T, TDecoder>(this IDb db, ReadOnlySpan<byte> keys, int keyLength,
            Span<T> values, TDecoder decoder) where TDecoder : IValueDecoder<T>
        {
            if (decoder is null) throw new ArgumentNullException(nameof(decoder));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyLength);
            if ((long)keys.Length != (long)values.Length * keyLength)
                throw new ArgumentException("The key buffer must contain one fixed-length key per output.", nameof(keys));

            if (db is IBatchReadOnlyDb batch)
            {
                batch.GetMany(keys, keyLength, values, decoder);
                return;
            }

            int completed = 0;
            try
            {
                for (; completed < values.Length; completed++)
                {
                    Span<byte> value = db.GetSpan(keys.Slice(completed * keyLength, keyLength));
                    try
                    {
                        values[completed] = value.IsNull() ? default! : decoder.Decode(value);
                    }
                    finally
                    {
                        db.DangerousReleaseMemory(value);
                    }
                }
            }
            catch
            {
                values[completed..].Clear();
                throw;
            }
        }

        public static ReadOnlyDb AsReadOnly(this IDb db, bool createInMemoryWriteStore) => new(db, createInMemoryWriteStore);

        public static KeyValuePair<byte[], byte[]?>[] MultiGet(this IDb db, IEnumerable<ValueHash256> keys)
        {
            byte[][] k = keys.Select(static k => k.Bytes.ToArray()).ToArray();
            return db[k];
        }

    }
}
