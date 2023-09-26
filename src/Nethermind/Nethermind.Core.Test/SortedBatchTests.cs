// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test;

public class SortedBatchTests
{
    [Test]
    public void Test_BatchWillSort()
    {
        TestBatch baseBatch = new TestBatch();

        IBatch sortedBatch = baseBatch.ToSortedBatch();

        IList<byte[]> expectedOrder = new List<byte[]>();
        for (int i = 0; i < 10; i++)
        {
            sortedBatch[TestItem.ValueKeccaks[i].ToByteArray()] = TestItem.ValueKeccaks[i].ToByteArray();
            expectedOrder.Add(TestItem.ValueKeccaks[i].ToByteArray());
        }

        baseBatch.DisposeCalled.Should().BeFalse();
        baseBatch.SettedValues.Count.Should().Be(0);

        sortedBatch.Dispose();

        baseBatch.DisposeCalled.Should().BeTrue();
        expectedOrder = expectedOrder.Order(Bytes.Comparer).ToList();
        baseBatch.SettedValues.Should().BeEquivalentTo(expectedOrder);
    }

    private class TestBatch : IBatch
    {
        public bool DisposeCalled { get; set; }
        public List<byte[]> SettedValues { get; set; } = new();

        public void Dispose()
        {
            DisposeCalled = true;
        }

        public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            return null;
        }

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            SettedValues.Add(key.ToArray());
        }
    }
}
