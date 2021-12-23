//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Db.Files
{
    /// <summary>
    /// An in memory map for mapping between Keccak and its position in the log
    /// </summary>
    public class PersistentKeccakMap
    {
        private readonly PersistentLog _log;

        struct Entry
        {
            public long _key0;

            /// <summary>
            /// The second part of the key.
            /// </summary>
            public long _key1;

            /// <summary>
            /// The third part of the key.
            /// </summary>
            public long _key2;

            /// <summary>
            /// The third part of the key.
            /// </summary>
            public long _key3;

            /// <summary>
            /// The id within the log
            /// </summary>
            public long _value;

            /// <summary>
            /// The pointer to next value.
            /// </summary>
            public long _next;
        }

        private readonly long[] _buckets;
        private readonly long _addressMask;

        public PersistentKeccakMap(int buckets, PersistentLog log)
        {
            int sizeLog2 = buckets.Log2();
            int size = 1 << sizeLog2;
            _addressMask = (1 << sizeLog2) - 1;
            _buckets = new long[size];

            _log = log;
        }

        public void Put(byte[] key, long value)
        {
            GuardKey(key);

            if (value <= 0)
            {
                throw new ArgumentException("Only positive values are allowed", nameof(value));
            }

            ref byte b = ref key[0];

            long key0 = Unsafe.ReadUnaligned<long>(ref b);
            long key1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref b, sizeof(long)));
            long key2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref b, sizeof(long)*2));
            long key3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref b, sizeof(long)*3));

            Put(key0, key1, key2, key3, value);
        }

        public void Put(long key0, long key1, long key2, long key3, long value)
        {
            Entry e;

            e._key0 = key0;
            e._key1 = key1;
            e._key2 = key2;
            e._key3 = key3;
            e._value = value;

            ref long bucket = ref GetBucket(key0);

            while (true)
            {
                e._next = Volatile.Read(ref bucket);

                Span<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref e, 1));
                long head = _log.Write(bytes);

                if (Interlocked.CompareExchange(ref bucket, head, e._next) == e._next)
                {
                    break;
                }
            }
        }

        public static void GuardKey(byte[] key)
        {
            if (key.Length != Keccak.Size)
            {
                throw new ArgumentException("Only keccaks are allowed. Pass 32 bytes.", nameof(key));
            }
        }

        ref long GetBucket(long key0) => ref _buckets[(int)(key0 & _addressMask)];

        [SkipLocalsInit]
        public bool TryGet(byte[] key, out long value)
        {
            GuardKey(key);

            ref byte b = ref key[0];
            long key0 = Unsafe.ReadUnaligned<long>(ref b);
            long key1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref key[0], sizeof(long)));
            long key2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref key[0], sizeof(long) * 2));
            long key3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref key[0], sizeof(long) * 3));

            ref long bucket = ref GetBucket(key0);

            long current = Volatile.Read(ref bucket);

            Span<Entry> entries = stackalloc Entry[1];
            ref Entry entry = ref entries[0];
            Span<byte> bytes = MemoryMarshal.AsBytes(entries);

            while (current != 0)
            {
                // read in place
                _log.Read(current, bytes);
                
                if (entry._key0 == key0 &&
                    entry._key1 == key1 &&
                    entry._key2 == key2 &&
                    entry._key3 == key3)
                {
                    value = entry._value;
                    return true;
                }

                current = entry._next;
            }

            value = default;
            return false;
        }

        public void RestoreFrom(FileStream checkpoint)
        {
            checkpoint.Read(MemoryMarshal.AsBytes<long>(_buckets));
        }

        public void CheckpointTo(FileStream checkpoint)
        {
            checkpoint.Write(MemoryMarshal.AsBytes<long>(_buckets));
        }
    }
}
