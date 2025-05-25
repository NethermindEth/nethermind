// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None);

        /// <summary>
        /// Return span. Must call `DangerousReleaseMemory` or there can be some leak.
        /// </summary>
        /// <param name="key"></param>
        /// <returns>Can return null or empty Span on missing key</returns>
        Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => Get(key, flags);

        bool KeyExists(ReadOnlySpan<byte> key)
        {
            Span<byte> span = GetSpan(key);
            bool result = !span.IsNull();
            DangerousReleaseMemory(span);
            return result;
        }

        void DangerousReleaseMemory(in ReadOnlySpan<byte> span) { }
    }

    public interface IReadOnlyNativeKeyValueStore
    {
        /// <summary>
        /// Return span. Must call `DangerousReleaseSlice` or there can be some leak.
        /// </summary>
        /// <param name="key"></param>
        /// <returns>Can return null or empty Span on missing key</returns>
        ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out IntPtr handle, ReadFlags flags = ReadFlags.None);

        void DangerousReleaseHandle(IntPtr handle);
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
        void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => Set(key, value.IsNull() ? null : value.ToArray(), flags);
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

        // Shameful hack to use different pool of readahead iterator.
        // Its for snap serving performance. Halfpath state db is split into three section (top state, state, storage).
        // If they use the same iterator, then during the tree traversal, when it go back up to a certain level where
        // the section differ the iterator will need to seek back (section is physically before another section),
        // which is a lot slower.
        HintReadAhead2 = 4,
        HintReadAhead3 = 8,

        // Used for full pruning db to skip duplicate read
        SkipDuplicateRead = 16,
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
