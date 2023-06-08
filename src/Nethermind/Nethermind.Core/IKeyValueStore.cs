// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public interface IKeccakValueStore : IReadOnlyKeccakValueStore
    {
        new byte[]? this[in ValueKeccak key]
        {
            get => Get(key, ReadFlags.None);
            set => Set(key, value, WriteFlags.None);
        }

        void Set(in ValueKeccak key, byte[]? value, WriteFlags flags = WriteFlags.None);
    }

    public interface IReadOnlyKeccakValueStore
    {
        byte[]? this[in ValueKeccak key] => Get(key, ReadFlags.None);

        byte[]? Get(in ValueKeccak key, ReadFlags flags = ReadFlags.None);
    }

    public interface IKeyValueStore : IReadOnlyKeyValueStore, IKeccakValueStore
    {
        new byte[]? this[ReadOnlySpan<byte> key]
        {
            get => Get(key, ReadFlags.None);
            set => Set(key, value, WriteFlags.None);
        }

        void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None);

        byte[]? IKeccakValueStore.this[in ValueKeccak key]
        {
            get => Get(key.Bytes, ReadFlags.None);
            set => Set(key.Bytes, value, WriteFlags.None);
        }

        void IKeccakValueStore.Set(in ValueKeccak key, byte[]? value, WriteFlags flags) => Set(key.Bytes, value, flags);
    }

    public interface IReadOnlyKeyValueStore : IReadOnlyKeccakValueStore
    {
        byte[]? this[ReadOnlySpan<byte> key] => Get(key, ReadFlags.None);

        byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None);

        byte[]? IReadOnlyKeccakValueStore.this[in ValueKeccak key] => Get(key.Bytes, ReadFlags.None);

        byte[]? IReadOnlyKeccakValueStore.Get(in ValueKeccak key, ReadFlags flags) => Get(key.Bytes, flags);
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
