// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class ReadOnlySnapshotBundleTests
{
    [Test]
    public void TryLoadStateRlp_ReusesCrossBlockNodeCacheAcrossBundles()
    {
        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        SeqlockCache<NodeKey, byte[], HugeCacheSets> crossBlockCache = new();
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash = Keccak.Compute([1, 2, 3]);
        byte[] rlp = [0xaa, 0xbb];

        persistenceReader.TryLoadStateRlp(path, ReadFlags.None).Returns(rlp);

        using (ReadOnlySnapshotBundle first = new(new SnapshotPooledList(0), persistenceReader, false, crossBlockCache))
        {
            Assert.That(first.TryLoadStateRlp(path, hash, ReadFlags.None), Is.EqualTo(rlp));
        }

        using (ReadOnlySnapshotBundle second = new(new SnapshotPooledList(0), persistenceReader, false, crossBlockCache))
        {
            Assert.That(second.TryLoadStateRlp(path, hash, ReadFlags.None), Is.EqualTo(rlp));
        }

        persistenceReader.Received(1).TryLoadStateRlp(path, ReadFlags.None);
    }

    [Test]
    public void TryLoadStateRlp_DifferentHashBypassesCrossBlockNodeCache()
    {
        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        SeqlockCache<NodeKey, byte[], HugeCacheSets> crossBlockCache = new();
        TreePath path = TreePath.FromHexString("1234");
        Hash256 hash1 = Keccak.Compute([1, 2, 3]);
        Hash256 hash2 = Keccak.Compute([4, 5, 6]);
        byte[] rlp = [0xaa, 0xbb];

        persistenceReader.TryLoadStateRlp(path, ReadFlags.None).Returns(rlp);

        using (ReadOnlySnapshotBundle first = new(new SnapshotPooledList(0), persistenceReader, false, crossBlockCache))
        {
            first.TryLoadStateRlp(path, hash1, ReadFlags.None);
        }

        using (ReadOnlySnapshotBundle second = new(new SnapshotPooledList(0), persistenceReader, false, crossBlockCache))
        {
            second.TryLoadStateRlp(path, hash2, ReadFlags.None);
        }

        persistenceReader.Received(2).TryLoadStateRlp(path, ReadFlags.None);
    }

    [Test]
    public void TryLoadStorageRlp_ReusesCrossBlockNodeCacheAcrossBundles()
    {
        IPersistence.IPersistenceReader persistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        SeqlockCache<NodeKey, byte[], HugeCacheSets> crossBlockCache = new();
        Hash256 address = Keccak.Compute([0x10, 0x20]);
        TreePath path = TreePath.FromHexString("abcd");
        Hash256 hash = Keccak.Compute([7, 8, 9]);
        byte[] rlp = [0xcc, 0xdd];

        persistenceReader.TryLoadStorageRlp(address, path, ReadFlags.None).Returns(rlp);

        using (ReadOnlySnapshotBundle first = new(new SnapshotPooledList(0), persistenceReader, false, crossBlockCache))
        {
            Assert.That(first.TryLoadStorageRlp(address, path, hash, ReadFlags.None), Is.EqualTo(rlp));
        }

        using (ReadOnlySnapshotBundle second = new(new SnapshotPooledList(0), persistenceReader, false, crossBlockCache))
        {
            Assert.That(second.TryLoadStorageRlp(address, path, hash, ReadFlags.None), Is.EqualTo(rlp));
        }

        persistenceReader.Received(1).TryLoadStorageRlp(address, path, ReadFlags.None);
    }
}
