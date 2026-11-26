// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Collections;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class PooledSegmentedBufferTests
{
    [Test]
    public void Add_keeps_each_item_contiguous_and_intact([Values(1, 16, 64)] int segmentSize)
    {
        byte[][] items = [.. Enumerable.Range(0, 40).Select(static i => Enumerable.Range(i, i % 23).Select(static b => (byte)b).ToArray())];
        using PooledSegmentedBuffer buffer = new(segmentSize);

        ReadOnlyMemory<byte>[] copies = [.. items.Select(item => buffer.Add(item))];

        Assert.That(copies.Select(static copy => copy.ToArray()), Is.EqualTo(items));
    }
}
