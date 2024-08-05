// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery.Kademlia;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Kademlia;

public class KBucketTests
{
    [Test]
    public void TestKBucketAdd()
    {
        KBucket<ValueHash256> bucket = new(5);

        ValueHash256[] toAdd = Enumerable.Range(0, 10).Select((k) => ValueKeccak.Compute(k.ToString())).ToArray();

        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(valueHash256, valueHash256, out _);
        }

        // Again
        foreach (ValueHash256 valueHash256 in toAdd)
        {
            bucket.TryAddOrRefresh(valueHash256, valueHash256, out _);
        }

        bucket.GetAll().ToHashSet().Should().BeEquivalentTo(toAdd[..5].ToHashSet());
    }
}
