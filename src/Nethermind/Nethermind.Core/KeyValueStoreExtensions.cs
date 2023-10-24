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

        public static void Set(this IWriteOnlyKeyValueStore db, Keccak key, byte[] value, WriteFlags writeFlags = WriteFlags.None)
        {
            if (db.PreferWriteByArray)
            {
                db.Set(key.Bytes, value, writeFlags);
                return;
            }
            db.PutSpan(key.Bytes, value, writeFlags);
        }

        public static void Set(this IWriteOnlyKeyValueStore db, Keccak key, CappedArray<byte> value, WriteFlags writeFlags = WriteFlags.None)
        {
            if (value.IsUncapped && db.PreferWriteByArray)
            {
                db.Set(key.Bytes, value.ToArray(), writeFlags);
                return;
            }

            db.PutSpan(key.Bytes, value, writeFlags);
        }
    }
}
