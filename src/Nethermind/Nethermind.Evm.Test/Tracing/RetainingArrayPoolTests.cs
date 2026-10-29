// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Blockchain.Tracing.ParityStyle;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing;

public class RetainingArrayPoolTests
{
    [TestCase(17)]
    [TestCase(33)]
    [TestCase(64)]
    [TestCase(65)]
    public void Rent_returns_an_array_at_least_as_long_as_requested(int length)
    {
        RetainingArrayPool<byte> pool = new();
        for (int size = 16; size <= 256; size *= 2) pool.Return(ArrayPool<byte>.Shared.Rent(size));

        byte[] rented = pool.Rent(length);

        Assert.That(rented.Length, Is.GreaterThanOrEqualTo(length));
        pool.Return(rented);
        pool.Clear();
    }

    [Test]
    public void Rent_keeps_a_much_larger_array_for_a_request_of_its_own_size()
    {
        const int LargeLength = 1024 * 1024;
        RetainingArrayPool<byte> pool = new();
        byte[] large = ArrayPool<byte>.Shared.Rent(LargeLength);
        pool.Return(large);

        byte[] small = pool.Rent(68);
        byte[] rentedLarge = pool.Rent(LargeLength);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(small, Is.Not.SameAs(large));
            Assert.That(small.Length, Is.LessThanOrEqualTo(4 * 68));
            Assert.That(rentedLarge, Is.SameAs(large));
        }

        pool.Return(small);
        pool.Return(rentedLarge);
        pool.Clear();
    }

    [TestCase(1, 16)]
    [TestCase(16, 32)]
    [TestCase(65, 256)]
    public void Rent_reuses_an_array_from_the_fitting_bucket_or_the_next(int length, int returnedLength)
    {
        RetainingArrayPool<byte> pool = new();
        byte[] returned = ArrayPool<byte>.Shared.Rent(returnedLength);
        pool.Return(returned);

        Assert.That(pool.Rent(length), Is.SameAs(returned));
        pool.Return(returned);
        pool.Clear();
    }
}
