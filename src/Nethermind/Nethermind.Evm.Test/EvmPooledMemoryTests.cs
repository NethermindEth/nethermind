// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.Blockchain;
using Nethermind.Core.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class EvmPooledMemoryTests : EvmMemoryTestsBase
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_memory")]
    private static extern ref byte[]? GetBackingMemory(ref EvmPooledMemory memory);

    private static IEnumerable<TestCaseData> ZeroExtendedCopyCases()
    {
        yield return new TestCaseData(
            new byte[] { 1, 2, 3, 4 }, UInt256.Zero, 4, new byte[] { 1, 2, 3, 4 })
            .SetName("CopyFromZeroExtendedAfterGas_exact_range");
        yield return new TestCaseData(
            new byte[] { 1, 2, 3, 4 }, (UInt256)1, 5, new byte[] { 2, 3, 4, 0, 0 })
            .SetName("CopyFromZeroExtendedAfterGas_partial_range");
        yield return new TestCaseData(
            new byte[] { 1, 2, 3, 4 }, (UInt256)4, 3, new byte[] { 0, 0, 0 })
            .SetName("CopyFromZeroExtendedAfterGas_offset_at_end");
        yield return new TestCaseData(
            new byte[] { 1, 2, 3, 4 }, (UInt256)100, 3, new byte[] { 0, 0, 0 })
            .SetName("CopyFromZeroExtendedAfterGas_offset_beyond_end");
        yield return new TestCaseData(
            new byte[] { 1, 2, 3, 4 }, new UInt256(0, 1, 0, 0), 3, new byte[] { 0, 0, 0 })
            .SetName("CopyFromZeroExtendedAfterGas_256_bit_offset");

        byte[] longSource = Enumerable.Range(1, 35).Select(static value => (byte)value).ToArray();
        yield return new TestCaseData(
            longSource, UInt256.Zero, 40, longSource.Concat(new byte[5]).ToArray())
            .SetName("CopyFromZeroExtendedAfterGas_multiword_range");
    }

    private static IEnumerable<TestCaseData> ExpandedZeroExtendedCopyCases()
    {
        yield return new TestCaseData(0, 0, 8192, 0UL, 8192)
            .SetName("ExpandedCopy_full_overwrite");
        yield return new TestCaseData(64, 5000, 4096, 0UL, 6000)
            .SetName("ExpandedCopy_gap_and_zero_extended_tail");
        yield return new TestCaseData(96, 4080, 64, 16UL, 96)
            .SetName("ExpandedCopy_crosses_zero_chunk_boundary");
        yield return new TestCaseData(160, 64, 32, 100UL, 4096)
            .SetName("ExpandedCopy_overwrites_existing_and_new_memory_with_zeroes");
    }

    private static IEnumerable<TestCaseData> MCopyReferenceCases()
    {
        yield return new TestCaseData(128, 256, 0, 128, false)
            .SetName("MCopy_non_overlapping");
        yield return new TestCaseData(128, 16, 0, 96, false)
            .SetName("MCopy_overlap_right");
        yield return new TestCaseData(128, 0, 16, 96, false)
            .SetName("MCopy_overlap_left");
        yield return new TestCaseData(128, 0, 0, 128, false)
            .SetName("MCopy_same_range");
        yield return new TestCaseData(64, 5000, 4064, 96, false)
            .SetName("MCopy_source_crosses_clean_prefix");
        yield return new TestCaseData(64, 64, 8192, 128, false)
            .SetName("MCopy_source_expansion_dominates");
        yield return new TestCaseData(128, 4096, 32, 160, true)
            .SetName("MCopy_tracing_materializes_source_first");
        yield return new TestCaseData(32 * 1024, 16 * 1024, 0, 32 * 1024, false)
            .SetName("MCopy_overlap_right_preserves_source_during_resize");
        yield return new TestCaseData(32 * 1024, 24 * 1024, 16 * 1024, 32 * 1024, false)
            .SetName("MCopy_partial_source_overlap_preserves_source_during_resize");
        yield return new TestCaseData(32 * 1024, 16 * 1024, 16 * 1024, 32 * 1024, false)
            .SetName("MCopy_same_partial_range_preserves_source_during_resize");
        yield return new TestCaseData(32 * 1024, 16 * 1024, 64 * 1024, 64 * 1024, false)
            .SetName("MCopy_zero_source_preserves_only_destination_prefix_during_resize");
    }

    [TestCase(32UL, 1UL)]
    [TestCase(0UL, 0UL)]
    [TestCase(33UL, 2UL)]
    [TestCase(64UL, 2UL)]
    [TestCase((ulong)int.MaxValue, (ulong)(int.MaxValue / 32 + 1))]
    public void Div32Ceiling(ulong input, ulong expectedResult)
    {
        ulong result = EvmCalculations.Div32Ceiling(input);
        TestContext.Out.WriteLine($"Memory cost (gas): {result}");
        Assert.That(result, Is.EqualTo(expectedResult));
    }

    private const ulong MaxCodeSize = CodeSizeConstants.MaxCodeSizeEip170;

    [TestCase(0UL, 0UL)]
    [TestCase(0UL, 32UL)]
    [TestCase(0UL, 256UL)]
    [TestCase(0UL, 2048UL)]
    [TestCase(0UL, MaxCodeSize)]
    [TestCase(10UL * MaxCodeSize, MaxCodeSize)]
    [TestCase(100UL * MaxCodeSize, MaxCodeSize)]
    [TestCase(1000UL * MaxCodeSize, MaxCodeSize)]
    [TestCase(0UL, (ulong)MemorySizes.MiB)]
    // Note: Int32.MaxValue was removed as a test case because after word alignment
    // it exceeds the maximum allowed memory size and correctly returns out-of-gas.
    public void MemoryCost(ulong destination, ulong memoryAllocation)
    {
        EvmPooledMemory memory = new();
        UInt256 dest = destination;
        ulong result = memory.CalculateMemoryCost(in dest, memoryAllocation, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(false));
        TestContext.Out.WriteLine($"Gas cost of allocating {memoryAllocation} starting from {dest}: {result}");
    }

    [TestCase(1UL, 32UL, 3UL)]
    [TestCase(33UL, 64UL, 6UL)]
    [TestCase(512UL, 512UL, 48UL)]
    [TestCase(1024UL, 1024UL, 98UL)]
    public void CalculateMemoryCost_aligns_size_and_charges_expected_cost(
        ulong requestedSize,
        ulong expectedSize,
        ulong expectedCost)
    {
        EvmPooledMemory memory = new();
        UInt256 location = UInt256.Zero;

        ulong cost = memory.CalculateMemoryCost(in location, requestedSize, out bool outOfGas);
        ulong repeatedCost = memory.CalculateMemoryCost(in location, requestedSize, out bool repeatedOutOfGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(outOfGas, Is.False);
            Assert.That(repeatedOutOfGas, Is.False);
            Assert.That(memory.Size, Is.EqualTo(expectedSize));
            Assert.That(cost, Is.EqualTo(expectedCost));
            Assert.That(repeatedCost, Is.Zero);
        }
    }

    [Test]
    public void CalculateMemoryCost_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0); // value larger than ulong max (u1 != 0)
        ulong result = memory.CalculateMemoryCost(in location, 32, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_LengthExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 length = new(0, 1, 0, 0); // value larger than ulong max (u1 != 0)
        ulong result = memory.CalculateMemoryCost(0, in length, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [TestCase(1024)]
    [TestCase(4096)]
    [TestCase(32 * 1024)]
    [TestCase(70 * 1024)]
    [TestCase(2 * 1024 * 1024)]
    [TestCase(4 * 1024 * 1024)]
    public void Pooled_buffer_is_zeroed_on_reuse(int size)
    {
        EvmPooledMemory dirty = new();
        UInt256 zero = UInt256.Zero;
        Span<byte> pattern = new byte[size];
        pattern.Fill(0xff);
        Assert.That(dirty.TrySave(in zero, pattern), Is.True);
        dirty.Dispose();

        EvmPooledMemory clean = new();
        UInt256 length = (UInt256)size;
        Assert.That(clean.TryLoadSpan(in zero, in length, out Span<byte> data), Is.True);
        Assert.That(data.Length, Is.EqualTo(size));
        Assert.That(data.IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "pooled buffer leaked stale data");
        clean.Dispose();
    }

    [Test]
    public void Fresh_non_pooled_large_allocation_has_zero_gap_and_written_tail()
    {
        const int memorySize = (4 * 1024 * 1024) + 64;
        int destinationOffset = memorySize - EvmPooledMemory.WordSize;
        byte[] word = CreatePattern(EvmPooledMemory.WordSize, 0x17, 29);
        byte[] expected = new byte[memorySize];
        word.CopyTo(expected, destinationOffset);

        EvmPooledMemory memory = default;
        try
        {
            UInt256 destination = (UInt256)destinationOffset;
            memory.CalculateMemoryCost(in destination, EvmPooledMemory.WordSize, out bool outOfGas);
            memory.StoreWordAfterGas(in destination, word);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(outOfGas, Is.False);
                Assert.That(memory.Size, Is.EqualTo((ulong)memorySize));
                Assert.That(GetBackingMemory(ref memory), Has.Length.EqualTo(8 * 1024 * 1024));
                Assert.That(memory.GetTrace().Slice(0, memorySize).ToArray(), Is.EqualTo(expected));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Test]
    public void Growth_into_fresh_non_pooled_large_allocation_preserves_prefix_and_zeroes_gap()
    {
        using ThreadCacheReservation cacheReservation = PrimeDirtyBuffer();
        const int prefixOffset = 257;
        const int memorySize = (4 * 1024 * 1024) + 96;
        int destinationOffset = memorySize - EvmPooledMemory.WordSize;
        byte[] prefix = CreatePattern(97, 0x23, 31);
        byte[] word = CreatePattern(EvmPooledMemory.WordSize, 0x41, 37);
        byte[] expected = new byte[memorySize];
        prefix.CopyTo(expected, prefixOffset);
        word.CopyTo(expected, destinationOffset);

        EvmPooledMemory memory = default;
        try
        {
            UInt256 prefixDestination = (UInt256)prefixOffset;
            Assert.That(memory.TrySave(in prefixDestination, prefix), Is.True);
            AssertDirtyTailWasReused(ref memory);

            UInt256 destination = (UInt256)destinationOffset;
            Assert.That(memory.TrySave(in destination, word), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(memory.Size, Is.EqualTo((ulong)memorySize));
                Assert.That(memory.GetTrace().Slice(0, memorySize).ToArray(), Is.EqualTo(expected));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Test]
    public void Grow_after_dirty_reuse_preserves_written_prefix_and_zeroes_tail()
    {
        const int firstSize = 2048;
        const int secondSize = 8192;

        EvmPooledMemory dirty = new();
        Span<byte> pattern = new byte[firstSize];
        pattern.Fill(0xaa);
        Assert.That(dirty.TrySave(UInt256.Zero, pattern), Is.True);
        dirty.Dispose();

        EvmPooledMemory next = new();
        try
        {
            byte[] firstWord = TestItem.KeccakA.BytesToArray();
            Assert.That(next.TrySaveWord(UInt256.Zero, firstWord), Is.True);

            UInt256 growOffset = (UInt256)(secondSize - EvmPooledMemory.WordSize);
            Assert.That(next.TryLoadSpan(in growOffset, (UInt256)EvmPooledMemory.WordSize, out Span<byte> tail), Is.True);
            Assert.That(tail.ToArray(), Is.EqualTo(new byte[EvmPooledMemory.WordSize]));

            Assert.That(next.TryLoadSpan(UInt256.Zero, (UInt256)EvmPooledMemory.WordSize, out Span<byte> head), Is.True);
            Assert.That(head.ToArray(), Is.EqualTo(firstWord));
        }
        finally
        {
            next.Dispose();
        }
    }

    [Test]
    public void GetTrace_slice_past_size_does_not_leak_dirty_bytes()
    {
        // Must exceed the 4 KiB RentSlow zero chunk, otherwise the whole buffer is zeroed anyway.
        const int dirtySize = 32 * 1024;
        EvmPooledMemory dirty = new();
        Span<byte> pattern = new byte[dirtySize];
        pattern.Fill(0xff);
        Assert.That(dirty.TrySave(UInt256.Zero, pattern), Is.True);
        dirty.Dispose();

        EvmPooledMemory memory = new();
        try
        {
            // TrySaveWord rents (unlike CalculateMemoryCost), so the dirty buffer is reused with Size = 32.
            Assert.That(memory.TrySaveWord(UInt256.Zero, new byte[EvmPooledMemory.WordSize]), Is.True);

            TraceMemory trace = memory.GetTrace();
            Assert.That(trace.Size, Is.EqualTo((ulong)EvmPooledMemory.WordSize));
            Assert.That(trace.Slice(0, 8 * 1024).ToArray(), Is.EqualTo(new byte[8 * 1024]), "trace leaked dirty tail bytes past Size");
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Test]
    public void CalculateMemoryCost_LengthExceedsLongMax_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 length = (UInt256)long.MaxValue + 1; // just over long.MaxValue
        ulong result = memory.CalculateMemoryCost(0, in length, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_LocationPlusLengthOverflows_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = ulong.MaxValue;
        ulong result = memory.CalculateMemoryCost(in location, 1, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_TotalSizeExceedsLongMax_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = (UInt256)long.MaxValue;
        ulong result = memory.CalculateMemoryCost(in location, 1, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_TotalSizeExceedsIntMaxAfterWordAlignment_ShouldReturnOutOfGas()
    {
        // Test that memory requests that would overflow int.MaxValue after word alignment
        // are properly rejected. This prevents crashes in .NET array operations.
        // The limit is int.MaxValue - WordSize + 1 to ensure word-aligned size fits in int.
        EvmPooledMemory memory = new();

        // Request exactly at the limit should succeed
        UInt256 maxAllowedSize = (UInt256)(int.MaxValue - EvmPooledMemory.WordSize + 1);
        ulong result = memory.CalculateMemoryCost(0, in maxAllowedSize, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(false), "Size at limit should be allowed");

        // Request one byte over the limit should fail
        UInt256 overLimitSize = maxAllowedSize + 1;
        result = memory.CalculateMemoryCost(0, in overLimitSize, out outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true), "Size over limit should return out of gas");
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_MaxAllowedSize_ShouldReturnExpectedCostForBothLengthOverloads()
    {
        decimal maxWords = EvmPooledMemory.MaxMemoryWords;
        ulong expectedCost = decimal.ToUInt64(
            maxWords * GasCostOf.Memory +
            decimal.Floor((maxWords * maxWords) / 512m));

        EvmPooledMemory ulongMemory = new();
        ulong ulongResult = ulongMemory.CalculateMemoryCost(0, EvmPooledMemory.MaxMemorySize, out bool ulongOutOfGas);
        Assert.That(ulongOutOfGas, Is.EqualTo(false));
        Assert.That(ulongResult, Is.EqualTo(expectedCost));

        EvmPooledMemory uint256Memory = new();
        UInt256 maxAllowedSize = (UInt256)EvmPooledMemory.MaxMemorySize;
        ulong uint256Result = uint256Memory.CalculateMemoryCost(0, in maxAllowedSize, out bool uint256OutOfGas);
        Assert.That(uint256OutOfGas, Is.EqualTo(false));
        Assert.That(uint256Result, Is.EqualTo(expectedCost));
    }

    [Test]
    public void CalculateMemoryCost_4GBMemoryRequest_ShouldReturnOutOfGas()
    {
        // Regression test: 4GB memory request (0xffffffff) should return out-of-gas
        // instead of causing integer overflow crash in array operations.
        EvmPooledMemory memory = new();
        UInt256 size4GB = 0xffffffffUL;
        ulong result = memory.CalculateMemoryCost(0, in size4GB, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true), "4GB memory request should return out of gas");
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void CalculateMemoryCost_LargeOffsetPlusLength_ShouldReturnOutOfGas()
    {
        // Test that location + length exceeding int.MaxValue - WordSize + 1 returns out-of-gas
        EvmPooledMemory memory = new();
        UInt256 location = (UInt256)(int.MaxValue / 2);
        UInt256 length = (UInt256)(int.MaxValue / 2 + 100); // Sum exceeds limit
        ulong result = memory.CalculateMemoryCost(in location, in length, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(true), "Location + length exceeding limit should return out of gas");
        Assert.That(result, Is.EqualTo(0UL));
    }

    [Test]
    public void Save_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TrySave(in location, new byte[32]);
        Assert.That(outOfGas, Is.EqualTo(true));
    }

    [Test]
    public void SaveWord_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TrySaveWord(in location, new byte[32]);
        Assert.That(outOfGas, Is.EqualTo(true));
    }

    [Test]
    public void SaveByte_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TrySaveByte(in location, 0x42);
        Assert.That(outOfGas, Is.EqualTo(true));
    }

    [Test]
    public void LoadSpan_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TryLoadSpan(in location, out Span<byte> result);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result.IsEmpty, Is.EqualTo(true));
    }

    [Test]
    public void LoadSpan_LengthExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 length = new(0, 1, 0, 0);
        bool outOfGas = !memory.TryLoadSpan(0, in length, out Span<byte> result);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result.IsEmpty, Is.EqualTo(true));
    }

    [Test]
    public void Load_LocationExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 location = new(0, 1, 0, 0);
        bool outOfGas = !memory.TryLoad(in location, 32, out ReadOnlyMemory<byte> result);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result.IsEmpty, Is.EqualTo(true));
    }

    [Test]
    public void Load_LengthExceedsULong_ShouldReturnOutOfGas()
    {
        EvmPooledMemory memory = new();
        UInt256 length = new(0, 1, 0, 0);
        bool outOfGas = !memory.TryLoad(0, in length, out ReadOnlyMemory<byte> result);
        Assert.That(outOfGas, Is.EqualTo(true));
        Assert.That(result.IsEmpty, Is.EqualTo(true));
    }

    [Test]
    public void VmState_inline_storage_clears_only_the_required_gap_after_reuse()
    {
        VmState<EthereumGasPolicy> owner = new();
        ref EvmPooledMemory memory = ref owner.Memory;
        UInt256 start = UInt256.Zero;

        try
        {
            memory.CalculateMemoryCost(in start, 256, out bool outOfGas);
            Assert.That(outOfGas, Is.False);
            Span<byte> inlineMemory = memory.LoadSpanAfterGas(in start, 256);
            inlineMemory.Fill(0xa7);
            memory.Dispose();

            byte[] word = CreatePattern(EvmPooledMemory.WordSize, 0x31);
            UInt256 destination = 64;
            memory.CalculateMemoryCost(in destination, EvmPooledMemory.WordSize, out outOfGas);
            Assert.That(outOfGas, Is.False);
            memory.StoreWordAfterGas(in destination, word);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(memory.Size, Is.EqualTo(96));
                Assert.That(GetBackingMemory(ref memory), Is.Null);
                Assert.That(inlineMemory[..64].IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
                Assert.That(inlineMemory.Slice(64, EvmPooledMemory.WordSize).ToArray(), Is.EqualTo(word));
                Assert.That(inlineMemory[96], Is.EqualTo(0xa7));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCase(1)]
    [TestCase(16)]
    public void Reserved_contiguous_MSTORE_does_not_materialize_unwritten_tail(int wordCount)
    {
        using ThreadCacheReservation cacheReservation = PrimeDirtyBuffer();
        const int reservationSize = 4 * 1024;
        byte[] prefix = CreatePattern(EvmPooledMemory.WordSize, 0x21);
        byte[] word = CreatePattern(EvmPooledMemory.WordSize, 0x61);
        byte[] expected = new byte[(wordCount + 1) * EvmPooledMemory.WordSize];
        prefix.CopyTo(expected, 0);
        for (int i = 0; i < wordCount; i++)
        {
            word.CopyTo(expected, (i + 1) * EvmPooledMemory.WordSize);
        }

        EvmPooledMemory memory = new();
        UInt256 start = UInt256.Zero;
        try
        {
            memory.CalculateMemoryCost(in start, EvmPooledMemory.WordSize, out bool outOfGas);
            Assert.That(outOfGas, Is.False);
            memory.SaveAfterGas(in start, prefix);
            byte[]? backingMemory = GetBackingMemory(ref memory);
            Assert.That(backingMemory, Is.Not.Null);
            Assert.That(backingMemory![EvmPooledMemory.WordSize], Is.EqualTo(0xa7));

            memory.CalculateMemoryCost(in start, reservationSize, out outOfGas);
            Assert.That(outOfGas, Is.False);
            for (int i = 0; i < wordCount; i++)
            {
                UInt256 destination = (UInt256)((i + 1) * EvmPooledMemory.WordSize);
                memory.StoreWordAfterGas(in destination, word);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(memory.Size, Is.EqualTo(reservationSize));
                Assert.That(GetBackingMemory(ref memory), Is.SameAs(backingMemory));
                Assert.That(backingMemory.AsSpan(0, expected.Length).ToArray(), Is.EqualTo(expected));
                Assert.That(backingMemory[expected.Length], Is.EqualTo(0xa7));
            }

            byte[] visibleMemory = memory.GetTrace().Slice(0, reservationSize).ToArray();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(visibleMemory.AsSpan(0, expected.Length).ToArray(), Is.EqualTo(expected));
                Assert.That(visibleMemory.AsSpan(expected.Length).IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCase(false, 512)]
    [TestCase(false, 4 * 1024)]
    [TestCase(true, 512)]
    [TestCase(true, 4 * 1024)]
    public void Read_within_initialized_prefix_does_not_materialize_larger_logical_expansion(
        bool afterGas,
        int reservationSize)
    {
        VmState<EthereumGasPolicy> owner = new();
        ref EvmPooledMemory memory = ref owner.Memory;
        UInt256 start = UInt256.Zero;

        try
        {
            memory.CalculateMemoryCost(in start, 256, out bool outOfGas);
            Assert.That(outOfGas, Is.False);
            Span<byte> inlineMemory = memory.LoadSpanAfterGas(in start, 256);
            inlineMemory.Fill(0xa7);
            memory.Dispose();

            byte[] word = CreatePattern(EvmPooledMemory.WordSize, 0x31);
            UInt256 destination = 64;
            memory.CalculateMemoryCost(in destination, EvmPooledMemory.WordSize, out outOfGas);
            Assert.That(outOfGas, Is.False);
            memory.StoreWordAfterGas(in destination, word);
            memory.CalculateMemoryCost(in start, (ulong)reservationSize, out outOfGas);
            Assert.That(outOfGas, Is.False);

            byte[] actual;
            if (afterGas)
            {
                actual = memory.LoadSpanAfterGas(in destination, EvmPooledMemory.WordSize).ToArray();
            }
            else
            {
                UInt256 length = EvmPooledMemory.WordSize;
                Assert.That(memory.TryLoad(in destination, in length, out ReadOnlyMemory<byte> loaded), Is.True);
                actual = loaded.ToArray();
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(memory.Size, Is.EqualTo((ulong)reservationSize));
                Assert.That(GetBackingMemory(ref memory), Is.Null);
                Assert.That(actual, Is.EqualTo(word));
                Assert.That(inlineMemory[96], Is.EqualTo(0xa7));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Test]
    public void VmState_inline_memory_view_tracks_spill_without_renting_early()
    {
        VmState<EthereumGasPolicy> owner = new();
        ref EvmPooledMemory memory = ref owner.Memory;
        byte[] expected = CreatePattern(96, 0x31);
        byte[] replacement = CreatePattern(96, 0x57);
        UInt256 start = UInt256.Zero;
        UInt256 length = (UInt256)expected.Length;

        try
        {
            Assert.That(memory.TrySave(in start, expected), Is.True);
            Assert.That(memory.TryLoad(in start, in length, out ReadOnlyMemory<byte> view), Is.True);
            Assert.That(GetBackingMemory(ref memory), Is.Null);

            UInt256 spillDestination = EvmPooledMemory.InlineCapacity;
            Assert.That(memory.TrySave(in spillDestination, expected), Is.True);
            byte[]? backingMemory = GetBackingMemory(ref memory);
            Assert.That(memory.Inspect(in start, in length).ToArray(), Is.EqualTo(expected));
            Assert.That(memory.TrySave(in start, replacement), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(backingMemory, Is.Not.Null);
                Assert.That(
                    backingMemory!.Length,
                    Is.GreaterThanOrEqualTo(EvmPooledMemory.InlineCapacity + expected.Length));
                Assert.That(System.Numerics.BitOperations.IsPow2((uint)backingMemory.Length), Is.True);
                Assert.That(view.ToArray(), Is.EqualTo(replacement));
                Assert.That(memory.Inspect(in start, in length).ToArray(), Is.EqualTo(replacement));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCase(false, TestName = "VmState_inline_memory_view_exposes_array_by_spilling")]
    [TestCase(true, TestName = "VmState_inline_memory_view_pins_by_spilling")]
    public void VmState_inline_memory_view_supports_standard_memory_consumers(bool pin)
    {
        VmState<EthereumGasPolicy> owner = new();
        ref EvmPooledMemory memory = ref owner.Memory;
        byte[] expected = CreatePattern(96, 0x41);
        UInt256 start = 17;
        UInt256 length = (UInt256)expected.Length;

        try
        {
            UInt256 zero = UInt256.Zero;
            memory.CalculateMemoryCost(in zero, 256, out bool outOfGas);
            Assert.That(outOfGas, Is.False);
            memory.LoadSpanAfterGas(in zero, 256).Fill(0xa7);
            memory.Dispose();

            Assert.That(memory.TrySave(in start, expected), Is.True);
            Assert.That(memory.TryLoad(in start, in length, out ReadOnlyMemory<byte> view), Is.True);
            Assert.That(GetBackingMemory(ref memory), Is.Null);

            byte[] actual;
            if (pin)
            {
                unsafe
                {
                    // The handle pins the view for this scope, and the copy is bounded by the view length.
                    using MemoryHandle handle = view.Pin();
                    actual = new ReadOnlySpan<byte>(handle.Pointer, expected.Length).ToArray();
                }
            }
            else
            {
                Assert.That(MemoryMarshal.TryGetArray(view, out ArraySegment<byte> segment), Is.True);
                actual = segment.AsSpan().ToArray();
            }

            byte[]? backingMemory = GetBackingMemory(ref memory);
            byte unmaterializedTail = backingMemory![17 + expected.Length];
            UInt256 tail = (UInt256)(17 + expected.Length);
            UInt256 one = UInt256.One;
            Assert.That(memory.TryLoad(in tail, in one, out ReadOnlyMemory<byte> clearedTail), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(backingMemory, Is.Not.Null);
                Assert.That(System.Numerics.BitOperations.IsPow2((uint)backingMemory!.Length), Is.True);
                Assert.That(actual, Is.EqualTo(expected));
                Assert.That(view.ToArray(), Is.EqualTo(expected));
                Assert.That(unmaterializedTail, Is.EqualTo(0xa7));
                Assert.That(clearedTail.Span[0], Is.EqualTo(0));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Test]
    public void VmState_owned_load_survives_inline_memory_reuse()
    {
        VmState<EthereumGasPolicy> owner = new();
        ref EvmPooledMemory memory = ref owner.Memory;
        byte[] expected = CreatePattern(96, 0x31);
        byte[] replacement = CreatePattern(96, 0x57);
        UInt256 location = UInt256.Zero;
        UInt256 length = (UInt256)expected.Length;

        try
        {
            Assert.That(memory.TrySave(in location, expected), Is.True);
            Assert.That(memory.TryLoadOwned(in location, in length, out ReadOnlyMemory<byte> owned), Is.True);

            memory.Dispose();
            Assert.That(memory.TrySave(in location, replacement), Is.True);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(owned.ToArray(), Is.EqualTo(expected));
                Assert.That(memory.Inspect(in location, in length).ToArray(), Is.EqualTo(replacement));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Test]
    public void Inspect_should_not_change_evm_memory()
    {
        EvmPooledMemory memory = new();
        bool outOfGas = !memory.TrySave(3, TestItem.KeccakA.Bytes);
        Assert.That(outOfGas, Is.EqualTo(false));
        ulong initialSize = memory.Size;
        ReadOnlyMemory<byte> result = memory.Inspect(initialSize + 32, 32);
        Assert.That(memory.Size, Is.EqualTo(initialSize));
        Assert.That(result, Is.EqualTo(ReadOnlyMemory<byte>.Empty));
    }

    [Test]
    public void Inspect_can_read_memory()
    {
        const int offset = 3;
        byte[] expectedEmptyRead = new byte[32 - offset];
        byte[] expectedKeccakRead = TestItem.KeccakA.BytesToArray();
        EvmPooledMemory memory = new();
        bool outOfGas = !memory.TrySave((UInt256)offset, expectedKeccakRead);
        Assert.That(outOfGas, Is.EqualTo(false));
        ulong initialSize = memory.Size;
        ReadOnlyMemory<byte> actualKeccakMemoryRead = memory.Inspect((UInt256)offset, 32);
        ReadOnlyMemory<byte> actualEmptyRead = memory.Inspect(32 + (UInt256)offset, 32 - (UInt256)offset);
        Assert.That(memory.Size, Is.EqualTo(initialSize));
        Assert.That(actualKeccakMemoryRead.ToArray(), Is.EqualTo(expectedKeccakRead));
        Assert.That(actualEmptyRead.ToArray(), Is.EqualTo(expectedEmptyRead));
    }

    [Test]
    public void Load_should_update_size_of_memory()
    {
        byte[] expectedResult = new byte[32];
        EvmPooledMemory memory = new();
        bool outOfGas = !memory.TrySave(3, TestItem.KeccakA.Bytes);
        Assert.That(outOfGas, Is.EqualTo(false));
        ulong initialSize = memory.Size;
        outOfGas = !memory.TryLoad(initialSize + 32, 32, out ReadOnlyMemory<byte> result);
        Assert.That(outOfGas, Is.EqualTo(false));
        Assert.That(memory.Size, Is.Not.EqualTo(initialSize));
        Assert.That(result.ToArray(), Is.EqualTo(expectedResult));
    }

    [TestCase(32)]
    [TestCase(64)]
    [TestCase(1024)]
    [TestCase(4096)]
    public void IncrementalGrowth_preserves_written_data_and_zeroes_new_regions(int step)
    {
        EvmPooledMemory memory = new();
        byte[] word = TestItem.KeccakA.BytesToArray();

        Assert.That(memory.TrySaveWord(0, word), Is.True);

        for (int offset = step; offset <= 64 * 1024; offset += step)
        {
            Assert.That(memory.TryLoadSpan((UInt256)offset, (UInt256)EvmPooledMemory.WordSize, out Span<byte> read), Is.True);
            Assert.That(read.ToArray(), Is.EqualTo(new byte[EvmPooledMemory.WordSize]),
                $"memory at offset {offset} must read as zero after growth to {memory.Size}");
        }

        Assert.That(memory.TryLoadSpan(0, (UInt256)EvmPooledMemory.WordSize, out Span<byte> first), Is.True);
        Assert.That(first.ToArray(), Is.EqualTo(word), "originally written word must survive re-rent");
    }

    // Sizes bracket the pooling boundaries: 64 KiB (largest fresh allocation), 256 KiB (largest
    // thread-cached buffer) and above (shared pools) — every rent source must hand out zeroed-
    // reading memory even after a deliberately dirtied buffer was recycled through it.
    [TestCase(64 * 1024 - 32)]
    [TestCase(64 * 1024)]
    [TestCase(64 * 1024 + 32)]
    [TestCase(256 * 1024)]
    [TestCase(256 * 1024 + 32)]
    [TestCase(1024 * 1024)]
    public void Growth_across_pooling_boundaries_reads_zero_and_recycles_clean(int size)
    {
        byte[] word = TestItem.KeccakA.BytesToArray();

        EvmPooledMemory original = new();
        Assert.That(original.TrySaveWord(0, word), Is.True);
        Assert.That(original.TryLoadSpan((UInt256)(size - EvmPooledMemory.WordSize), (UInt256)EvmPooledMemory.WordSize, out Span<byte> tail), Is.True);
        Assert.That(tail.ToArray(), Is.EqualTo(new byte[EvmPooledMemory.WordSize]), "grown tail must read as zero");
        Assert.That(original.TryLoadSpan(0, (UInt256)EvmPooledMemory.WordSize, out Span<byte> head), Is.True);
        Assert.That(head.ToArray(), Is.EqualTo(word), "written word must survive growth");
        Assert.That(original.TryLoadSpan(0, (UInt256)size, out Span<byte> whole), Is.True);
        whole.Fill(0xff);
        original.Dispose();

        EvmPooledMemory recycled = new();
        Assert.That(recycled.TryLoadSpan(0, (UInt256)size, out Span<byte> reused), Is.True);
        Assert.That(reused.IndexOfAnyExcept((byte)0), Is.EqualTo(-1), "recycled buffer must read as zero");
        recycled.Dispose();
    }

    [Test]
    public void CopyAfterGas_after_dispose_treats_reused_memory_as_uninitialized()
    {
        using ThreadCacheReservation cacheReservation = PrimeDirtyBuffer();
        const int memorySize = 4 * 1024;
        byte[] dirtyBytes = new byte[memorySize];
        dirtyBytes.AsSpan().Fill(0xa7);
        UInt256 zero = UInt256.Zero;
        EvmPooledMemory memory = new();

        try
        {
            Assert.That(memory.TryLoadSpan(in zero, (UInt256)memorySize, out _), Is.True);
            byte[]? firstBackingMemory = GetBackingMemory(ref memory);
            Assert.That(firstBackingMemory, Is.Not.Null);
            memory.Dispose();

            EvmPooledMemory dirty = new();
            try
            {
                Assert.That(dirty.TrySave(in zero, dirtyBytes), Is.True);
                Assert.That(GetBackingMemory(ref dirty), Is.SameAs(firstBackingMemory));
            }
            finally
            {
                dirty.Dispose();
            }

            memory.CalculateMemoryCost(in zero, EvmPooledMemory.WordSize, out bool outOfGas);
            Assert.That(outOfGas, Is.False);
            memory.CopyAfterGas(in zero, in zero, EvmPooledMemory.WordSize);

            byte[] actual = ReadVisibleMemory(ref memory);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetBackingMemory(ref memory), Is.SameAs(firstBackingMemory));
                Assert.That(actual, Has.Length.EqualTo(EvmPooledMemory.WordSize));
                Assert.That(actual.AsSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void CopyAfterGas_sizes_storage_for_destination_not_logically_zero_source(bool allocateDestinationFirst)
    {
        using ThreadCacheReservation _ = new();
        byte[] word = CreatePattern(EvmPooledMemory.WordSize, 0x31);
        EvmPooledMemory memory = new();

        try
        {
            byte[]? originalBackingMemory = null;
            if (allocateDestinationFirst)
            {
                Assert.That(memory.TrySaveWord(UInt256.Zero, word), Is.True);
                originalBackingMemory = GetBackingMemory(ref memory);
                Assert.That(originalBackingMemory, Has.Length.EqualTo(1024));
            }

            UInt256 destination = UInt256.Zero;
            UInt256 source = 8 * 1024;
            memory.CalculateMemoryCost(in source, EvmPooledMemory.WordSize, out bool outOfGas);
            Assert.That(outOfGas, Is.False);

            memory.CopyAfterGas(in destination, in source, EvmPooledMemory.WordSize);
            UInt256 inspectLength = EvmPooledMemory.WordSize;
            ReadOnlyMemory<byte> inspected = memory.Inspect(in source, in inspectLength);
            byte[]? backingMemory = GetBackingMemory(ref memory);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(backingMemory, Has.Length.EqualTo(1024));
                if (originalBackingMemory is not null)
                {
                    Assert.That(backingMemory, Is.SameAs(originalBackingMemory));
                }
                Assert.That(inspected.IsEmpty, Is.True);
            }

            Assert.That(ReadVisibleMemory(ref memory).AsSpan().IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCase(false, 0)]
    [TestCase(false, 1000)]
    [TestCase(false, 4095)]
    [TestCase(false, 5000)]
    [TestCase(true, 0)]
    [TestCase(true, 1023)]
    [TestCase(true, 4096)]
    [TestCase(true, 5000)]
    public void StoreAfterGas_matches_independent_model_on_dirty_reused_buffer(bool storeByte, int offset)
    {
        using ThreadCacheReservation cacheReservation = PrimeDirtyBuffer();

        int length = storeByte ? 1 : EvmPooledMemory.WordSize;
        byte[] expected = new byte[AlignToWord(offset + length)];
        byte[] word = CreatePattern(EvmPooledMemory.WordSize, 0x31);
        const byte value = 0xa5;
        if (storeByte)
        {
            expected[offset] = value;
        }
        else
        {
            word.CopyTo(expected, offset);
        }

        EvmPooledMemory memory = new();
        try
        {
            UInt256 location = (UInt256)offset;
            memory.CalculateMemoryCost(in location, (ulong)length, out bool outOfGas);
            Assert.That(outOfGas, Is.False);

            if (storeByte)
            {
                memory.StoreByteAfterGas(in location, value);
            }
            else
            {
                memory.StoreWordAfterGas(in location, word);
            }

            AssertDirtyTailWasReused(ref memory);
            Assert.That(ReadVisibleMemory(ref memory), Is.EqualTo(expected));
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCase(0, TestName = "SaveAfterGas_contiguous_overwrite_leaves_later_memory_lazy")]
    [TestCase(5000, TestName = "SaveAfterGas_jump_clears_only_the_gap")]
    public void Full_overwrite_materializes_only_gap_and_written_range_on_dirty_reuse(int offset)
    {
        using ThreadCacheReservation cacheReservation = PrimeDirtyBuffer();

        const int length = 97;
        int prefixLength = offset == 0 ? 0 : EvmPooledMemory.WordSize;
        byte[] prefix = CreatePattern(prefixLength, 0x19);
        byte[] source = CreatePattern(length, 0x53);
        byte[] expected = new byte[AlignToWord(offset + length)];
        prefix.CopyTo(expected, 0);
        source.CopyTo(expected, offset);

        EvmPooledMemory memory = new();
        try
        {
            if (prefixLength != 0)
            {
                Assert.That(memory.TrySaveWord(UInt256.Zero, prefix), Is.True);
            }

            UInt256 destination = (UInt256)offset;
            memory.CalculateMemoryCost(in destination, (ulong)length, out bool outOfGas);
            Assert.That(outOfGas, Is.False);

            memory.SaveAfterGas(in destination, source);

            byte[]? backingMemory = GetBackingMemory(ref memory);
            Assert.That(backingMemory, Is.Not.Null);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(backingMemory!.AsSpan(offset, length).ToArray(), Is.EqualTo(source));
                if (prefixLength != 0)
                {
                    Assert.That(backingMemory.AsSpan(prefixLength, offset - prefixLength).IndexOfAnyExcept((byte)0), Is.EqualTo(-1));
                }
                Assert.That(backingMemory[offset + length], Is.EqualTo(0xa7), "logical zeroes after the overwrite should remain unmaterialized");
            }

            Assert.That(ReadVisibleMemory(ref memory), Is.EqualTo(expected));
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCaseSource(nameof(ZeroExtendedCopyCases))]
    public void CopyFromZeroExtendedAfterGas_copies_and_zeroes_only_the_destination(
        byte[] source,
        UInt256 sourceOffset,
        int length,
        byte[] expected)
    {
        const int destinationOffset = 3;
        const int inspectedLength = 64;
        EvmPooledMemory memory = new();
        UInt256 memoryStart = UInt256.Zero;
        UInt256 destination = destinationOffset;

        try
        {
            memory.CalculateMemoryCost(in memoryStart, inspectedLength, out bool outOfGas);
            Assert.That(outOfGas, Is.False);
            memory.LoadSpanAfterGas(in memoryStart, inspectedLength).Fill(0xff);

            memory.CopyFromZeroExtendedAfterGas(in destination, source, in sourceOffset, length);

            ReadOnlySpan<byte> actual = memory.Inspect(memoryStart, inspectedLength).Span;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(actual[..destinationOffset].ToArray(), Is.EqualTo(new byte[] { 0xff, 0xff, 0xff }));
                Assert.That(actual.Slice(destinationOffset, length).ToArray(), Is.EqualTo(expected));
                Assert.That(actual[destinationOffset + length], Is.EqualTo(0xff));
            }
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCaseSource(nameof(ExpandedZeroExtendedCopyCases))]
    public void CopyFromZeroExtendedAfterGas_matches_independent_model_on_dirty_reused_buffer(
        int initialLength,
        int destinationOffset,
        int sourceLength,
        ulong sourceOffset,
        int length)
    {
        using ThreadCacheReservation cacheReservation = PrimeDirtyBuffer();
        byte[] initial = CreatePattern(initialLength, 0x11);
        byte[] source = CreatePattern(sourceLength, 0x61);
        int expectedSize = AlignToWord(Math.Max(initialLength, destinationOffset + length));
        byte[] expected = new byte[expectedSize];
        initial.CopyTo(expected, 0);
        for (int i = 0; i < length; i++)
        {
            ulong sourceIndex = sourceOffset + (ulong)i;
            expected[destinationOffset + i] = sourceIndex < (ulong)source.Length
                ? source[(int)sourceIndex]
                : (byte)0;
        }

        EvmPooledMemory memory = new();
        try
        {
            WriteInitialMemory(ref memory, initial);
            if (initial.Length != 0)
            {
                AssertDirtyTailWasReused(ref memory);
            }

            UInt256 destination = (UInt256)destinationOffset;
            UInt256 uint256SourceOffset = (UInt256)sourceOffset;
            memory.CalculateMemoryCost(in destination, (ulong)length, out bool outOfGas);
            Assert.That(outOfGas, Is.False);

            memory.CopyFromZeroExtendedAfterGas(in destination, source, in uint256SourceOffset, length);

            Assert.That(ReadVisibleMemory(ref memory), Is.EqualTo(expected));
        }
        finally
        {
            memory.Dispose();
        }
    }

    [Test]
    public void TrySave_full_overwrite_matches_independent_model_on_dirty_reused_buffer()
    {
        const int destinationOffset = 5000;
        const int sourceLength = 64 * 1024;
        using ThreadCacheReservation cacheReservation = PrimeDirtyBuffer();
        byte[] initial = CreatePattern(64, 0x21);
        byte[] source = CreatePattern(sourceLength, 0x81);
        byte[] expected = new byte[AlignToWord(destinationOffset + sourceLength)];
        initial.CopyTo(expected, 0);
        source.CopyTo(expected, destinationOffset);

        EvmPooledMemory memory = new();
        try
        {
            WriteInitialMemory(ref memory, initial);
            AssertDirtyTailWasReused(ref memory);
            UInt256 destination = destinationOffset;
            Assert.That(memory.TrySave(in destination, source), Is.True);

            Assert.That(ReadVisibleMemory(ref memory), Is.EqualTo(expected));
        }
        finally
        {
            memory.Dispose();
        }
    }

    [TestCaseSource(nameof(MCopyReferenceCases))]
    public void CopyAfterGas_matches_snapshot_model_on_dirty_reused_buffer(
        int initialLength,
        int destinationOffset,
        int sourceOffset,
        int length,
        bool materializeSourceFirst)
    {
        using ThreadCacheReservation cacheReservation = PrimeDirtyBuffer();
        byte[] initial = CreatePattern(initialLength, 0x31);
        int expectedSize = AlignToWord(Math.Max(destinationOffset + length, sourceOffset + length));
        byte[] before = new byte[expectedSize];
        initial.CopyTo(before, 0);
        byte[] expected = (byte[])before.Clone();
        byte[] sourceSnapshot = before.AsSpan(sourceOffset, length).ToArray();
        sourceSnapshot.CopyTo(expected, destinationOffset);

        EvmPooledMemory memory = new();
        try
        {
            WriteInitialMemory(ref memory, initial);
            if (initialLength <= 16 * 1024)
            {
                AssertDirtyTailWasReused(ref memory);
            }
            UInt256 destination = (UInt256)destinationOffset;
            UInt256 source = (UInt256)sourceOffset;
            UInt256 expansionStart = (UInt256)Math.Max(destinationOffset, sourceOffset);
            memory.CalculateMemoryCost(in expansionStart, (ulong)length, out bool outOfGas);
            Assert.That(outOfGas, Is.False);

            if (materializeSourceFirst)
            {
                Assert.That(memory.LoadSpanAfterGas(in source, (ulong)length).ToArray(), Is.EqualTo(sourceSnapshot));
            }

            memory.CopyAfterGas(in destination, in source, (ulong)length);

            Assert.That(ReadVisibleMemory(ref memory), Is.EqualTo(expected));
        }
        finally
        {
            memory.Dispose();
        }
    }

    private static ThreadCacheReservation PrimeDirtyBuffer()
    {
        ThreadCacheReservation cacheReservation = new();
        const int dirtySize = 32 * 1024;
        byte[] dirtyBytes = new byte[dirtySize];
        dirtyBytes.AsSpan().Fill(0xa7);
        EvmPooledMemory dirty = new();
        try
        {
            Assert.That(dirty.TrySave(UInt256.Zero, dirtyBytes), Is.True);
            return cacheReservation;
        }
        catch
        {
            cacheReservation.Dispose();
            throw;
        }
        finally
        {
            dirty.Dispose();
        }
    }

    private static void AssertDirtyTailWasReused(ref EvmPooledMemory memory)
    {
        byte[]? backingMemory = GetBackingMemory(ref memory);
        Assert.That(backingMemory, Is.Not.Null);
        Assert.That(backingMemory!.Length, Is.GreaterThan(16 * 1024), "negative control requires a sufficiently large pooled buffer");
        Assert.That(backingMemory![16 * 1024], Is.EqualTo(0xa7), "negative control requires a stale pooled tail");
    }

    private sealed class ThreadCacheReservation : IDisposable
    {
        private const int ThreadCacheSlots = 16;
        private readonly EvmPooledMemory[] _rentals = new EvmPooledMemory[ThreadCacheSlots];

        public ThreadCacheReservation()
        {
            try
            {
                UInt256 zero = UInt256.Zero;
                for (int i = 0; i < _rentals.Length; i++)
                {
                    Assert.That(_rentals[i].TrySaveByte(in zero, 0), Is.True);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _rentals.Length; i++)
            {
                _rentals[i].Dispose();
            }
        }
    }

    private static void WriteInitialMemory(ref EvmPooledMemory memory, byte[] initial)
    {
        for (int offset = 0; offset < initial.Length; offset += EvmPooledMemory.WordSize)
        {
            UInt256 location = (UInt256)offset;
            Assert.That(memory.TrySaveWord(in location, initial.AsSpan(offset, EvmPooledMemory.WordSize)), Is.True);
        }
    }

    private static byte[] ReadVisibleMemory(ref EvmPooledMemory memory)
        => memory.GetTrace().Slice(0, (int)memory.Size).ToArray();

    private static byte[] CreatePattern(int length, byte seed, int multiplier = 37)
    {
        byte[] pattern = new byte[length];
        for (int i = 0; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(seed + i * multiplier);
        }

        return pattern;
    }

    private static int AlignToWord(int length)
        => (length + EvmPooledMemory.WordSize - 1) & ~(EvmPooledMemory.WordSize - 1);

    [Test]
    public void GetTrace_should_not_throw_on_not_initialized_memory()
    {
        EvmPooledMemory memory = new();
        memory.CalculateMemoryCost(0, 32, out bool outOfGas);
        Assert.That(outOfGas, Is.EqualTo(false));
        Assert.That(memory.GetTrace().ToHexWordList(), Is.EqualTo(new string[] { "0000000000000000000000000000000000000000000000000000000000000000" }));
    }

    [Test]
    public void GetTrace_memory_should_not_bleed_between_txs()
    {
        byte[] first = new byte[] {
            0x5b, 0x38, 0x36, 0x59, 0x59, 0x59, 0x59, 0x52, 0x3a, 0x60, 0x05, 0x30,
            0xf4, 0x05, 0x56};
        byte[] second = new byte[] {
            0x5b, 0x36, 0x59, 0x3a, 0x34, 0x60, 0x5b, 0x59, 0x05, 0x30, 0xf4, 0x3a,
            0x56};

        string a = Run(second).ToString();
        Run(first);
        string b = Run(second).ToString();

        Assert.That(b, Is.EqualTo(a));
    }

    [Test]
    public void GetTrace_memory_should_not_overflow()
    {
        byte[] input = new byte[] {
            0x5b, 0x59, 0x60, 0x20, 0x59, 0x81, 0x91, 0x52, 0x44, 0x36, 0x5a, 0x3b,
            0x59, 0xf4, 0x5b, 0x31, 0x56, 0x08};
        Run(input);
    }

    private static readonly PrivateKey PrivateKeyD = new("0000000000000000000000000000000000000000000000000000001000000000");
    private static readonly Address sender = new("0x59ede65f910076f60e07b2aeb189c72348525e72");

    private static readonly Address to = new("0x000000000000000000000000636f6e7472616374");
    private static readonly Address coinbase = new("0x4444588443C3a91288c5002483449Aba1054192b");
    // for testing purposes, particular chain id does not matter. Maybe make random id so it captures the idea that signature should would irrespective of chain
    private static readonly EthereumEcdsa ethereumEcdsa = new(BlockchainIds.GenericNonRealNetwork);
    private static string Run(byte[] input)
    {
        ulong blocknr = 12965000;
        ulong gas = 34218;
        ulong ts = 123456;
        IWorldState stateProvider = TestWorldStateFactory.CreateForTest();
        ISpecProvider specProvider = new TestSpecProvider(London.Instance);
        EthereumCodeInfoRepository codeInfoRepository = new(stateProvider);
        EthereumVirtualMachine virtualMachine = new(
            new TestBlockhashProvider(specProvider),
            specProvider,
            LimboLogs.Instance);
        ITransactionProcessor transactionProcessor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            stateProvider,
            virtualMachine,
            codeInfoRepository,
            LimboLogs.Instance);

        Hash256 stateRoot = null;
        using IDisposable _ = stateProvider.BeginScope(IWorldState.PreGenesis);
        stateProvider.CreateAccount(to, 123);
        stateProvider.InsertCode(to, input, specProvider.GenesisSpec);

        stateProvider.CreateAccount(sender, 40000000);
        stateProvider.Commit(specProvider.GenesisSpec);

        stateProvider.CommitTree(0);
        stateRoot = stateProvider.StateRoot;

        Transaction tx = Build.A.Transaction.
            WithData(input).
            WithTo(to).
            WithGasLimit(gas).
            WithGasPrice(0).
            WithValue(0).
            SignedAndResolved(ethereumEcdsa, PrivateKeyD, true).
            TestObject;
        Block block = Build.A.Block.
            WithBeneficiary(coinbase).
            WithNumber(blocknr + 1).
            WithTimestamp(ts).
            WithTransactions(tx).
            WithGasLimit(30000000).
            WithDifficulty(0).
            WithStateRoot(stateRoot).
            TestObject;
        MyTracer tracer = new();
        transactionProcessor.Execute(
                tx,
                new BlockExecutionContext(block.Header, specProvider.GetSpec(block.Header)),
                tracer);
        return tracer.lastmemline;
    }
}

public class MyTracer : ITxTracer, IDisposable
{
    public bool IsTracingReceipt => true;
    public bool IsTracingActions => false;
    public bool IsTracingOpLevelStorage => true;
    public bool IsTracingMemory => true;
    public bool IsTracingDetailedMemory { get; set; } = true;
    public bool IsTracingInstructions => true;
    public bool IsTracingRefunds { get; } = false;
    public bool IsTracingReturnData { get; } = false;
    public bool IsTracingCode => true;
    public bool IsTracingStack { get; set; } = true;
    public bool IsTracingState => false;
    public bool IsTracingStorage => false;
    public bool IsTracingBlockHash { get; } = false;
    public bool IsTracingAccess { get; } = false;
    public bool IsTracingFees => false;
    public bool IsTracingLogs => false;
    public bool IsTracing => IsTracingReceipt
                             || IsTracingActions
                             || IsTracingOpLevelStorage
                             || IsTracingMemory
                             || IsTracingInstructions
                             || IsTracingRefunds
                             || IsTracingCode
                             || IsTracingStack
                             || IsTracingBlockHash
                             || IsTracingAccess
                             || IsTracingFees
                             || IsTracingLogs;

    public string lastmemline;

    public void MarkAsSuccess(Address recipient, in GasConsumed gasSpent, byte[] output, LogEntry[] logs, Hash256? stateRoot = null)
    {
    }

    public void MarkAsFailed(Address recipient, in GasConsumed gasSpent, byte[] output, string? error, Hash256? stateRoot = null)
    {
    }

    public void StartOperation(int pc, Instruction opcode, ulong gas, in ExecutionEnvironment env)
    {
    }

    public void ReportOperationError(EvmExceptionType error)
    {
    }

    public void ReportOperationRemainingGas(ulong gas)
    {
    }

    public void ReportLog(LogEntry log)
    {
    }

    public void SetOperationStack(TraceStack stack)
    {
    }

    public void SetOperationMemory(TraceMemory memoryTrace) => lastmemline = string.Concat("0x", string.Join("", memoryTrace.ToHexWordList().Select(static mt => mt.Replace("0x", string.Empty))));

    public void SetOperationMemorySize(ulong newSize)
    {
    }

    public void SetOperationReturnData(ReadOnlyMemory<byte> returnData)
    {
    }

    public void ReportMemoryChange(long offset, in ReadOnlySpan<byte> data)
    {
    }

    public void ReportStorageChange(in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
    }

    public void SetOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
    }

    public void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value)
    {
    }

    public void ReportSelfDestruct(Address address, UInt256 balance, Address refundAddress) => throw new NotSupportedException();

    public void ReportBalanceChange(Address address, UInt256? before, UInt256? after) => throw new NotSupportedException();

    public void ReportCodeChange(Address address, byte[] before, byte[] after) => throw new NotSupportedException();

    public void ReportNonceChange(Address address, UInt256? before, UInt256? after) => throw new NotSupportedException();

    public void ReportAccountRead(Address address) => throw new NotImplementedException();

    public void ReportStorageChange(in StorageCell storageAddress, byte[] before, byte[] after) => throw new NotSupportedException();

    public void ReportStorageRead(in StorageCell storageCell) => throw new NotImplementedException();

    public void ReportAction(ulong gas, UInt256 value, Address from, Address to, ReadOnlyMemory<byte> input, ExecutionType callType, bool isPrecompileCall = false) => throw new NotSupportedException();

    public void ReportActionEnd(ulong gas, ReadOnlyMemory<byte> output) => throw new NotSupportedException();

    public void ReportActionError(EvmExceptionType exceptionType) => throw new NotSupportedException();

    public void ReportActionRevert(ulong gas, ReadOnlyMemory<byte> output) => throw new NotSupportedException();

    public void ReportActionEnd(ulong gas, Address deploymentAddress, ReadOnlyMemory<byte> deployedCode) => throw new NotSupportedException();

    public void ReportBlockHash(Hash256 blockHash) => throw new NotImplementedException();

    public void ReportByteCode(ReadOnlyMemory<byte> byteCode) => throw new NotSupportedException();

    public void ReportGasUpdateForVmTrace(ulong refund, ulong gasAvailable)
    {
    }

    public void ReportRefundForVmTrace(long refund, long gasAvailable)
    {
    }

    public void ReportRefund(long refund)
    {
    }

    public void ReportExtraGasPressure(ulong extraGasPressure) => throw new NotImplementedException();

    public void ReportAccess(IEnumerable<Address> accessedAddresses, IEnumerable<StorageCell> accessedStorageCells) => throw new NotImplementedException();

    public void ReportStackPush(in ReadOnlySpan<byte> stackItem)
    {
    }

    public void ReportFees(UInt256 fees, UInt256 burntFees) => throw new NotImplementedException();

    public void Dispose() { }
}
