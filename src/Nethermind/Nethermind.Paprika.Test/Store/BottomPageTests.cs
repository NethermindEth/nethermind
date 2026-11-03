// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class BottomPageTests : BasePageTests
{
    private const uint BatchId = 1;

    [TestCase(true, TestName = "Empty key - first")]
    [TestCase(false, TestName = "Empty key - last")]
    public void Ordering(bool emptyFirst)
    {
        TestBatchContext batch = NewBatch(BatchId);
        BottomPage bottom = ((IBatchContext)batch).GetNewPage<BottomPage>(out _);

        NibblePath key = NibblePath.Empty;

        NibblePath key0 = NibblePath.FromKey([0x0A]);
        NibblePath key1 = NibblePath.FromKey([0x4A]);
        NibblePath key2 = NibblePath.FromKey([0x1A]);
        NibblePath key3 = NibblePath.FromKey([0x8A]);
        NibblePath key4 = NibblePath.FromKey([0xFA]);
        NibblePath key5 = NibblePath.FromKey([0xCA]);
        NibblePath key6 = NibblePath.FromKey([0x33]);
        NibblePath key7 = NibblePath.FromKey([0x34]);

        byte[] v = new byte[1799];
        byte[] v0 = new byte[1800];
        byte[] v1 = new byte[1801];
        byte[] v2 = new byte[1802];
        byte[] v3 = new byte[1803];
        byte[] v4 = new byte[1804];
        byte[] v5 = new byte[1805];
        byte[] v6 = new byte[1806];
        byte[] v7 = new byte[1807];

        if (emptyFirst)
        {
            Set(key, v);
        }

        Set(key0, v0);
        Set(key1, v1);
        Set(key2, v2);
        Set(key3, v3);
        Set(key4, v4);
        Set(key5, v5);
        Set(key6, v6);
        Set(key7, v7);

        if (!emptyFirst)
        {
            Set(key, v);
        }

        AssertValue(key, v);
        AssertValue(key0, v0);
        AssertValue(key1, v1);
        AssertValue(key2, v2);
        AssertValue(key3, v3);
        AssertValue(key4, v4);
        AssertValue(key5, v5);
        AssertValue(key6, v6);
        AssertValue(key7, v7);

        return;

        void Set(in NibblePath path, byte[] value) => bottom.Set(path, value, batch);

        void AssertValue(in NibblePath path, in ReadOnlySpan<byte> expected)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(bottom.TryGet(batch, path, out ReadOnlySpan<byte> actual), Is.True);
                Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
            }
        }
    }
}
