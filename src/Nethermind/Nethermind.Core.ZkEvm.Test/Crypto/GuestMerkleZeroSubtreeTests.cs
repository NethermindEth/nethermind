// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using Nethermind.Serialization.Ssz.Merkleization;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Crypto;

/// <summary>
/// The guest's zero-subtree test in <c>Merkle.zkevm.cs</c>, which reads the baked zero-hash chain in place
/// by level rather than through <see cref="Merkle.ZeroHashes"/>.
/// </summary>
public class GuestMerkleZeroSubtreeTests
{
    private const int Levels = 64;

    [Test]
    public void Zero_hash_is_a_zero_subtree_at_its_own_level_only([Range(0, Levels - 1)] int level)
    {
        Assert.That(Merkle.IsZeroSubtree(Merkle.ZeroHashes[level], level), Is.True);
        Assert.That(Merkle.IsZeroSubtree(Merkle.ZeroHashes[(level + 1) % Levels], level), Is.False);
    }

    [Test]
    public void Node_differing_in_any_limb_is_not_a_zero_subtree([Values(0, 1, 2, 3)] int limb, [Values(0, 1, Levels - 1)] int level)
    {
        UInt256 zeroHash = Merkle.ZeroHashes[level];
        UInt256 node = new(
            zeroHash.u0 ^ (limb == 0 ? 1UL : 0),
            zeroHash.u1 ^ (limb == 1 ? 1UL : 0),
            zeroHash.u2 ^ (limb == 2 ? 1UL : 0),
            zeroHash.u3 ^ (limb == 3 ? 1UL : 0));

        Assert.That(Merkle.IsZeroSubtree(node, level), Is.False);
    }
}
