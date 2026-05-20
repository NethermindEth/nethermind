// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;

namespace Nethermind.Benchmarks.Core;

[MemoryDiagnoser]
public class ByteArrayConverterWriteBenchmarks
{
    private const ushort HexPrefix = 0x7830;

    private byte[] _bytes = null!;
    private ArrayBufferWriter<byte> _buffer = null!;
    private Utf8JsonWriter _writer = null!;

    [Params(32, 127, 512, 1022, 1023)]
    public int ByteLength { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _bytes = GC.AllocateUninitializedArray<byte>(ByteLength);
        for (int i = 0; i < _bytes.Length; i++)
        {
            _bytes[i] = (byte)(i + 1);
        }

        _buffer = new ArrayBufferWriter<byte>(ByteLength * 2 + 64);
        _writer = new Utf8JsonWriter(_buffer);
    }

    [GlobalCleanup]
    public void Cleanup() => _writer.Dispose();

    [Benchmark(Baseline = true)]
    public int Inline256OrPool()
    {
        ResetWriter();
        ConvertWithInline256(_writer, _bytes);
        _writer.Flush();
        return _buffer.WrittenCount;
    }

    [Benchmark]
    public int Inline2048OrPool()
    {
        ResetWriter();
        ConvertWithInline2048(_writer, _bytes);
        _writer.Flush();
        return _buffer.WrittenCount;
    }

    [Benchmark]
    public int Production()
    {
        ResetWriter();
        ByteArrayConverter.Convert(_writer, _bytes, skipLeadingZeros: false);
        _writer.Flush();
        return _buffer.WrittenCount;
    }

    private void ResetWriter()
    {
        _writer.Reset();
        _buffer.Clear();
    }

    [SkipLocalsInit]
    private static void ConvertWithInline256(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
    {
        int rawLength = bytes.Length * 2 + 4;
        Unsafe.SkipInit(out HexBuffer256 buffer);
        if (rawLength <= 256)
        {
            WriteRawHexString(
                writer,
                bytes,
                MemoryMarshal.CreateSpan(ref Unsafe.As<HexBuffer256, byte>(ref buffer), rawLength));
            return;
        }

        byte[] array = ArrayPool<byte>.Shared.Rent(rawLength);
        try
        {
            WriteRawHexString(writer, bytes, array.AsSpan(0, rawLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    [SkipLocalsInit]
    private static void ConvertWithInline2048(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes)
    {
        int rawLength = bytes.Length * 2 + 4;
        Unsafe.SkipInit(out HexBuffer2048 buffer);
        if (rawLength <= 2048)
        {
            WriteRawHexString(
                writer,
                bytes,
                MemoryMarshal.CreateSpan(ref Unsafe.As<HexBuffer2048, byte>(ref buffer), rawLength));
            return;
        }

        byte[] array = ArrayPool<byte>.Shared.Rent(rawLength);
        try
        {
            WriteRawHexString(writer, bytes, array.AsSpan(0, rawLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(array);
        }
    }

    private static void WriteRawHexString(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes, Span<byte> hex)
    {
        ref byte hexRef = ref MemoryMarshal.GetReference(hex);
        hexRef = (byte)'"';
        Unsafe.As<byte, ushort>(ref Unsafe.Add(ref hexRef, 1)) = HexPrefix;
        Unsafe.Add(ref hexRef, hex.Length - 1) = (byte)'"';
        bytes.OutputBytesToByteHex(
            MemoryMarshal.CreateSpan(ref Unsafe.Add(ref hexRef, 3), hex.Length - 4),
            extraNibble: false);
        writer.WriteRawValue(hex, skipInputValidation: true);
    }

    [InlineArray(256)]
    private struct HexBuffer256
    {
        private byte _element0;
    }

    [InlineArray(2048)]
    private struct HexBuffer2048
    {
        private byte _element0;
    }
}
