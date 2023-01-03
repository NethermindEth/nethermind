// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using Nethermind.Int256;

namespace Nethermind.Core;

[DebuggerDisplay("{Address}->{Chunk}")]
public readonly struct CodeChunk : IEquatable<CodeChunk>
{
    public Address Address { get; }
    public UInt256 Chunk { get; }

    public CodeChunk(Address address, in UInt256 chunk)
    {
        Address = address;
        Chunk = chunk;
    }

    public bool Equals(CodeChunk other)
    {
        return Chunk.Equals(other.Chunk) && Address.Equals(other.Address);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(null, obj))
        {
            return false;
        }

        return obj is CodeChunk address && Equals(address);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            return (Address.GetHashCode() * 397) ^ Chunk.GetHashCode();
        }
    }

    public override string ToString()
    {
        return $"{Address}.{Chunk}";
    }

    public static bool operator ==(CodeChunk left, CodeChunk right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(CodeChunk left, CodeChunk right)
    {
        return !(left == right);
    }
}
