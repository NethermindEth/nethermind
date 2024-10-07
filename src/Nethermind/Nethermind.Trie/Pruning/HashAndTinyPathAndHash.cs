// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

[StructLayout(LayoutKind.Auto)]
internal readonly struct HashAndTinyPathAndHash : IEquatable<HashAndTinyPathAndHash>
{
    public readonly ValueHash256 hash;
    public readonly TinyTreePath path;
    public readonly ValueHash256 valueHash;

    public HashAndTinyPathAndHash(Hash256? hash, in TinyTreePath path, in ValueHash256 valueHash)
    {
        this.hash = hash ?? default;
        this.path = path;
        this.valueHash = valueHash;
    }
    public HashAndTinyPathAndHash(in ValueHash256 hash, in TinyTreePath path, in ValueHash256 valueHash)
    {
        this.hash = hash;
        this.path = path;
        this.valueHash = valueHash;
    }

    public bool Equals(HashAndTinyPathAndHash other) => hash == other.hash && path.Equals(in other.path) && valueHash.Equals(in other.valueHash);
    public override bool Equals(object? obj) => obj is HashAndTinyPath other && Equals(other);
    public override int GetHashCode()
    {
        var hashHash = hash != default ? hash.GetHashCode() : 1;
        return valueHash.GetChainedHashCode((uint)path.GetHashCode()) ^ hashHash;
    }
}
