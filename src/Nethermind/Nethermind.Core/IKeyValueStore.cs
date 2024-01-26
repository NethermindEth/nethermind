// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;

namespace Nethermind.Core
{
    public interface IKeyValueStore : IReadOnlyKeyValueStore, IWriteOnlyKeyValueStore
    {
        new byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key);
            set => Set(key, value);
        }
    }

    public interface IReadOnlyKeyValueStore
    {
        byte[]? this[ReadOnlySpan<byte> key] => Get(key);

        byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None);

        /// <summary>
        /// Return span. Must call `DangerousReleaseMemory` or there can be some leak.
        /// </summary>
        /// <param name="key"></param>
        /// <returns>Can return null or empty Span on missing key</returns>
        Span<byte> GetSpan(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => Get(key, flags);

        bool KeyExists(ReadOnlySpan<byte> key)
        {
            Span<byte> span = GetSpan(key);
            bool result = span.IsNull();
            DangerousReleaseMemory(span);
            return result;
        }

        void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }
    }

    public interface IWriteOnlyKeyValueStore
    {
        byte[]? this[ReadOnlySpan<byte> key]
        {
            set => Set(key, value);
        }

        void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None);

        /// <summary>
        /// Some store keep the input array directly. (eg: CachingStore), and therefore passing the value by array
        /// is preferable. Unless you plan to reuse the array somehow (pool), then you'd just use span.
        /// </summary>
        public bool PreferWriteByArray => false;
        void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => Set(key, value.ToArray(), flags);
        void Remove(ReadOnlySpan<byte> key) => Set(key, null);
    }

    [Flags]
    public enum ReadFlags
    {
        None = 0,

        // Hint that the workload is likely to not going to benefit from caching and should skip any cache handling
        // to reduce CPU usage
        HintCacheMiss = 1,

        // Hint that the workload is likely to need the next value in the sequence and should prefetch it.
        HintReadAhead = 2,

        // Used for full pruning db to skip duplicate read
        SkipDuplicateRead = 4,
    }

    [Flags]
    public enum WriteFlags
    {
        None = 0,

        // Hint that this is a low priority write
        LowPriority = 1,

        // Hint that this write does not require durable writes, as if it crash, it'll start over anyway.
        DisableWAL = 2,

        LowPriorityAndNoWAL = LowPriority | DisableWAL,
    }
}
