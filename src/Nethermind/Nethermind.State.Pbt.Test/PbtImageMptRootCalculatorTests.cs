// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Test;

public class PbtImageMptRootCalculatorTests
{
    [Test]
    public void MatchesPatriciaTree(
        [Values(0, 1, 2, 16, 257, 4096)] int count,
        [Values(0, 1, 30)] int sharedPrefixBytes,
        [Values(1, 27, 28, 32, 128)] int valueLength)
    {
        List<KeyValuePair<ValueHash256, byte[]>> entries = new(count);
        Random random = new(12345);
        for (int index = 0; index < count; index++)
        {
            byte[] key = new byte[32];
            random.NextBytes(key);
            key.AsSpan(0, sharedPrefixBytes).Clear();
            // Reserve two bytes for uniqueness while exercising long common paths.
            key[30] = (byte)(index >> 8);
            key[31] = (byte)index;
            byte[] value = new byte[valueLength];
            random.NextBytes(value);
            entries.Add(new(new ValueHash256(key), Rlp.Encode(value).Bytes));
        }
        entries.Sort(static (left, right) =>
        {
            ValueHash256 leftKey = left.Key;
            ValueHash256 rightKey = right.Key;
            return leftKey.Bytes.SequenceCompareTo(rightKey.Bytes);
        });

        PatriciaTree tree = new(NullTrieStore.Instance, LimboLogs.Instance);
        foreach (KeyValuePair<ValueHash256, byte[]> entry in entries)
            tree.Set(entry.Key.Bytes, entry.Value);
        tree.UpdateRootHash();

        ValueHash256 actual = PbtImageMptRootCalculator.Calculate(entries, CancellationToken.None);

        Assert.That(actual, Is.EqualTo(tree.RootHash.ValueHash256));
    }

    [Test]
    public void RejectsNonIncreasingKeys([Values] bool duplicate)
    {
        ValueHash256 first = new(Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000002"));
        ValueHash256 second = duplicate ? first : new(Bytes.FromHexString("0000000000000000000000000000000000000000000000000000000000000001"));
        KeyValuePair<ValueHash256, byte[]>[] entries = [new(first, Bytes.FromHexString("01")), new(second, Bytes.FromHexString("02"))];

        Assert.Throws<System.IO.InvalidDataException>(() => PbtImageMptRootCalculator.Calculate(entries, CancellationToken.None));
    }

    [Test]
    public void ObservesCancellationAndDisposesInput([Values] bool cancelBeforeFirst)
    {
        using CancellationTokenSource cancellation = new();
        bool disposed = false;
        if (cancelBeforeFirst)
            cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => PbtImageMptRootCalculator.Calculate(Entries(), cancellation.Token));
        Assert.That(disposed, Is.EqualTo(!cancelBeforeFirst));

        IEnumerable<KeyValuePair<ValueHash256, byte[]>> Entries()
        {
            try
            {
                cancellation.Cancel();
                yield return new(ValueKeccak.Compute(Bytes.FromHexString("01")), Bytes.FromHexString("01"));
            }
            finally
            {
                disposed = true;
            }
        }
    }
}
