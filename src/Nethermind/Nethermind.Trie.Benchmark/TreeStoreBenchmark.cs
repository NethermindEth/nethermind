// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Benchmark;

[MemoryDiagnoser]
public class TreeStoreBenchmark
{
    private MemDb _db = null!;

    [GlobalSetup]
    public void Setup() => _db = new MemDb();

    [Benchmark]
    public Hash256 Commit_SingleLeaf()
    {
        PatriciaTree tree = new(new RawTrieStore(_db).GetTrieStore(null), LimboLogs.Instance);
        tree.Set(TestItem.KeccakA.Bytes, TestItem.GenerateIndexedAccountRlp(1));
        tree.UpdateRootHash();
        tree.Commit();
        return tree.RootHash;
    }
}
