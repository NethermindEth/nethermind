// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

[StructLayout(LayoutKind.Auto)]
internal readonly struct HashAndTinyPath : IEquatable<HashAndTinyPath>
{
    public readonly Hash256? addr;
    public readonly TinyTreePath path;

    public HashAndTinyPath(Hash256? hash, in TinyTreePath path)
    {
        addr = hash;
        this.path = path;
    }

    public bool Equals(HashAndTinyPath other) => addr == other.addr && path.Equals(in other.path);
    public override bool Equals(object? obj) => obj is HashAndTinyPath other && Equals(other);
    public override int GetHashCode()
    {
        int addressHash = addr?.GetHashCode() ?? 0x55555555;
        return path.GetHashCode() ^ addressHash;
    }
}
