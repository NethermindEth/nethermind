// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Consensus.ZkEvm.Test.Stateless;

/// <summary>Tests that the guest's witness trie store leaves the storage trie walk to the Airbender guest: elsewhere a
/// storage root loads unchecked, an overlong path included.</summary>
/// <remarks>Without hashing: in a ZK_EVM build the Keccak permutation is a zkVM precompile that throws outside the guest, so
/// the root is committed under a hash of the test's choosing rather than listed by the witness.</remarks>
[Parallelizable(ParallelScope.All)]
public class GuestWitnessTriePathsTests
{
    private const int KeyNibbles = 64;
    private static readonly Hash256 Address = new("0x0000000000000000000000000000000000000000000000000000000000000001");
    private static readonly Hash256 Root = new("0x0000000000000000000000000000000000000000000000000000000000000002");

    [Test]
    public void Storage_root_loads_unwalked_off_airbender([Values] bool overlong)
    {
        ITrieStore store = WitnessNodeStorage.CreateTrieStore(ArrayPoolList<byte[]>.Empty(), out WitnessTriePaths paths);
        byte[] leaf = Rlp.Encode(Rlp.Encode(HexPrefix.ToBytes(new byte[KeyNibbles + (overlong ? 1 : 0)], isLeaf: true)), Rlp.Encode([0x01])).Bytes;
        TreePath path = TreePath.Empty;
        using (ICommitter committer = store.BeginCommit(Address, null, WriteFlags.None))
        {
            committer.CommitNode(ref path, new TrieNode(NodeType.Unknown, Root, leaf));
        }

        paths.AddStorageRoot(Root);

        Assert.That(store.TryLoadRlp(Address, path, Root), Is.EqualTo(leaf));
    }
}
