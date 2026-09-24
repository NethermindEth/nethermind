// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

[TestFixture]
public class PbtNodeTraverserTests
{
    [TestCase(1, TypeArgs = [typeof(PbtTreeKey)])]
    [TestCase(2, TypeArgs = [typeof(PbtTreeKey)])]
    [TestCase(256, TypeArgs = [typeof(PbtTreeKey)])]
    [TestCase(1, TypeArgs = [typeof(PbtStorageTreeKey)])]
    [TestCase(2, TypeArgs = [typeof(PbtStorageTreeKey)])]
    [TestCase(256, TypeArgs = [typeof(PbtStorageTreeKey)])]
    public void Leaf_hash_matches_the_reference_tree_for_present_and_absent_keys<TKey>(int leafCount)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        Random random = new(leafCount);
        TrackingMemoryProvider memory = new();
        PbtNodeGroupStore store = new(memory);
        EipReferenceTree oracle = new();
        List<TKey> keys = [];
        using (PbtWriteBatchBuilder<TKey> batch = new(0))
        {
            for (int index = 0; index < leafCount; index++)
            {
                TKey key = RandomKey<TKey>(random);
                byte[] value = new byte[32];
                random.NextBytes(value);
                keys.Add(key);
                batch.Set(key, new ValueHash256(value));
                oracle.Insert(key.Bytes, value);
            }
            ValueHash256 root = TrieUpdater<TKey, PbtStorageNodePath>.UpdateRoot(store, default, batch.Build());
            Assert.That(root, Is.EqualTo(new ValueHash256(oracle.Merkelize())));

            using (Assert.EnterMultipleScope())
            {
                foreach (TKey key in keys)
                {
                    ValueHash256 expected = new(oracle.Merkelize(PbtStorageNodePath.Create(key.Bytes, key.BitLength)));
                    Assert.That(PbtNodeTraverser.GetLeafHash(store, root, key, 0, out _), Is.EqualTo(expected), $"present key {Convert.ToHexString(key.Bytes)}");
                }
                for (int index = 0; index < 64; index++)
                {
                    TKey absent = RandomKey<TKey>(random);
                    Assert.That(PbtNodeTraverser.GetLeafHash(store, root, absent, 0, out _), Is.EqualTo(default(ValueHash256)), $"absent key {Convert.ToHexString(absent.Bytes)}");
                }
            }
        }
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    private static TKey RandomKey<TKey>(Random random) where TKey : unmanaged, IPbtKey<TKey>
    {
        byte[] bytes = new byte[TKey.Capacity];
        random.NextBytes(bytes);
        return TKey.Create(bytes);
    }
}
