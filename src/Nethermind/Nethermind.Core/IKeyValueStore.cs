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

        /// <summary>
        /// Get, C-style. Write the output to <paramref name="output" /> span and return the length of written data.
        /// Cannot differentiate if the data is missing or does not exist. Throws if <paramref name="output" /> is not large enough.
        /// </summary>
        /// <param name="key">Key whose associated value should be read.</param>
        /// <param name="output">Destination buffer to receive the value bytes; must be large enough to hold the data.</param>
        /// <param name="flags">Read behavior flags that control how the value is retrieved.</param>
        /// <returns>The number of bytes written into <paramref name="output" />.</returns>
        int Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags = ReadFlags.None)
        {
            Span<byte> span = GetSpan(key, flags);
            try
            {
                if (span.IsNull())
                {
                    return 0;
                }
                span.CopyTo(output);
                return span.Length;
            }
            finally
            {
                DangerousReleaseMemory(span);
            }
        }

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

    public interface IMergeableKeyValueStore : IWriteOnlyKeyValueStore
    {
        void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None);
    }

    public interface ISortedKeyValueStore : IReadOnlyKeyValueStore
    {
        byte[]? FirstKey { get; }
        byte[]? LastKey { get; }

        ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive);
    }

    /// <summary>
    /// Provides the capability to create read-only snapshots of a key-value store.
    /// </summary>
    /// <remarks>
    /// Implementations expose a <see cref="IKeyValueStoreSnapshot" /> that represents a consistent,
    /// point-in-time view of the underlying store. The snapshot is not affected by subsequent writes
    /// to the parent store, but it reflects the state as it existed when <see cref="CreateSnapshot" />
    /// was called.
    /// </remarks>
    public interface IKeyValueStoreWithSnapshot
    {
        /// <summary>
        /// Creates a new read-only snapshot of the current state of the key-value store.
        /// </summary>
        /// <returns>
        /// An <see cref="IKeyValueStoreSnapshot" /> that can be used to perform read operations
        /// against a stable view of the data.
        /// </returns>
        /// <remarks>
        /// The returned snapshot must be disposed when no longer needed in order to release any
        /// resources that may be held by the underlying storage engine (for example, pinned
        /// iterators or file handles). The snapshot is guaranteed to be consistent with the
        /// state of the store at the time of creation, regardless of concurrent modifications
        /// performed afterwards.
        /// </remarks>
        IKeyValueStoreSnapshot CreateSnapshot();
    }

    /// <summary>
    /// Represents a read-only, point-in-time view of the data in an <see cref="IKeyValueStore" />.
    /// </summary>
    /// <remarks>
    /// A snapshot exposes the <see cref="IReadOnlyKeyValueStore" /> API and is isolated from
    /// subsequent mutations to the parent store. Implementations are expected to provide a
    /// consistent view of the data as it existed when the snapshot was created. Callers must
    /// dispose the snapshot via <see cref="IDisposable.Dispose" /> when finished with it to
    /// free any underlying resources.
    /// </remarks>
    public interface IKeyValueStoreSnapshot : IReadOnlyKeyValueStore, IDisposable
    {
    }

    /// <summary>
    /// Represent a sorted view of a `ISortedKeyValueStore`.
    /// </summary>
    public interface ISortedView : IDisposable
    {
        public bool StartBefore(ReadOnlySpan<byte> value);
        public bool MoveNext();
        public ReadOnlySpan<byte> CurrentKey { get; }
        public ReadOnlySpan<byte> CurrentValue { get; }
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
