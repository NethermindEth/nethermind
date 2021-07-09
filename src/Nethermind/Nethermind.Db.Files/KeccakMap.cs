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
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Db.Files
{
    /// <summary>
    /// An in memory map for entries that uses a bitmask addressing to access values.
    ///
    /// It uses an array for <see cref="_size"/> entries. Once a collision is found, another layer is used that keeps recently written values in the top. This means that:
    /// - when no collision, the lookup should be fast
    /// - when was a hash collision, the lookup will fail on fast path but should pick up the slow so it should be lookup in an array and they lookup by a ref
    ///
    /// This means that making the initial array big enough should result in some values written in O(1).
    /// </summary>
    public class KeccakMap
    {
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
            public int _next;
        }

        private readonly int[] _buckets;
        private readonly long _addressMask;
        private readonly Entry[] _entries;
        private int _next = 1;

        public KeccakMap(int buckets, int allocate)
        {
            // 1st level
            {
                int sizeLog2 = buckets.Log2();
                int size = 1 << sizeLog2;
                _addressMask = (1 << sizeLog2) - 1;
                _buckets = new int[size];
            }

            _entries = new Entry[allocate];
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

        private void Put(long key0, long key1, long key2, long key3, long value)
        {
            int head = Interlocked.Increment(ref _next);
            ref Entry e = ref _entries[head];

            e._key0 = key0;
            e._key1 = key1;
            e._key2 = key2;
            e._key3 = key3;
            e._value = value;

            ref int bucket = ref GetBucket(key0);

            while (true)
            {
                e._next = Volatile.Read(ref bucket);
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

        ref int GetBucket(long key0) => ref _buckets[(int)(key0 & _addressMask)];

        [SkipLocalsInit]
        public bool TryGet(byte[] key, out long value)
        {
            GuardKey(key);

            ref byte b = ref key[0];
            long key0 = Unsafe.ReadUnaligned<long>(ref b);
            long key1 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref key[0], sizeof(long)));
            long key2 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref key[0], sizeof(long) * 2));
            long key3 = Unsafe.ReadUnaligned<long>(ref Unsafe.Add(ref key[0], sizeof(long) * 3));

            ref int bucket = ref GetBucket(key0);

            int current = Volatile.Read(ref bucket);

            while (current != 0)
            {
                ref Entry entry = ref _entries[current];
                
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

        public void CopyTo(KeccakMap other)
        {
            for (int i = 0; i < _buckets.Length; i++)
            {
                int current = Volatile.Read(ref _buckets[i]);

                while (current != 0)
                {
                    ref Entry entry = ref _entries[current];

                    other.Put(entry._key0, entry._key1, entry._key2, entry._key3, entry._value);
                    current = entry._next;
                }
            }
        }

        public void Clear()
        {
            _buckets.AsSpan().Clear();
            _entries.AsSpan().Clear();
            _next = 1;
        }
    }
}
