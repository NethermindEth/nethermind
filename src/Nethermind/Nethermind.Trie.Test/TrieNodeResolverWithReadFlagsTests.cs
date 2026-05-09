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

[Parallelizable(ParallelScope.All)]
public class TrieNodeResolverWithReadFlagsTests
{
    [Test]
    public void LoadRlp_shouldPassTheFlag()
    {
        ReadFlags theFlags = ReadFlags.HintCacheMiss;
        TestMemDb memDb = new();
        ITrieStore trieStore = TestTrieStoreFactory.Build(memDb, LimboLogs.Instance);
        TrieNodeResolverWithReadFlags resolver = new(trieStore.GetTrieStore(null), theFlags);

        Hash256 theKeccak = TestItem.KeccakA;
        memDb[NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, theKeccak)] = TestItem.KeccakA.BytesToArray();
        resolver.LoadRlp(TreePath.Empty, theKeccak);

        memDb.KeyWasReadWithFlags(NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, theKeccak), theFlags);
    }

    [Test]
    public void LoadRlp_combine_passed_flag()
    {
        ReadFlags theFlags = ReadFlags.HintCacheMiss;
        TestMemDb memDb = new();
        ITrieStore trieStore = TestTrieStoreFactory.Build(memDb, LimboLogs.Instance);
        TrieNodeResolverWithReadFlags resolver = new(trieStore.GetTrieStore(null), theFlags);

        Hash256 theKeccak = TestItem.KeccakA;
        memDb[NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, theKeccak)] = TestItem.KeccakA.BytesToArray();
        resolver.LoadRlp(TreePath.Empty, theKeccak, ReadFlags.HintReadAhead);

        memDb.KeyWasReadWithFlags(NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, theKeccak), theFlags | ReadFlags.HintReadAhead);
    }

    [Test]
    public void LoadRlp_shouldPassTheFlag_forStorageStoreAlso()
    {
        ReadFlags theFlags = ReadFlags.HintCacheMiss;
        TestMemDb memDb = new();
        ITrieStore trieStore = TestTrieStoreFactory.Build(memDb, LimboLogs.Instance);
        ITrieNodeResolver resolver = new TrieNodeResolverWithReadFlags(trieStore.GetTrieStore(null), theFlags);
        resolver = resolver.GetStorageTrieNodeResolver(TestItem.KeccakA);

        Hash256 theKeccak = TestItem.KeccakA;
        memDb[NodeStorage.GetHalfPathNodeStoragePath(TestItem.KeccakA, TreePath.Empty, theKeccak)] = TestItem.KeccakA.BytesToArray();
        resolver.LoadRlp(TreePath.Empty, theKeccak);

        memDb.KeyWasReadWithFlags(NodeStorage.GetHalfPathNodeStoragePath(TestItem.KeccakA, TreePath.Empty, theKeccak), theFlags);
    }

    [Test]
    public void ReadOnlyTraversal_shouldPreserveReadFlags()
    {
        TestResolver sourceResolver = new();
        TrieNodeResolverWithReadFlags resolver = new(sourceResolver, ReadFlags.HintCacheMiss);
        ITrieNodeResolver readOnlyResolver = ((ITrieNodeResolverSource)resolver).GetReadOnlyTraversalResolver()!;

        readOnlyResolver.LoadRlp(TreePath.Empty, TestItem.KeccakA, ReadFlags.HintReadAhead);

        Assert.That(sourceResolver.ReadOnlyResolver.LastFlags, Is.EqualTo(ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead));
    }

    private sealed class TestResolver : ITrieNodeResolver, ITrieNodeResolverSource
    {
        public readonly TestReadOnlyResolver ReadOnlyResolver = new();

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => [];

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => [];

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => this;

        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;

        public ITrieNodeResolver? GetReadOnlyTraversalResolver() => ReadOnlyResolver;
    }

    private sealed class TestReadOnlyResolver : ITrieNodeResolver
    {
        public ReadFlags LastFlags { get; private set; }

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);

        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            LastFlags = flags;
            return [];
        }

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            LastFlags = flags;
            return [];
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => this;

        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
    }
}
