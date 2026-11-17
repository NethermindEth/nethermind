// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using static Nethermind.Db.LogIndex.LogIndexStorage;

namespace Nethermind.Db.Test.LogIndex;

[Parallelizable(ParallelScope.All)]
public class LogIndexCompressionTests
{
    // With 2^k - 1 values the old encoder buffer (data length + 4 bytes) was an exact pool size, with no spare room.
    private static readonly int[] Counts = [1, 2, 15, 63, 127, 255, 1_023, 2_047];

    public static IEnumerable<string> Algorithms() => CompressionAlgorithm.Supported.Keys;

    [Test]
    public void Values_that_do_not_compress_round_trip(
        [ValueSource(nameof(Algorithms))] string algorithmName,
        [ValueSource(nameof(Counts))] int count)
    {
        CompressionAlgorithm algorithm = CompressionAlgorithm.Supported[algorithmName];
        int[] blocks = Incompressible(count, seed: count);

        byte[] compressed = CompressDbValue(algorithm, [], MemoryMarshal.AsBytes(blocks.AsSpan()));
        int[] decompressed = new int[count + 16];
        DecompressDbValue(algorithm, compressed, decompressed);

        Assert.That(decompressed[..count], Is.EqualTo(blocks));
    }

    [Test]
    public void Decompressing_needs_room_for_the_values_the_decoder_writes_after_the_last_one(
        [ValueSource(nameof(Algorithms))] string algorithmName)
    {
        CompressionAlgorithm algorithm = CompressionAlgorithm.Supported[algorithmName];
        int[] blocks = Enumerable.Range(1, 100).ToArray();
        byte[] compressed = CompressDbValue(algorithm, [], MemoryMarshal.AsBytes(blocks.AsSpan()));

        Assert.That(() => DecompressDbValue(algorithm, compressed, new int[blocks.Length + 1]), Throws.ArgumentException);
    }

    [TestCase(1, 6)]
    [TestCase(128, 514)]
    [TestCase(129, 519)]
    [TestCase(256, 1_027)]
    public void Max_compressed_length_is_the_codec_bound_plus_one_and_the_overwrite(int count, int expected) =>
        Assert.That(CompressionAlgorithm.GetMaxCompressedLength(count), Is.EqualTo(expected + CompressionAlgorithm.CompressedDataOverwrite));

    [Test]
    [Platform("Linux,MacOsX")]
    public void Codec_stays_inside_the_value_overrun_and_the_input_overread([ValueSource(nameof(Algorithms))] string algorithmName)
    {
        CompressionAlgorithm algorithm = CompressionAlgorithm.Supported[algorithmName];
        for (int count = 1; count <= 600; count++)
        {
            foreach (int[] blocks in Patterns(count))
            {
                using GuardedMemory encoded = new(CompressionAlgorithm.GetMaxCompressedLength(count));
                int length = (int)algorithm.Compress(blocks, (nuint)count, encoded.Bytes);

                using GuardedMemory input = new(length + CompressionAlgorithm.CompressedDataOverread);
                encoded.Bytes[..length].CopyTo(input.Bytes);
                using GuardedMemory output = new((count + CompressionAlgorithm.DecompressionOverrun) * sizeof(int));
                Span<int> decompressed = MemoryMarshal.Cast<byte, int>(output.Bytes);
                algorithm.Decompress(input.Bytes, (nuint)count, decompressed);

                Assert.That(decompressed[..count].ToArray(), Is.EqualTo(blocks), $"{count} values");
            }
        }
    }

    [Test]
    [Platform("Linux,MacOsX")]
    public void Encoder_stays_inside_the_max_compressed_length([ValueSource(nameof(Algorithms))] string algorithmName)
    {
        CompressionAlgorithm algorithm = CompressionAlgorithm.Supported[algorithmName];
        for (int count = 1; count <= 600; count++)
        {
            foreach (int[] blocks in Patterns(count))
            {
                using GuardedMemory output = new(CompressionAlgorithm.GetMaxCompressedLength(count));
                int length = (int)algorithm.Compress(blocks, (nuint)count, output.Bytes);

                Assert.That(length, Is.LessThanOrEqualTo(output.Bytes.Length - CompressionAlgorithm.CompressedDataOverwrite));
            }
        }
    }

