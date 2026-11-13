// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Core;

/// <summary>
/// Compares <see cref="AddressAsKey"/> by its 20 bytes and lets a set be probed with the raw bytes, without an
/// <see cref="Address"/> instance.
/// </summary>
public sealed class AddressAsKeyComparer : IEqualityComparer<AddressAsKey>, IAlternateEqualityComparer<ReadOnlySpan<byte>, AddressAsKey>
{
    public static AddressAsKeyComparer Instance { get; } = new();

    private AddressAsKeyComparer() { }

    public bool Equals(AddressAsKey x, AddressAsKey y) => x.Equals(in y);

    public int GetHashCode(AddressAsKey obj) => unchecked((int)obj.GetHashCode64());

    public bool Equals(ReadOnlySpan<byte> alternate, AddressAsKey other)
        => other.Value is not null
            && alternate.Length == Address.Size
            && Address.BytesEqual(ref MemoryMarshal.GetReference(alternate), ref MemoryMarshal.GetReference(other.Value.Bytes));

    public int GetHashCode(ReadOnlySpan<byte> alternate)
        => alternate.Length == Address.Size ? unchecked((int)SpanExtensions.FastHash64For20Bytes(ref MemoryMarshal.GetReference(alternate))) : 0;

    public AddressAsKey Create(ReadOnlySpan<byte> alternate) => new Address(alternate);
}
