// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    public static class KeyValueStoreExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void GuardKey(Hash256 key)
        {
#if DEBUG
            if (key == Keccak.OfAnEmptyString) throw new InvalidOperationException();
#endif
        }

        /// <param name="db"></param>
        extension(IReadOnlyKeyValueStore db)
        {
            public byte[]? Get(Hash256 key)
            {
                GuardKey(key);
                return db[key.Bytes];
            }

            /// <summary>
            ///
            /// </summary>
            /// <param name="key"></param>
            /// <returns>Can return null or empty Span on missing key</returns>
            /// <exception cref="InvalidOperationException"></exception>
            public Span<byte> GetSpan(Hash256 key)
            {
                GuardKey(key);
                return db.GetSpan(key.Bytes);
            }

            public bool KeyExists(Hash256 key)
            {
                GuardKey(key);
                return db.KeyExists(key.Bytes);
            }

            public bool KeyExists(long key) => db.KeyExists(key.ToBigEndianSpanWithoutLeadingZeros(out _));

            public byte[]? Get(long key) => db[key.ToBigEndianSpanWithoutLeadingZeros(out _)];
        }

        extension(IWriteOnlyKeyValueStore db)
        {
            public IWriteBatch LikeABatch(Action? onDispose = null) => new FakeWriteBatch(db, onDispose);

            public void Set(Hash256 key, byte[] value, WriteFlags writeFlags = WriteFlags.None)
            {
                if (db.PreferWriteByArray)
                {
                    db.Set(key.Bytes, value, writeFlags);
                }
                else
                {
                    db.PutSpan(key.Bytes, value, writeFlags);
                }
            }

            public void Set(Hash256 key, in CappedArray<byte> value, WriteFlags writeFlags = WriteFlags.None)
            {
                if (db.PreferWriteByArray && value.IsUncapped)
                {
                    db.Set(key.Bytes, value.UnderlyingArray, writeFlags);
                }
                else
                {
                    db.PutSpan(key.Bytes, value.AsSpan(), writeFlags);
                }
            }

            public void Set(long blockNumber, Hash256 key, ReadOnlySpan<byte> value, WriteFlags writeFlags = WriteFlags.None)
            {
                Span<byte> blockNumberPrefixedKey = stackalloc byte[40];
                GetBlockNumPrefixedKey(blockNumber, key, blockNumberPrefixedKey);
                db.PutSpan(blockNumberPrefixedKey, value, writeFlags);
            }

            public void Set(in ValueHash256 key, Span<byte> value)
            {
                db.PutSpan(key.Bytes, value);
            }

            public void Delete(Hash256 key)
            {
                db.Remove(key.Bytes);
            }

            public void Delete(long key)
            {
                db.Remove(key.ToBigEndianSpanWithoutLeadingZeros(out _));
            }

            [SkipLocalsInit]
            public void Delete(long blockNumber, Hash256 hash)
            {
                Span<byte> key = stackalloc byte[40];
                GetBlockNumPrefixedKey(blockNumber, hash, key);
                db.Remove(key);
            }

            public void Set(long key, byte[] value)
            {
                db[key.ToBigEndianSpanWithoutLeadingZeros(out _)] = value;
            }
        }

        public static void GetBlockNumPrefixedKey(long blockNumber, ValueHash256 blockHash, Span<byte> output)
        {
            blockNumber.WriteBigEndian(output);
            blockHash!.Bytes.CopyTo(output[8..]);
        }
    }
}
