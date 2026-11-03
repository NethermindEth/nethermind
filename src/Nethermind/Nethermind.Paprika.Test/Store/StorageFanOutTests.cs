// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Crypto;
using Paprika.Data;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class StorageFanOutTests
{
    private const uint Meg = 1024u * 1024u;

    [TestCase(0u)]
    [TestCase(1u)]
    [TestCase(1023u)]
    [TestCase(1024u)]
    [TestCase(1025u)]
    [TestCase(Meg - 1)]
    [TestCase(Meg)]
    [TestCase(Meg + 1)]
    [TestCase(Meg * 2 - 1)]
    [TestCase(Meg * 2)]
    [TestCase(Meg * 2 + 1)]
    [TestCase(Meg * 4 - 1)]
    [TestCase(Meg * 4)]
    [TestCase(Meg * 4 + 1)]
    [TestCase(Meg * 8 - 1)]
    [TestCase(Meg * 8)]
    [TestCase(Meg * 8 + 1)]
    [TestCase(Meg * 16 - 1)]
    [TestCase(Meg * 16)]
    [TestCase(Meg * 16 + 1)]
    [TestCase(Meg * 32 - 1)]
    [TestCase(Meg * 32)]
    [TestCase(Meg * 32 + 1)]
    [TestCase(Meg * 64 - 1)]
    [TestCase(Meg * 64)]
    [TestCase(Meg * 64 + 1)]
    public void AssertBoundaries(uint at)
    {
        (uint next0, int index0) = StorageFanOut.GetIndex(at, 0);
        (uint next1, int index1) = StorageFanOut.GetIndex(next0, 1);
        (byte bucket2, int index2) = StorageFanOut.Level2Page.GetIndex(next1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(index0, Is.LessThan(1024), $"L0 failed at: {at}");
            Assert.That(index1, Is.LessThan(1024), $"L1 failed at: {at}");
            Assert.That(index2, Is.LessThan(256), $"L2 failed at: {at}");
            Assert.That(bucket2, Is.LessThan(16), $"L2 failed at: {at}");
        }
    }

    [Test]
    public void TryGetId_treats_an_empty_id_as_absent()
    {
        BasePageTests.TestBatchContext batch = BasePageTests.NewBatch(1);
        DbAddressList.Of1024 addresses = default;
        StorageFanOut.Level0 level0 = new(ref addresses);
        Keccak account = Values.Key2;

        level0.SetId(account, new ContractId(1), batch);

        // Overwrite the id with a tombstone. The Ids fan-out consumes the first four nibbles of the account.
        Page ids = Enumerable.Range(1, (int)batch.PageCount)
            .Select(i => batch.GetAt(DbAddress.Page((uint)i)))
            .Single(page => page.Header.PageType == PageType.Bottom);
        new BottomPage(ids).Map.Set(NibblePath.FromKey(account).SliceFrom(4), ReadOnlySpan<byte>.Empty);

        Assert.That(level0.TryGetId(account, out _, batch), Is.False);
    }
}
