// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace Nethermind.Db.Test;

// More for documenting how the library works and comparing compression sizes
public class TurboPForTests
{
    private static IEnumerable<int> Lengths()
    {
        yield return 1;
        yield return 10;

        for (var i = 32; i <= 1024; i <<= 1)
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

    [TestCaseSource(nameof(Lengths))]
    public unsafe void p4nd1enc256v32_Increasing_Consecutive(int length)
    {
        var values = Enumerable.Range(0, length).ToArray();
        var compressed = Compress(values, TurboPFor.p4nd1enc256v32);
        var decompressed = Decompress(compressed, values.Length, TurboPFor.p4nd1dec256v32);

        Assert.That(decompressed, Is.EquivalentTo(values));
    }

    [TestCaseSource(nameof(Lengths))]
    public unsafe void p4nd1enc128v32_Increasing_Consecutive(int length)
    {
        var values = Enumerable.Range(0, length).ToArray();
        var compressed = Compress(values, TurboPFor.p4nd1enc128v32);
        var decompressed = Decompress(compressed, values.Length, TurboPFor.p4nd1dec128v32);

        Assert.That(decompressed, Is.EquivalentTo(values));
    }

    [Combinatorial]
    public unsafe void p4nd1enc256v32_Increasing_Random(
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new(42), length, maxDelta).ToArray();
        var compressed = Compress(values, TurboPFor.p4nd1enc256v32);
        var decompressed = Decompress(compressed, values.Length, TurboPFor.p4nd1dec256v32);

        Assert.That(decompressed, Is.EquivalentTo(values));
    }

    [Combinatorial]
    public unsafe void p4nd1enc128v32_Increasing_Random(
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new(42), length, maxDelta).ToArray();
        var compressed = Compress(values, TurboPFor.p4nd1enc128v32);
        var decompressed = Decompress(compressed, values.Length, TurboPFor.p4nd1dec128v32);

        Assert.That(decompressed, Is.EquivalentTo(values));
    }

    [TestCaseSource(nameof(Lengths))]
    public unsafe void p4nd1enc256v32_Increasing_Consecutive_Negative(int length)
    {
        var values = Enumerable.Range(0, length).Reverse().Select(x => -x).ToArray();
        var compressed = Compress(values, TurboPFor.p4nd1enc256v32);
        var decompressed = Decompress(compressed, values.Length, TurboPFor.p4nd1dec256v32);

        Assert.That(decompressed, Is.EquivalentTo(values));
    }

    [TestCaseSource(nameof(Lengths))]
    public unsafe void p4nd1enc256v32_Decreasing_Consecutive(int length)
    {
        var values = Enumerable.Range(0, length).Reverse().ToArray();
        var compressed = Compress(values, TurboPFor.p4nd1enc256v32);
        var decompressed = Decompress(compressed, values.Length, TurboPFor.p4nd1dec256v32);

        Assert.That(decompressed, Is.EquivalentTo(values));
    }

    [Combinatorial]
    public unsafe void p4nd1enc256v32_Increasing_Random_Negative(
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new(42), length, maxDelta).Reverse().Select(x => -x).ToArray();
        var compressed = Compress(values, TurboPFor.p4nd1enc256v32);
        var decompressed = Decompress(compressed, values.Length, TurboPFor.p4nd1dec256v32);

        Assert.That(decompressed, Is.EquivalentTo(values));
    }

    [Combinatorial]
    public unsafe void p4nd1enc256v32_Decreasing_Random(
        [ValueSource(nameof(Lengths))] int length,
        [ValueSource(nameof(Deltas))] int maxDelta
    )
    {
        var values = RandomIncreasingRange(new(42), length, maxDelta).Reverse().ToArray();
        var compressed = Compress(values, TurboPFor.p4nd1enc256v32);
        var decompressed = Decompress(compressed, values.Length, TurboPFor.p4nd1dec256v32);

        Assert.That(decompressed, Is.EquivalentTo(values));
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

    private unsafe delegate nuint CompressFunc(int* @in, nuint n, byte* @out);

    private unsafe delegate nuint DecompressFunc(byte* @in, nuint n, int* @out);

    private unsafe delegate byte* CompressBlockFunc(int* @in, int n, byte* @out, int start);

    private unsafe delegate byte* DecompressBlockFunc(byte* @in, int n, int* @out, int start);

    private static unsafe byte[] Compress(int[] values, CompressBlockFunc compressFunc, int deltaStart = 0)
    {
        var buffer = new byte[values.Length * sizeof(int) + 1024];

        int resultLength;
        fixed (int* inputPtr = values)
        fixed (byte* resultPtr = buffer)
        {
            var endPtr = compressFunc(inputPtr, values.Length, resultPtr, deltaStart);
            resultLength = (int)(endPtr - (long)resultPtr);
        }

        //TestContext.Out.WriteLine($"Compressed: {resultLength} bytes");
        return buffer[..resultLength];
    }

    private static unsafe int[] Decompress(byte[] data, int count, DecompressBlockFunc decompressFunc, int deltaStart = 0)
    {
        var buffer = new int[count + 1024];

        fixed (byte* inputPtr = data)
        fixed (int* resultPtr = buffer)
        {
            var endPtr = decompressFunc(inputPtr, count, resultPtr, deltaStart);
        }

        return buffer[..count];
    }

    private static unsafe byte[] Compress(int[] values, CompressFunc compressFunc)
    {
        var buffer = new byte[values.Length * sizeof(int) + 1024];

        int resultLength;
        fixed (int* inputPtr = values)
        fixed (byte* resultPtr = buffer)
        {
            resultLength = (int)compressFunc(inputPtr, (nuint)values.Length, resultPtr);
        }

        //TestContext.Out.WriteLine($"Compressed: {resultLength} bytes");
        return buffer[..resultLength];
    }

    private static unsafe int[] Decompress(byte[] data, int count, DecompressFunc decompressFunc)
    {
        //var buffer = new int[count];

        var buffer = new int[count * 2];
        for (var i = count; i < buffer.Length; i++)
            buffer[i] = -1;

        fixed (byte* inputPtr = data)
        fixed (int* resultPtr = buffer)
        {
            _ = decompressFunc(inputPtr, (nuint)count, resultPtr);
        }

        for (var i = count; i < buffer.Length; i++) Assert.That(buffer[i], Is.EqualTo(-1));

        return buffer[..count];
    }
}
