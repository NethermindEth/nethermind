// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    public static class KeyValueStoreExtensions
    {
        public static IWriteBatch LikeABatch(this IWriteOnlyKeyValueStore keyValueStore)
        {
            return LikeABatch(keyValueStore, null);
        }

        public static IWriteBatch LikeABatch(this IWriteOnlyKeyValueStore keyValueStore, Action? onDispose)
        {
            return new FakeWriteBatch(keyValueStore, onDispose);
        }

        #region Getters

        public static byte[]? Get(this IReadOnlyKeyValueStore db, Hash256 key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
#endif

            return db[key.Bytes];
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="db"></param>
        /// <param name="key"></param>
        /// <returns>Can return null or empty Span on missing key</returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static Span<byte> GetSpan(this IReadOnlyKeyValueStore db, Hash256 key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
#endif

            return db.GetSpan(key.Bytes);
        }

        public static bool KeyExists(this IReadOnlyKeyValueStore db, Hash256 key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString)
            {
                throw new InvalidOperationException();
            }
#endif

            return db.KeyExists(key.Bytes);
        }

        public static bool KeyExists(this IReadOnlyKeyValueStore db, long key)
        {
            return db.KeyExists(key.ToBigEndianByteArrayWithoutLeadingZeros());
        }

        public static byte[]? Get(this IReadOnlyKeyValueStore db, long key) => db[key.ToBigEndianByteArrayWithoutLeadingZeros()];

        /// <summary>
        ///
        /// </summary>
        /// <param name="db"></param>
        /// <param name="key"></param>
        /// <returns>Can return null or empty Span on missing key</returns>
        public static Span<byte> GetSpan(this IReadOnlyKeyValueStore db, long key) => db.GetSpan(key.ToBigEndianByteArrayWithoutLeadingZeros());

        public static MemoryManager<byte>? GetOwnedMemory(this IReadOnlyKeyValueStore db, ReadOnlySpan<byte> key)
        {
            Span<byte> span = db.GetSpan(key);
            return span.IsNullOrEmpty() ? null : new DbSpanMemoryManager(db, span);
        }


        #endregion


        #region Setters

        public static void Set(this IWriteOnlyKeyValueStore db, Hash256 key, byte[] value, WriteFlags writeFlags = WriteFlags.None)
        {
            if (db.PreferWriteByArray)
            {
                db.Set(key.Bytes, value, writeFlags);
                return;
            }
            db.PutSpan(key.Bytes, value, writeFlags);
        }

        public static void Set(this IWriteOnlyKeyValueStore db, Hash256 key, in CappedArray<byte> value, WriteFlags writeFlags = WriteFlags.None)
        {
            if (value.IsUncapped && db.PreferWriteByArray)
            {
                db.Set(key.Bytes, value.ToArray(), writeFlags);
                return;
            }

            db.PutSpan(key.Bytes, value.AsSpan(), writeFlags);
        }

        public static void Set(this IWriteOnlyKeyValueStore db, long blockNumber, Hash256 key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None)
        {
            Span<byte> blockNumberPrefixedKey = stackalloc byte[40];
            GetBlockNumPrefixedKey(blockNumber, key, blockNumberPrefixedKey);
            db.PutSpan(blockNumberPrefixedKey, value, writeFlags);
        }

        public static void GetBlockNumPrefixedKey(long blockNumber, ValueHash256 blockHash, Span<byte> output)
        {
            blockNumber.WriteBigEndian(output);
            blockHash!.Bytes.CopyTo(output[8..]);
        }

        public static void Set(this IWriteOnlyKeyValueStore db, in ValueHash256 key, Span<byte> value)
        {
            db.PutSpan(key.Bytes, value);
        }

        public static void Delete(this IWriteOnlyKeyValueStore db, Hash256 key)
        {
            db.Remove(key.Bytes);
        }

        public static void Delete(this IWriteOnlyKeyValueStore db, long key)
        {
            db.Remove(key.ToBigEndianByteArrayWithoutLeadingZeros());
        }

        [SkipLocalsInit]
        public static void Delete(this IWriteOnlyKeyValueStore db, long blockNumber, Hash256 hash)
        {
            Span<byte> key = stackalloc byte[40];
            GetBlockNumPrefixedKey(blockNumber, hash, key);
            db.Remove(key);
        }

        public static void Set(this IWriteOnlyKeyValueStore db, long key, byte[] value)
        {
            db[key.ToBigEndianByteArrayWithoutLeadingZeros()] = value;
        }

        #endregion
    }
}
