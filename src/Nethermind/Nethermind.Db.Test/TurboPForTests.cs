// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using NUnit.Framework;

namespace Nethermind.Db.Test;

[Parallelizable(ParallelScope.All)]
[TestFixtureSource(nameof(Algorithms))]
public class TurboPForTests(TurboPForTests.Algorithm algorithm)
{
    [SuppressMessage("ReSharper", "MemberHidesStaticFromOuterClass")]
    public record Algorithm(string Name, CompressFunc Compress, DecompressFunc Decompress)
    {
        public override string ToString() => Name;
    }

    private static Algorithm[] Algorithms() =>
    [
        new("p4nd1*256v32", TurboPFor.p4nd1enc256v32, TurboPFor.p4nd1dec256v32),
        new("p4nd1*128v32", TurboPFor.p4nd1enc128v32, TurboPFor.p4nd1dec128v32),

        // Mixed version - don't work
        //new("p4nd1enc256v32 / p4nd1dec128v32", TurboPFor.p4nd1enc256v32, TurboPFor.p4nd1dec128v32),
        //new("p4nd1enc128v32 / p4nd1dec256v32", TurboPFor.p4nd1enc128v32, TurboPFor.p4nd1dec256v32)
    ];

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

    [Test]
    [Combinatorial]
    public void Increasing_Consecutive(
        [ValueSource(nameof(Lengths))] int length
    )
    {
        var values = Enumerable.Range(0, length).ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Increasing_Consecutive_Negative(
        [ValueSource(nameof(Lengths))] int length
    )
    {
        var values = Enumerable.Range(0, length).Reverse().Select(x => -x).ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Increasing_Random(
        [Values(42, 4242, 424242)] int seed,
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new Random(seed), length, maxDelta).ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Increasing_Random_Negative(
        [Values(42, 4242, 424242)] int seed,
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new Random(seed), length, maxDelta).Reverse().Select(x => -x).ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Decreasing_Consecutive(
        [ValueSource(nameof(Lengths))] int length
    )
    {
        var values = Enumerable.Range(0, length).Reverse().ToArray();
        Verify(values);
    }

    [Test]
    [Combinatorial]
    public void Decreasing_Random(
        [Values(42, 4242, 424242)] int seed,
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new(42), length, maxDelta).Reverse().ToArray();
        Verify(values);
    }

    private static IEnumerable<int> RandomIncreasingRange(Random random, int length, int maxDelta)
    {
        var value = 0;
        for (var i = 0; i < length; i++)
        {
            value += random.Next(maxDelta);
            yield return value;
        }
    }

    public delegate nuint CompressFunc(ReadOnlySpan<int> @in, nuint n, Span<byte> @out);

    public delegate nuint DecompressFunc(ReadOnlySpan<byte> @in, nuint n, Span<int> @out);

    private void Verify(int[] values)
    {
        if (!TurboPFor.Supports256Blocks && algorithm.Name.Contains("256"))
            Assert.Ignore("256 blocks are not supported on this platform.");

        var compressed = Compress(values, algorithm.Compress);
        var decompressed = Decompress(compressed, values.Length, algorithm.Decompress);

        Assert.That(decompressed, Is.EqualTo(values));
    }

    private static byte[] Compress(int[] values, CompressFunc compressFunc)
    {
        var buffer = new byte[values.Length * sizeof(int) + 1024];

        var resultLength = (int)compressFunc(values, (nuint)values.Length, buffer);

        TestContext.Out.WriteLine($"Compressed: {resultLength} bytes");
        return buffer[..resultLength];
    }

    private static int[] Decompress(byte[] data, int count, DecompressFunc decompressFunc)
    {
        var buffer = new int[count + 1];
        for (var i = count; i < buffer.Length; i++)
            buffer[i] = -1;

        _ = decompressFunc(data, (nuint)count, buffer);

        for (var i = count; i < buffer.Length; i++) Assert.That(buffer[i], Is.EqualTo(-1));

        return buffer[..count];
    }
}
