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
    [TestCase(1, TypeArgs = [typeof(PbtPath)])]
    [TestCase(2, TypeArgs = [typeof(PbtPath)])]
    [TestCase(256, TypeArgs = [typeof(PbtPath)])]
    [TestCase(1, TypeArgs = [typeof(PbtStoragePath)])]
    [TestCase(2, TypeArgs = [typeof(PbtStoragePath)])]
    [TestCase(256, TypeArgs = [typeof(PbtStoragePath)])]
    public void Leaf_hash_matches_the_reference_tree_for_present_and_absent_keys<TKey>(int leafCount)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        Random random = new(leafCount);
        TrackingMemoryProvider memory = new();
        PbtNodeGroupStore store = new(memory);
        EipReferenceTree oracle = new();
        List<TKey> keys = [];
        List<(byte[] Key, byte[]? Value)> changes = [];
        for (int index = 0; index < leafCount; index++)
        {
            TKey key = RandomKey<TKey>(random);
            byte[] value = new byte[32];
            random.NextBytes(value);
            keys.Add(key);
            changes.Add((key.Bytes.ToArray(), value));
            oracle.Insert(key.Bytes, value);
        }
        ValueHash256 root = store.Fold(default, changes);
        Assert.That(root, Is.EqualTo(new ValueHash256(oracle.Merkelize())));

        using (Assert.EnterMultipleScope())
        {
            foreach (TKey key in keys)
            {
                ValueHash256 expected = new(oracle.Merkelize(PbtStorageNodePath.Create(key.Bytes, key.BitLength)));
                Assert.That(PbtNodeTraverser.GetLeafHash(store, root, key, 0, null, out _), Is.EqualTo(expected), $"present key {Convert.ToHexString(key.Bytes)}");
            }
            for (int index = 0; index < 64; index++)
            {
                TKey absent = RandomKey<TKey>(random);
                Assert.That(PbtNodeTraverser.GetLeafHash(store, root, absent, 0, null, out _), Is.EqualTo(default(ValueHash256)), $"absent key {Convert.ToHexString(absent.Bytes)}");
            }
        }
        store.Dispose();
        Assert.That(TrackingMemoryProvider.CountUnreleased(memory.Rented), Is.Zero);
    }

    private static TKey RandomKey<TKey>(Random random) where TKey : unmanaged, IPbtKey<TKey>
    {
        byte[] bytes = new byte[TKey.Capacity];
        random.NextBytes(bytes);
        bytes[0] = TKey.Capacity == PbtStoragePath.KeyLength ? Eip8297KeyDerivation.StorageZone : Eip8297KeyDerivation.AccountZone;
        return TKey.Create(bytes);
    }
}
