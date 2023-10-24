// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public static class KeyValueStoreExtensions
    {
        public static IWriteBatch LikeABatch(this IKeyValueStoreWithBatching keyValueStore)
        {
            return LikeABatch(keyValueStore, null);
        }

        public static IWriteBatch LikeABatch(this IKeyValueStoreWithBatching keyValueStore, Action? onDispose)
        {
            return new FakeWriteBatch(keyValueStore, onDispose);
        }

        public static void Set(this IKeyValueStore db, Keccak key, byte[] value)
        {
            if (db.PreferWriteByArray)
            {
                db[key.Bytes] = value;
                return;
            }
            db.PutSpan(key.Bytes, value);
        }

        public static void Set(this IKeyValueStore db, Keccak key, Span<byte> value)
        {
            db.PutSpan(key.Bytes, value);
        }

        public static void Set(this IKeyValueStore db, Keccak key, CappedArray<byte> value)
        {
            if (value.IsUncapped && db.PreferWriteByArray)
            {
                db[key.Bytes] = value.ToArray();
                return;
            }

            db.PutSpan(key.Bytes, value);
        }
    }
}
