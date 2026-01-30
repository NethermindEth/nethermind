// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

public class RefCounterTrieNodeRlpTests
{
    [Test]
    public void CreateFromRlp_ShouldCopyDataAndSetLength()
    {
        byte[] rlpData = Bytes.FromHexString("0xf8518080808080a0e4761d6d3ca2a3e6bcae7ee7f851e6da8c8a6e9d2a2c50927c8cee5a3a8a92c280808080808080a0b50e9e0a5a22c6a1f45e8e3c4b47adbc6e3e1c5c4a8e9d2b5a7c1e3f4a6b8c9d0180");

        using RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(rlpData);

        refRlp.Length.Should().Be(rlpData.Length);
        refRlp.Span.ToArray().Should().BeEquivalentTo(rlpData);
    }

    [Test]
    public void ToArray_ShouldReturnCopyOfData()
    {
        byte[] rlpData = Bytes.FromHexString("0xc68365746883676f6f64");

        using RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(rlpData);
        byte[] copy = refRlp.ToArray();

        copy.Should().BeEquivalentTo(rlpData);
        copy.Should().NotBeSameAs(rlpData);
    }

    [Test]
    public void MemorySize_ShouldIncludeOverheadAndPooledArrayLength()
    {
        byte[] rlpData = new byte[100];

        using RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(rlpData);

        // MemorySize should be at least SmallObjectOverhead + ArrayOverhead + actual pooled array size
        // ArrayPool may rent a larger array, so we check minimum
        refRlp.MemorySize.Should().BeGreaterThanOrEqualTo(
            MemorySizes.SmallObjectOverhead + MemorySizes.ArrayOverhead + rlpData.Length);
    }

    [Test]
    public void Dispose_ShouldAllowOnlyOneDispose()
    {
        RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(Bytes.FromHexString("0x80"));

        refRlp.Dispose();

        // Second dispose should throw
        Action secondDispose = () => refRlp.Dispose();
        secondDispose.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void AcquireLease_ShouldAllowMultipleReferences()
    {
        RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(Bytes.FromHexString("0x8465746821"));

        refRlp.AcquireLease(); // Now ref count is 2
        refRlp.AcquireLease(); // Now ref count is 3

        // Dispose three times (initial + 2 acquired)
        refRlp.Dispose();
        refRlp.Dispose();
        refRlp.Dispose();

        // Fourth dispose should throw
        Action fourthDispose = () => refRlp.Dispose();
        fourthDispose.Should().Throw<ObjectDisposedException>();
    }

    [Test]
    public void AcquireLease_ShouldThrowAfterDisposed()
    {
        RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(Bytes.FromHexString("0x80"));
        refRlp.Dispose();

        Action acquireAfterDispose = () => refRlp.AcquireLease();
        acquireAfterDispose.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public async Task ConcurrentLeaseOperations_ShouldBeThreadSafe()
    {
        byte[] rlpData = Bytes.FromHexString("0xf8518080808080a0e4761d6d3ca2a3e6bcae7ee7f851e6da8c8a6e9d2a2c50927c8cee5a3a8a92c280808080808080a0b50e9e0a5a22c6a1f45e8e3c4b47adbc6e3e1c5c4a8e9d2b5a7c1e3f4a6b8c9d0180");
        RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(rlpData);

        const int leaseCount = 100;

        // Acquire multiple leases concurrently
        Task[] acquireTasks = new Task[leaseCount];
        for (int i = 0; i < leaseCount; i++)
        {
            acquireTasks[i] = Task.Run(() => refRlp.AcquireLease());
        }
        await Task.WhenAll(acquireTasks);

        // Release all leases concurrently (including the initial one)
        Task[] releaseTasks = new Task[leaseCount + 1];
        for (int i = 0; i < leaseCount + 1; i++)
        {
            releaseTasks[i] = Task.Run(() => refRlp.Dispose());
        }
        await Task.WhenAll(releaseTasks);

        // Should be fully disposed now, next acquire should fail
        Action acquireAfterDispose = () => refRlp.AcquireLease();
        acquireAfterDispose.Should().Throw<InvalidOperationException>();
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(16)]
    [TestCase(1024)]
    [TestCase(4096)]
    public void CreateFromRlp_ShouldHandleVariousSizes(int size)
    {
        byte[] rlpData = new byte[size];
        for (int i = 0; i < size; i++)
        {
            rlpData[i] = (byte)(i % 256);
        }

        using RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(rlpData);

        refRlp.Length.Should().Be(size);
        refRlp.Span.ToArray().Should().BeEquivalentTo(rlpData);
    }

    [Test]
    public void Span_ShouldReturnCorrectSliceOfPooledArray()
    {
        // ArrayPool may return a larger array than requested
        // Verify that Span returns only the requested portion
        byte[] rlpData = Bytes.FromHexString("0xc0"); // Smallest valid RLP

        using RefCounterTrieNodeRlp refRlp = RefCounterTrieNodeRlp.CreateFromRlp(rlpData);

        refRlp.Span.Length.Should().Be(1);
        refRlp.Span[0].Should().Be(0xc0);
    }
}
