// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class NodeStorageFactoryTests
{
    [Test]
    public void Should_DetectHashBasedLayout([Values(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath)] INodeStorage.KeyScheme preferredKeyScheme)
    {
        IDb memDb = PrepareMemDbWithKeyScheme(INodeStorage.KeyScheme.Hash);

        NodeStorageFactory nodeStorageFactory = new(preferredKeyScheme, LimboLogs.Instance);
        nodeStorageFactory.DetectCurrentKeySchemeFrom(memDb);
        Assert.That(nodeStorageFactory.WrapKeyValueStore(memDb).Scheme, Is.EqualTo(INodeStorage.KeyScheme.Hash));
    }

    [Test]
    public void Should_DetectHalfPathBasedLayout([Values(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath)] INodeStorage.KeyScheme preferredKeyScheme)
    {
        IDb memDb = PrepareMemDbWithKeyScheme(INodeStorage.KeyScheme.HalfPath);

        NodeStorageFactory nodeStorageFactory = new(preferredKeyScheme, LimboLogs.Instance);
        nodeStorageFactory.DetectCurrentKeySchemeFrom(memDb);
        Assert.That(nodeStorageFactory.WrapKeyValueStore(memDb).Scheme, Is.EqualTo(INodeStorage.KeyScheme.HalfPath));
    }

    [Test]
    public void When_NotEnoughKey_Then_UsePreferredKeyScheme([Values(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath)] INodeStorage.KeyScheme preferredKeyScheme)
    {
        IDb memDb = new MemDb();
        for (int i = 0; i < 5; i++)
        {
            Hash256 hash = Keccak.Compute(i.ToBigEndianByteArray());
            memDb[hash.Bytes] = hash.Bytes.ToArray();
        }

        NodeStorageFactory nodeStorageFactory = new(preferredKeyScheme, LimboLogs.Instance);
        nodeStorageFactory.DetectCurrentKeySchemeFrom(memDb);
        Assert.That(nodeStorageFactory.WrapKeyValueStore(memDb).Scheme, Is.EqualTo(preferredKeyScheme));
    }

    [TestCase(INodeStorage.KeyScheme.HalfPath, INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.Hash, true)]
    [TestCase(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath, INodeStorage.KeyScheme.HalfPath, true)]
    [TestCase(INodeStorage.KeyScheme.HalfPath, INodeStorage.KeyScheme.Current, INodeStorage.KeyScheme.HalfPath, true)]
    [TestCase(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.Current, INodeStorage.KeyScheme.Hash, false)]
    [TestCase(null, INodeStorage.KeyScheme.Current, INodeStorage.KeyScheme.HalfPath, true)]
    public void When_ForceUsePreferredIsTrue_Then_UsePreferred(INodeStorage.KeyScheme? currentKeyScheme, INodeStorage.KeyScheme preferredKeyScheme, INodeStorage.KeyScheme expectedScheme, bool requirePath)
    {
        IDb memDb = PrepareMemDbWithKeyScheme(currentKeyScheme);

        NodeStorageFactory nodeStorageFactory = new(preferredKeyScheme, LimboLogs.Instance);
        nodeStorageFactory.DetectCurrentKeySchemeFrom(memDb);
        INodeStorage nodeStorage = nodeStorageFactory.WrapKeyValueStore(memDb, forceUsePreferredKeyScheme: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeStorage.Scheme, Is.EqualTo(expectedScheme));
            Assert.That(nodeStorage.RequirePath, Is.EqualTo(requirePath));
        }
    }

    public const string EoaNodeHex = "0xf8448001a056e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421a0c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470";

    [TestCase(EoaNodeHex, "0x00f8448001")]
    [TestCase("0xc20102", "0xc20102")]
    public void WrapKeyValueStore_CompressesEoaSuffix(string nodeHex, string storedHex)
    {
        MemDb memDb = new();
        INodeStorage nodeStorage = new NodeStorageFactory(INodeStorage.KeyScheme.Hash, LimboLogs.Instance).WrapKeyValueStore(memDb);
        byte[] node = Bytes.FromHexString(nodeHex);

        nodeStorage.Set(null, TreePath.Empty, TestItem.KeccakA, node);
        using (INodeStorage.IWriteBatch writeBatch = nodeStorage.StartWriteBatch())
        {
            writeBatch.Set(null, TreePath.Empty, TestItem.KeccakB, node, WriteFlags.None);
        }
        memDb[TestItem.KeccakC.Bytes] = node;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(memDb[TestItem.KeccakA.Bytes], Is.EqualTo(Bytes.FromHexString(storedHex)));
            Assert.That(memDb[TestItem.KeccakB.Bytes], Is.EqualTo(Bytes.FromHexString(storedHex)));
            Assert.That(nodeStorage.Get(null, TreePath.Empty, TestItem.KeccakA), Is.EqualTo(node));
            Assert.That(nodeStorage.Get(null, TreePath.Empty, TestItem.KeccakB), Is.EqualTo(node));
            Assert.That(nodeStorage.Get(null, TreePath.Empty, TestItem.KeccakC), Is.EqualTo(node));
        }
    }

    private MemDb PrepareMemDbWithKeyScheme(INodeStorage.KeyScheme? scheme)
    {
        MemDb memDb = new();
        if (scheme == INodeStorage.KeyScheme.Hash)
        {
            for (int i = 0; i < 20; i++)
            {
                Hash256 hash = Keccak.Compute(i.ToBigEndianByteArray());
                memDb[hash.Bytes] = hash.Bytes.ToArray();
            }
        }
        else if (scheme == INodeStorage.KeyScheme.HalfPath)
        {
            for (int i = 0; i < 10; i++)
            {
                Hash256 hash = Keccak.Compute(i.ToBigEndianByteArray());
                memDb[NodeStorage.GetHalfPathNodeStoragePath(null, TreePath.Empty, hash)] = hash.Bytes.ToArray();
            }
            for (int i = 0; i < 10; i++)
            {
                Hash256 hash = Keccak.Compute(i.ToBigEndianByteArray());
                memDb[NodeStorage.GetHalfPathNodeStoragePath(hash, TreePath.Empty, hash)] = hash.Bytes.ToArray();
            }
        }

        return memDb;
    }
}
