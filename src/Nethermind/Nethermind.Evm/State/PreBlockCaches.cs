// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.State;

public readonly struct PrecompileCacheKey(Address address, ReadOnlyMemory<byte> data) : IEquatable<PrecompileCacheKey>
{
    private Address Address { get; } = address;
    private ReadOnlyMemory<byte> Data { get; } = data;
    public bool Equals(PrecompileCacheKey other) => Address == other.Address && Data.Span.SequenceEqual(other.Data.Span);
    public override bool Equals(object? obj) => obj is PrecompileCacheKey other && Equals(other);
    public override int GetHashCode() => Data.Span.FastHash() ^ Address.GetHashCode();
}
