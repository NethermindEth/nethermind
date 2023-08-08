// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public interface IKeyValueStore : IReadOnlyKeyValueStore
    {
        new byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key);
            set => Set(key, value);
        }

        void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None);
    }

    public interface IReadOnlyKeyValueStore
    {
        byte[]? this[ReadOnlySpan<byte> key] => Get(key);

        byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None);
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
