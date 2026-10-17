// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Trie.NodeLog;

/// <summary>A trie's root hash with the pointer of its root record, 0 when unknown.</summary>
internal readonly struct TrieRoot(in ValueHash256 hash, ulong pointer)
{
    public static TrieRoot Empty => new(ValueKeccak.EmptyTreeHash, 0);

    public readonly ValueHash256 Hash = hash;
    public readonly ulong Pointer = pointer;

    public Hash256 ToHash256() => new(Hash);
}