    [Test]
    [Platform("Linux,MacOsX")]
    public void Decoder_stays_inside_the_value_and_the_buffer([ValueSource(nameof(Algorithms))] string algorithmName)
    {
        CompressionAlgorithm algorithm = CompressionAlgorithm.Supported[algorithmName];
        for (int count = 1; count <= 600; count++)
        {
            foreach (int[] blocks in Patterns(count))
            {
                byte[] compressed = CompressDbValue(algorithm, [], MemoryMarshal.AsBytes(blocks.AsSpan()));
                using GuardedMemory input = new(compressed.Length);
                compressed.CopyTo(input.Bytes);
                using GuardedMemory output = new((count + CompressionAlgorithm.DecompressionOverrun) * sizeof(int));
                Span<int> decompressed = MemoryMarshal.Cast<byte, int>(output.Bytes);

                DecompressDbValue(algorithm, input.Bytes, decompressed);

                Assert.That(decompressed[..count].ToArray(), Is.EqualTo(blocks), $"{count} values");
            }
        }
    }

    private static IEnumerable<int[]> Patterns(int count)
    {
        Random random = new(count);
        int[] gaps = new int[count];
        int[] exceptions = new int[count];
        int gapBlock = 300_000_000;
        int exceptionBlock = 20_000_000;
        for (int i = 0; i < count; i++)
        {
            gapBlock += 1 + random.Next(256);
            gaps[i] = gapBlock;
            exceptionBlock += random.Next(64) == 0 ? 1 + random.Next(1 << 24) : 1 + random.Next(4);
            exceptions[i] = exceptionBlock;
        }

        yield return gaps;
        yield return exceptions;
        yield return Incompressible(count, seed: -count);

        if (count == 3) yield return [20_000_002, 21_341_774, 21_341_775];
        if (count == 3) yield return [1 << 28, (1 << 28) + 1, int.MaxValue];
        if (count == 5) yield return [20_000_002, 20_000_479, 20_000_482, 20_000_486, 20_000_490];
        if (count == 9) yield return [20_000_002, 20_000_003, 20_000_004, 20_000_005, 20_000_006, 20_000_007, 20_000_008, 20_000_009, 20_000_210];
    }

    private static int[] Incompressible(int count, int seed)
    {
        Random random = new(seed);
        int[] blocks = new int[count];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = random.Next(int.MinValue, int.MaxValue);
        // A negative first value would read as the compression marker; 2^28 or more takes TurboPFor's longest first-value form.
        blocks[0] = random.Next(1 << 28, int.MaxValue);
        return blocks;
    }

    /// <summary>
    /// Native memory that ends right before an inaccessible page, so an access past the end stops the process.
    /// </summary>
    private sealed unsafe class GuardedMemory : IDisposable
    {
        private const int ProtNone = 0;
        private const int ProtReadWrite = 3;
        private const int MapPrivate = 2;

        private static readonly int MapAnonymous = OperatingSystem.IsMacOS() ? 0x1000 : 0x20;
        private static readonly nint LibC = NativeLibrary.GetMainProgramHandle();
        private static readonly delegate* unmanaged<nint, nuint, int, int, int, nint, nint> Mmap =
            (delegate* unmanaged<nint, nuint, int, int, int, nint, nint>)NativeLibrary.GetExport(LibC, "mmap");
        private static readonly delegate* unmanaged<nint, nuint, int, int> Mprotect =
            (delegate* unmanaged<nint, nuint, int, int>)NativeLibrary.GetExport(LibC, "mprotect");
        private static readonly delegate* unmanaged<nint, nuint, int> Munmap =
            (delegate* unmanaged<nint, nuint, int>)NativeLibrary.GetExport(LibC, "munmap");

        private readonly nint _region;
        private readonly nuint _regionSize;
        private readonly nint _start;
        private readonly int _length;

        public GuardedMemory(int length)
        {
            nuint page = (nuint)Environment.SystemPageSize;
            _length = length;
            _regionSize = ((nuint)length + page - 1) / page * page + page;
            _region = Mmap(0, _regionSize, ProtReadWrite, MapPrivate | MapAnonymous, -1, 0);
            if (_region == -1) throw new InvalidOperationException("mmap failed");
            nint guard = _region + (nint)(_regionSize - page);
            if (Mprotect(guard, page, ProtNone) != 0)
            {
                Munmap(_region, _regionSize);
                throw new InvalidOperationException("mprotect failed");
            }
            _start = guard - length;
        }

        public Span<byte> Bytes => new((void*)_start, _length);

        public void Dispose() => Munmap(_region, _regionSize);
    }
}
