// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtOperationSortTests
{
    /// <param name="keysPerStem">How many keys share every byte but the last, as the leaves of one account or contract do.</param>
    [Test]
    public void Sorts_like_the_key_comparison(
        [Values(0, 1, 2, 17, 1000)] int count,
        [Values(1, 3, 40)] int keysPerStem)
    {
        Random random = new(count * 31 + keysPerStem);
        AssertSorted(Operations(count, keysPerStem, random, static bytes => new PbtPath(bytes), PbtPath.KeyLength));
        AssertSorted(Operations(count, keysPerStem, random, static bytes => new PbtStoragePath(bytes), PbtStoragePath.KeyLength));
        // Variable-length keys that prefix one another, padded chunks included, sort shorter first.
        AssertSorted(Operations(count, keysPerStem, random, static bytes => new PbtStorageTreeKey(bytes), 0));
    }

    private static PbtWriteOperation<TKey>[] Operations<TKey>(int count, int keysPerStem, Random random, Func<byte[], TKey> create, int keyLength)
        where TKey : unmanaged, IPbtKey<TKey>
    {
        byte[] stem = [];
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                int length = keyLength != 0 ? keyLength : random.Next(1, PbtStorageTreeKey.MaxLength + 1);
                if (index % keysPerStem == 0) stem = RandomBytes(random, PbtStorageTreeKey.MaxLength);
                byte[] key = stem[..length];
                key[^1] = (byte)(index % keysPerStem);
                return create(key);
            })
            .Distinct()
            .Select(static key => new PbtWriteOperation<TKey>(key, default))
            .OrderBy(_ => random.Next())
            .ToArray();
    }

    private static void AssertSorted<TKey>(PbtWriteOperation<TKey>[] operations) where TKey : unmanaged, IPbtKey<TKey>
    {
        PbtWriteOperation<TKey>[] expected = [.. operations.OrderBy(static operation => operation.Key)];
        PbtOperationSort.Sort(operations.AsSpan());
        Assert.That(operations, Is.EqualTo(expected));
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }
}
