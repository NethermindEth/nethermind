// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.ObjectModel;
using Nethermind.Core;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merkleization;

using SHA256 =
#if ZK_EVM
    ShaMerkleTree.Sha256;
#else
    System.Security.Cryptography.SHA256;
#endif

public class ShaMerkleTree(IKeyValueStore<ulong> keyValueStore) : MerkleTree(keyValueStore)
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

    protected override void Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> target) => HashStatic(a, b, target);

#if ZK_EVM
    internal static class Sha256
    {
        internal static bool TryHashData(ReadOnlySpan<byte> data, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = System.Security.Cryptography.SHA256.HashSizeInBytes;

            Nethermind.Zkvm.Abstractions.Accelerators.Sha256(data, destination);

            return true;
        }
    }
#endif
}
