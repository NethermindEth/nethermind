// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Trie;

namespace Nethermind.State;

public class PreBlockCaches
{
    public ConcurrentDictionary<StorageCell, byte[]> StorageCache { get; } = new(Environment.ProcessorCount * 2, 4096 * 4);
    public ConcurrentDictionary<AddressAsKey, Account> StateCache { get; } = new(Environment.ProcessorCount * 2, 4096 * 4);
    public ConcurrentDictionary<NodeKey, byte[]?> RlpCache { get; } = new(Environment.ProcessorCount * 2, 4096 * 4);
    public ConcurrentDictionary<PrecompileCacheKey, (ReadOnlyMemory<byte>, bool)> PrecompileCache { get; } = new(Environment.ProcessorCount * 2, 4096 * 4);

    public bool Clear()
    {
        bool isDirty = !StorageCache.IsEmpty || !StateCache.IsEmpty || !RlpCache.IsEmpty;
        if (isDirty)
        {
            StorageCache.Clear();
            StateCache.Clear();
            RlpCache.Clear();
        }

        return isDirty;
    }

    public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data) : IEquatable<PrecompileCacheKey>
    {
        private Address Address { get; } = address;
        private ReadOnlyMemory<byte> Data { get; } = data;
        public bool Equals(PrecompileCacheKey other) => Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
        public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
        public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode();
    }
}
