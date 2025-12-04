// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Db.LogIndex;
using Nethermind.TurboPForBindings;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.All)]
[TestFixtureSource(nameof(Algorithms))]
public class TurboPFor2Tests(TurboPFor2Tests.Algorithm algorithm)
{
    [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
    public record Algorithm(string Name, CompressFunc Compress, DecompressFunc Decompress)
    {
        public override string ToString() => Name;
    }

    private static Algorithm[] Algorithms() =>
    [
        new("p4nd1*64", TurboPFor2.p4nd1enc64, TurboPFor2.p4nd1dec64)

        // Mixed version - don't work
        //new("p4nd1enc256v32 / p4nd1dec128v32", TurboPFor.p4nd1enc256v32, TurboPFor.p4nd1dec128v32),
        //new("p4nd1enc128v32 / p4nd1dec256v32", TurboPFor.p4nd1enc128v32, TurboPFor.p4nd1dec256v32)
    ];

    private static IEnumerable<long> Starts()
    {
        yield return 0;
        yield return int.MaxValue;
        yield return long.MaxValue - int.MaxValue / 2;
    }

    private static IEnumerable<int> Lengths()
    {
        yield return 1;
        yield return 10;

        for (var i = 32; i <= 2048; i <<= 1)
        {
            yield return i - 1;
            yield return i;
            yield return i + 1;
        }
    }

    private static IEnumerable<int> Deltas()
    {
        yield return 10;
        yield return 20;
        yield return 50;
        yield return 100;
        yield return 1000;
    }

    private static IEnumerable<long> Range(long start, int length)
    {
        for (var i = start; i < start + length; i++)
            yield return i;
    }

    [Test]
    [Combinatorial]
    public void Increasing_Consecutive(
        [ValueSource(nameof(Starts))] long start,
        [ValueSource(nameof(Lengths))] int length
    )
    {
        var values = Range(start, length).ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Increasing_Consecutive_Negative(
        [ValueSource(nameof(Starts))] long start,
        [ValueSource(nameof(Lengths))] int length
    )
    {
        var values = Range(start, length).Reverse().Select(x => -x).ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Increasing_Random(
        [Values(42, 4242, 424242)] int seed,
        [ValueSource(nameof(Starts))] long start,
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new Random(seed), start, length, maxDelta).ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Increasing_Random_Negative(
        [Values(42, 4242, 424242)] int seed,
        [ValueSource(nameof(Starts))] long start,
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new Random(seed), start, length, maxDelta).Reverse().Select(x => -x).ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Decreasing_Consecutive(
        [ValueSource(nameof(Starts))] long start,
        [ValueSource(nameof(Lengths))] int length
    )
    {
        var values = Range(start, length).Reverse().ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Decreasing_Random(
        [Values(42, 4242, 424242)] int seed,
        [ValueSource(nameof(Starts))] long start,
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new(42), start, length, maxDelta).Reverse().ToArray();
        Verify(values);
    }

    private static IEnumerable<long> RandomIncreasingRange(Random random, long start, int length, int maxDelta)
    {
        var value = 0;
        for (var i = start; i < start + length; i++)
        {
            value += random.Next(maxDelta);
            yield return value;
        }
    }

    public delegate nuint CompressFunc(ReadOnlySpan<long> @in, nuint n, Span<byte> @out);

    public delegate nuint DecompressFunc(ReadOnlySpan<byte> @in, nuint n, Span<long> @out);

    private void Verify(long[] values)
    {
        if (!TurboPFor.Supports256Blocks && algorithm.Name.Contains("256"))
            Assert.Ignore("256 blocks are not supported on this platform.");

        var compressed = Compress(values, algorithm.Compress);
        var decompressed = Decompress(compressed, values.Length, algorithm.Decompress);

        Assert.That(decompressed, Is.EqualTo(values));
    }

    private static byte[] Compress(long[] values, CompressFunc compressFunc)
    {
        var buffer = new byte[values.Length * sizeof(long) + 1024];

        var resultLength = (int)compressFunc(values, (nuint)values.Length, buffer);

        TestContext.Out.WriteLine($"Compressed: {resultLength} bytes");
        return buffer[..resultLength];
    }

    private static long[] Decompress(byte[] data, int count, DecompressFunc decompressFunc)
    {
        var buffer = new long[count + 1];
        for (var i = count; i < buffer.Length; i++)
            buffer[i] = -1;

        _ = decompressFunc(data, (nuint)count, buffer);

        for (var i = count; i < buffer.Length; i++) Assert.That(buffer[i], Is.EqualTo(-1));

        return buffer[..count];
    }
}
