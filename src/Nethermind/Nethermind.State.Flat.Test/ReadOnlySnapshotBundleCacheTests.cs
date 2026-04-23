// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ReadOnlySnapshotBundleCacheTests
{
    [Test]
    public void TryLoadStateRlp_ReusesSharedHashCacheAcrossBundles()
    {
        SeqlockCache<Hash256AsKey, byte[], HugeCacheSets> sharedCache = new();
        Hash256 hash = CreateHash(1);
        byte[] expected = [1, 2, 3];
        TreePath firstPath = TreePath.Empty.Append(1).Append(2);
        TreePath secondPath = TreePath.Empty.Append(3).Append(4);

        IPersistence.IPersistenceReader firstReader = Substitute.For<IPersistence.IPersistenceReader>();
        firstReader.TryLoadStateRlp(Arg.Any<TreePath>(), ReadFlags.None).Returns(expected);

        using (ReadOnlySnapshotBundle firstBundle = new(new SnapshotPooledList(0), firstReader, false, sharedCache))
        {
            Assert.That(firstBundle.TryLoadStateRlp(firstPath, hash, ReadFlags.None), Is.EqualTo(expected));
        }

        IPersistence.IPersistenceReader secondReader = Substitute.For<IPersistence.IPersistenceReader>();
        using (ReadOnlySnapshotBundle secondBundle = new(new SnapshotPooledList(0), secondReader, false, sharedCache))
        {
            Assert.That(secondBundle.TryLoadStateRlp(secondPath, hash, ReadFlags.None), Is.EqualTo(expected));
        }

        firstReader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), ReadFlags.None);
        secondReader.DidNotReceive().TryLoadStateRlp(Arg.Any<TreePath>(), ReadFlags.None);
    }

    [Test]
    public void TryLoadStorageRlp_ReusesSharedHashCacheAcrossBundles()
    {
        SeqlockCache<Hash256AsKey, byte[], HugeCacheSets> sharedCache = new();
        Hash256 hash = CreateHash(2);
        Hash256 firstAddress = CreateHash(3);
        Hash256 secondAddress = CreateHash(4);
        byte[] expected = [4, 5, 6];
        TreePath firstPath = TreePath.Empty.Append(5).Append(6);
        TreePath secondPath = TreePath.Empty.Append(7).Append(8);

        IPersistence.IPersistenceReader firstReader = Substitute.For<IPersistence.IPersistenceReader>();
        firstReader.TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), ReadFlags.None).Returns(expected);

        using (ReadOnlySnapshotBundle firstBundle = new(new SnapshotPooledList(0), firstReader, false, sharedCache))
        {
            Assert.That(firstBundle.TryLoadStorageRlp(firstAddress, firstPath, hash, ReadFlags.None), Is.EqualTo(expected));
        }

        IPersistence.IPersistenceReader secondReader = Substitute.For<IPersistence.IPersistenceReader>();
        using (ReadOnlySnapshotBundle secondBundle = new(new SnapshotPooledList(0), secondReader, false, sharedCache))
        {
            Assert.That(secondBundle.TryLoadStorageRlp(secondAddress, secondPath, hash, ReadFlags.None), Is.EqualTo(expected));
        }

        firstReader.Received(1).TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), ReadFlags.None);
        secondReader.DidNotReceive().TryLoadStorageRlp(Arg.Any<Hash256>(), Arg.Any<TreePath>(), ReadFlags.None);
    }

    [Test]
    public void TryLoadStateRlp_WithNonDefaultFlags_BypassesSharedHashCache()
    {
        SeqlockCache<Hash256AsKey, byte[], HugeCacheSets> sharedCache = new();
        Hash256 hash = CreateHash(5);
        TreePath path = TreePath.Empty.Append(9);
        byte[] cached = [7];
        byte[] uncached = [8];

        IPersistence.IPersistenceReader firstReader = Substitute.For<IPersistence.IPersistenceReader>();
        firstReader.TryLoadStateRlp(Arg.Any<TreePath>(), ReadFlags.None).Returns(cached);
        using (ReadOnlySnapshotBundle firstBundle = new(new SnapshotPooledList(0), firstReader, false, sharedCache))
        {
            Assert.That(firstBundle.TryLoadStateRlp(path, hash, ReadFlags.None), Is.EqualTo(cached));
        }

        IPersistence.IPersistenceReader secondReader = Substitute.For<IPersistence.IPersistenceReader>();
        secondReader.TryLoadStateRlp(Arg.Any<TreePath>(), ReadFlags.HintCacheMiss).Returns(uncached);
        using (ReadOnlySnapshotBundle secondBundle = new(new SnapshotPooledList(0), secondReader, false, sharedCache))
        {
            Assert.That(secondBundle.TryLoadStateRlp(path, hash, ReadFlags.HintCacheMiss), Is.EqualTo(uncached));
        }

        secondReader.Received(1).TryLoadStateRlp(Arg.Any<TreePath>(), ReadFlags.HintCacheMiss);
    }

    private static Hash256 CreateHash(byte marker)
    {
        byte[] bytes = new byte[Hash256.Size];
        bytes[0] = marker;
        return new Hash256(bytes);
    }
}
