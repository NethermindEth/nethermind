// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SnapshotBundleTests
{
    [Test]
    public void TryLoadStateRlp_UsesNodeStorageCache_ForDefaultReads()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = Keccak.Zero;
        byte[] rlp = [0xc1, 0xff];
        reader.TryLoadStateRlp(path, ReadFlags.None).Returns(rlp);

        NodeStorageCache nodeStorageCache = new() { Enabled = true };
        using SnapshotBundle bundle = CreateBundle(pool, reader, nodeStorageCache);

        Assert.That(bundle.TryLoadStateRlp(path, hash, ReadFlags.None), Is.EqualTo(rlp));
        Assert.That(bundle.TryLoadStateRlp(path, hash, ReadFlags.None), Is.EqualTo(rlp));

        reader.Received(1).TryLoadStateRlp(path, ReadFlags.None);
    }

    [Test]
    public void TryLoadStorageRlp_UsesNodeStorageCache_ForDefaultReads()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        Hash256 address = TestItem.KeccakA;
        TreePath path = TreePath.FromHexString("ab");
        Hash256 hash = Keccak.Zero;
        byte[] rlp = [0xc1, 0xee];
        reader.TryLoadStorageRlp(address, path, ReadFlags.None).Returns(rlp);

        NodeStorageCache nodeStorageCache = new() { Enabled = true };
        using SnapshotBundle bundle = CreateBundle(pool, reader, nodeStorageCache);

        Assert.That(bundle.TryLoadStorageRlp(address, path, hash, ReadFlags.None), Is.EqualTo(rlp));
        Assert.That(bundle.TryLoadStorageRlp(address, path, hash, ReadFlags.None), Is.EqualTo(rlp));

        reader.Received(1).TryLoadStorageRlp(address, path, ReadFlags.None);
    }

    [Test]
    public void TryLoadStateRlp_UsesNodeStorageCache_ForReadAheadHints()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = Keccak.Zero;
        byte[] rlp = [0xc1, 0xff];
        reader.TryLoadStateRlp(path, ReadFlags.HintReadAhead).Returns(rlp);

        NodeStorageCache nodeStorageCache = new() { Enabled = true };
        using SnapshotBundle bundle = CreateBundle(pool, reader, nodeStorageCache);

        Assert.That(bundle.TryLoadStateRlp(path, hash, ReadFlags.HintReadAhead), Is.EqualTo(rlp));
        Assert.That(bundle.TryLoadStateRlp(path, hash, ReadFlags.HintReadAhead), Is.EqualTo(rlp));

        reader.Received(1).TryLoadStateRlp(path, ReadFlags.HintReadAhead);
    }

    [Test]
    public void TryLoadStateRlp_BypassesNodeStorageCache_ForCacheMissHints()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        TreePath path = TreePath.FromHexString("12");
        Hash256 hash = Keccak.Zero;
        byte[] rlp = [0xc1, 0xff];
        reader.TryLoadStateRlp(path, ReadFlags.HintCacheMiss).Returns(rlp);

        NodeStorageCache nodeStorageCache = new() { Enabled = true };
        using SnapshotBundle bundle = CreateBundle(pool, reader, nodeStorageCache);

        Assert.That(bundle.TryLoadStateRlp(path, hash, ReadFlags.HintCacheMiss), Is.EqualTo(rlp));
        Assert.That(bundle.TryLoadStateRlp(path, hash, ReadFlags.HintCacheMiss), Is.EqualTo(rlp));

        reader.Received(2).TryLoadStateRlp(path, ReadFlags.HintCacheMiss);
    }

    private static SnapshotBundle CreateBundle(ResourcePool pool, IPersistence.IPersistenceReader reader, NodeStorageCache nodeStorageCache)
    {
        ReadOnlySnapshotBundle readOnlySnapshotBundle = new(
            new SnapshotPooledList(0),
            reader,
            recordDetailedMetrics: false);

        return new SnapshotBundle(
            readOnlySnapshotBundle,
            Substitute.For<ITrieNodeCache>(),
            pool,
            ResourcePool.Usage.MainBlockProcessing,
            nodeStorageCache: nodeStorageCache);
    }
}
