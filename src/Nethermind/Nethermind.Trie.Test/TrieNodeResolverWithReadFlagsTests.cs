// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class TrieNodeResolverWithReadFlagsTests
{
    private static readonly byte[] NodeRlp = [1, 2, 3];
    private static readonly Hash256 NodeHash = Keccak.Compute("node");

    [Test]
    public void LoadRlp_shouldPassTheDefaultFlag()
    {
        IScopedTrieStore baseResolver = Substitute.For<IScopedTrieStore>();
        baseResolver.LoadRlp(TreePath.Empty, NodeHash, ReadFlags.HintCacheMiss).Returns(NodeRlp);
        TrieNodeResolverWithReadFlags resolver = new(baseResolver, ReadFlags.HintCacheMiss);

        Assert.That(resolver.LoadRlp(TreePath.Empty, NodeHash), Is.EqualTo(NodeRlp));
        baseResolver.Received(1).LoadRlp(TreePath.Empty, NodeHash, ReadFlags.HintCacheMiss);
    }

    [Test]
    public void LoadRlp_should_combine_explicit_and_default_flags()
    {
        IScopedTrieStore baseResolver = Substitute.For<IScopedTrieStore>();
        baseResolver.LoadRlp(TreePath.Empty, NodeHash, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead).Returns(NodeRlp);
        TrieNodeResolverWithReadFlags resolver = new(baseResolver, ReadFlags.HintCacheMiss);

        Assert.That(resolver.LoadRlp(TreePath.Empty, NodeHash, ReadFlags.HintReadAhead), Is.EqualTo(NodeRlp));
        baseResolver.Received(1).LoadRlp(TreePath.Empty, NodeHash, ReadFlags.HintCacheMiss | ReadFlags.HintReadAhead);
    }

    [Test]
    public void LoadRlp_should_preserve_flags_when_switching_to_storage_resolver()
    {
        IScopedTrieStore baseResolver = Substitute.For<IScopedTrieStore>();
        ITrieNodeResolver storageResolver = Substitute.For<ITrieNodeResolver>();
        baseResolver.GetStorageTrieNodeResolver(TestItem.KeccakA).Returns(storageResolver);
        storageResolver.LoadRlp(TreePath.Empty, NodeHash, ReadFlags.HintCacheMiss).Returns(NodeRlp);
        TrieNodeResolverWithReadFlags resolver = new(baseResolver, ReadFlags.HintCacheMiss);

        ITrieNodeResolver storage = resolver.GetStorageTrieNodeResolver(TestItem.KeccakA);
        Assert.That(storage.LoadRlp(TreePath.Empty, NodeHash), Is.EqualTo(NodeRlp));
        storageResolver.Received(1).LoadRlp(TreePath.Empty, NodeHash, ReadFlags.HintCacheMiss);
    }

    [Test]
    public void Full_scan_should_request_read_ahead()
    {
        RecordingScopedTrieStore store = new(new RawScopedTrieStore(new TestNodeStorage(new MemDb())));
        PatriciaTree source = new(store, LimboLogs.Instance);
        source.Set([1], [2]);
        source.Commit();

        PatriciaTree reader = new(store, source.RootHash, true, LimboLogs.Instance);
        ITreeVisitor<EmptyContext> visitor = Substitute.For<ITreeVisitor<EmptyContext>>();
        visitor.IsFullDbScan.Returns(true);

        reader.Accept(visitor, source.RootHash);

        Assert.That(store.ObservedFlags.HasFlag(ReadFlags.HintReadAhead), Is.True);
    }

    private sealed class RecordingScopedTrieStore(IScopedTrieStore inner) : IScopedTrieStore
    {
        public ReadFlags ObservedFlags { get; private set; }

        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => inner.FindCachedOrUnknown(in path, hash);

        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            ObservedFlags |= flags;
            return inner.LoadRlp(in path, hash, flags);
        }

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            ObservedFlags |= flags;
            return inner.TryLoadRlp(in path, hash, flags);
        }

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => inner.GetStorageTrieNodeResolver(address);

        public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => inner.BeginCommit(root, writeFlags);
    }
}
