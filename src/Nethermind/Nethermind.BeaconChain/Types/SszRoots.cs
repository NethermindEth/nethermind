// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>SSZ hash-tree-root helpers shared across the beacon chain plugin.</summary>
public static class SszRoots
{
    /// <summary>Computes <c>hash_tree_root(value)</c> of an SSZ container.</summary>
    public static Hash256 HashTreeRoot<T>(T value) where T : class, ISszCodec<T>
    {
        T.Merkleize(value, out UInt256 root);
        return new Hash256(root.ToLittleEndian());
    }
}
