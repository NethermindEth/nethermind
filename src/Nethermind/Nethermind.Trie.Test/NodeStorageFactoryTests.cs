// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

public class NodeStorageFactoryTests
{
    [TestCase(INodeStorage.KeyScheme.Hash)]
    [TestCase(INodeStorage.KeyScheme.HalfPath)]
    public void Should_DetectHashBasedLayout(INodeStorage.KeyScheme preferredKeyScheme)
    {
        IDb memDb = new MemDb();
        for (int i = 0; i < 20; i++)
        {
            Hash256 hash = Keccak.Compute(i.ToBigEndianByteArray());
            memDb[hash.Bytes] = hash.Bytes.ToArray();
        }

        NodeStorageFactory nodeStorageFactory = new NodeStorageFactory(preferredKeyScheme);
        nodeStorageFactory.WrapKeyValueStore(memDb).Scheme.Should().Be(INodeStorage.KeyScheme.Hash);
    }

    [TestCase(INodeStorage.KeyScheme.Hash)]
    [TestCase(INodeStorage.KeyScheme.HalfPath)]
    public void Should_DetectHalfPathBasedLayout(INodeStorage.KeyScheme preferredKeyScheme)
    {
        IDb memDb = new MemDb();
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

        NodeStorageFactory nodeStorageFactory = new NodeStorageFactory(preferredKeyScheme);
        nodeStorageFactory.WrapKeyValueStore(memDb).Scheme.Should().Be(INodeStorage.KeyScheme.HalfPath);
    }

    [TestCase(INodeStorage.KeyScheme.Hash)]
    [TestCase(INodeStorage.KeyScheme.HalfPath)]
    public void When_NotEnoughKey_Then_UsePreferredKeyScheme(INodeStorage.KeyScheme preferredKeyScheme)
    {
        IDb memDb = new MemDb();
        for (int i = 0; i < 5; i++)
        {
            Hash256 hash = Keccak.Compute(i.ToBigEndianByteArray());
            memDb[hash.Bytes] = hash.Bytes.ToArray();
        }

        NodeStorageFactory nodeStorageFactory = new NodeStorageFactory(preferredKeyScheme);
        nodeStorageFactory.WrapKeyValueStore(memDb).Scheme.Should().Be(preferredKeyScheme);
    }

    [TestCase(INodeStorage.KeyScheme.Hash)]
    [TestCase(INodeStorage.KeyScheme.HalfPath)]
    public void When_KVStoreIsNotDb_Then_UsePreferredKeyScheme(INodeStorage.KeyScheme preferredKeyScheme)
    {
        IKeyValueStore memDb = new JustKvStore(new MemDb());
        for (int i = 0; i < 5; i++)
        {
            Hash256 hash = Keccak.Compute(i.ToBigEndianByteArray());
            memDb[hash.Bytes] = hash.Bytes.ToArray();
        }

        NodeStorageFactory nodeStorageFactory = new NodeStorageFactory(preferredKeyScheme);
        nodeStorageFactory.WrapKeyValueStore(memDb).Scheme.Should().Be(preferredKeyScheme);
    }

    private class JustKvStore : IKeyValueStore
    {
        private IKeyValueStore _keyValueStoreImplementation;

        public JustKvStore(IKeyValueStore keyValueStoreImplementation)
        {
            _keyValueStoreImplementation = keyValueStoreImplementation;
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return _keyValueStoreImplementation.Get(key, flags);
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            _keyValueStoreImplementation.Set(key, value, flags);
        }
    }
}
