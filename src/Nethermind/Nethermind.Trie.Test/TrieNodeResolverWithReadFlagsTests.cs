// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class TrieNodeResolverWithReadFlagsTests
{
    [Test]
    public void LoadRlp_shouldPassTheFlag()
    {
        ReadFlags theFlags = ReadFlags.HintCacheMiss;
        TestMemDb memDb = new();
        ITrieStore trieStore = new TrieStore(memDb, LimboLogs.Instance);
        TrieNodeResolverWithReadFlags resolver = new(trieStore.GetTrieStore(null), theFlags);

        Hash256 theKeccak = TestItem.KeccakA;
        memDb[NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, theKeccak)] = TestItem.KeccakA.BytesToArray();
        resolver.LoadRlp(TreePath.Empty, theKeccak);

        memDb.KeyWasReadWithFlags(NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, theKeccak), theFlags);
    }

    [Test]
    public void LoadRlp_combine_passed_flaeg()
    {
        ReadFlags theFlags = ReadFlags.HintCacheMiss;
        TestMemDb memDb = new();
        ITrieStore trieStore = new TrieStore(memDb, LimboLogs.Instance);
        TrieNodeResolverWithReadFlags resolver = new(trieStore.GetTrieStore(null), theFlags);

        Hash256 theKeccak = TestItem.KeccakA;
        memDb[NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, theKeccak)] = TestItem.KeccakA.BytesToArray();
        memDb[NodeStorage.GetHalfPathNodeStoragePath(null, new TreePath(TestItem.KeccakA, 64), theKeccak)] = TestItem.KeccakA.BytesToArray();
        resolver.LoadRlp(TreePath.Empty, theKeccak, ReadFlags.HintReadAhead);
        resolver.LoadRlp(new TreePath(TestItem.KeccakA, 64), theKeccak, ReadFlags.HintReadAhead);
        memDb.KeyWasReadWithFlags(NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, theKeccak), theFlags);
        memDb.KeyWasReadWithFlags(NodeStorage.GetHalfPathNodeStoragePath(null, new TreePath(TestItem.KeccakA, 64), theKeccak), theFlags | ReadFlags.HintReadAhead);
    }

    [Test]
    public void LoadRlp_shouldPassTheFlag_forStorageStoreAlso()
    {
        ReadFlags theFlags = ReadFlags.HintCacheMiss;
        TestMemDb memDb = new();
        ITrieStore trieStore = new TrieStore(memDb, LimboLogs.Instance);
        ITrieNodeResolver resolver = new TrieNodeResolverWithReadFlags(trieStore.GetTrieStore(null), theFlags);
        resolver = resolver.GetStorageTrieNodeResolver(TestItem.KeccakA);

        Hash256 theKeccak = TestItem.KeccakA;
        memDb[NodeStorage.GetHalfPathNodeStoragePath(TestItem.KeccakA, TreePath.Empty, theKeccak)] = TestItem.KeccakA.BytesToArray();
        resolver.LoadRlp(TreePath.Empty, theKeccak);

        memDb.KeyWasReadWithFlags(NodeStorage.GetHalfPathNodeStoragePath(TestItem.KeccakA, TreePath.Empty, theKeccak), theFlags);
    }
}
