// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merkleization;

public class ShaMerkleTree(IKeyValueStore<ulong, byte[]> keyValueStore) : MerkleTree(keyValueStore)
{
    private static readonly Bytes32[] _zeroHashes = new Bytes32[32];

    static ShaMerkleTree()
    {
        _zeroHashes[0] = new Bytes32();
        for (int index = 1; index < 32; index++)
        {
            _zeroHashes[index] = new Bytes32();
            HashStatic(_zeroHashes[index - 1].Unwrap(), _zeroHashes[index - 1].Unwrap(), _zeroHashes[index].Unwrap());
        }
    }

    public static ReadOnlyCollection<Bytes32> ZeroHashes => Array.AsReadOnly(_zeroHashes);

    public ShaMerkleTree() : this(new MemMerkleTreeStore()) { }

    private static void HashStatic(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target)
    {
        Span<byte> combined = stackalloc byte[a.Length + b.Length];
        a.CopyTo(combined);
        b.CopyTo(combined[a.Length..]);

        SHA256.TryHashData(combined, target, out _);
    }

    protected override Bytes32[] ZeroHashesInternal => _zeroHashes;

    protected override void Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target)
    {
        HashStatic(a, b, target);
    }
}
