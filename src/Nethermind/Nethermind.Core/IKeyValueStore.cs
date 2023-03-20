// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public interface IKeyValueStore : IReadOnlyKeyValueStore
    {
        new byte[]? this[ReadOnlySpan<byte> key] { get; set; }
    }

    public interface IReadOnlyKeyValueStore
    {
        byte[]? this[ReadOnlySpan<byte> key] { get; }

        byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return this[key];
        }
    }

    public enum ReadFlags
    {
        None,

        // Hint that the workload is likely to not going to benefit from caching and should skip any cache handling
        // to reduce CPU usage
        HintCacheMiss,
    }
}
