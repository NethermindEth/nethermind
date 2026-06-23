// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Threading;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Snappier;
using ZstdSharp;

namespace Nethermind.Db.Rocks;

internal sealed class MdbxValueCompression(bool enabled, int minValueLength = MdbxValueCompression.DefaultMinValueLength) : IDisposable
{
    private const int LegacyHeaderLength = 9;
    private const int HeaderLength = 10;
    private const int RawEscapedHeaderLength = 5;
    internal const int DefaultMinValueLength = 256;
    private const int MaxDecodedLength = 1024 * 1024 * 1024;
    private const byte RawEscapedVersion = 0;
    private const byte LegacySnappyVersion = 1;
    private const byte Version = 2;
    private const byte SnappyCodec = 1;
    private const byte ZstdCodec = 2;
    private const int ZstdCompressionLevel = 1;
    private const string EnabledVariable = "NETHERMIND_MDBX_COMPRESSION";
    private const string MinValueLengthVariable = "NETHERMIND_MDBX_COMPRESSION_MIN_BYTES";
    private const string StateMinValueLengthVariable = "NETHERMIND_MDBX_STATE_COMPRESSION_MIN_BYTES";

    private readonly ThreadLocal<Compressor> _zstdCompressors = new(() => new Compressor(ZstdCompressionLevel), trackAllValues: true);
    private readonly ThreadLocal<Decompressor> _zstdDecompressors = new(() => new Decompressor(), trackAllValues: true);
    private readonly ThreadLocal<byte[]> _zstdEncodeBuffers = new(() => [], trackAllValues: false);
    private bool _disposed;

    private static ReadOnlySpan<byte> Magic => [0xFF, (byte)'N', (byte)'M', (byte)'X'];

    public bool Enabled { get; } = enabled;

    public int MinValueLength { get; } = Math.Max(0, minValueLength);

    public static MdbxValueCompression Create(IRocksDbConfig rocksDbConfig, ILogger logger, string path)
    {
        bool configured = IsCompressionConfigured(rocksDbConfig);
        bool enabled = configured;
        string? overrideValue = Environment.GetEnvironmentVariable(EnabledVariable);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            if (MdbxTuningOptions.TryParseBool(overrideValue, out bool parsed))
            {
                enabled = parsed;
            }
            else if (logger.IsWarn)
            {
                logger.Warn($"Ignoring invalid {EnabledVariable} value '{overrideValue}'. Use true/false.");
            }
        }

        int minValueLength = IsStateDbPath(path)
            ? ReadMinValueLength(StateMinValueLengthVariable, int.MaxValue, logger)
            : ReadMinValueLength(MinValueLengthVariable, DefaultMinValueLength, logger);
        if (logger.IsInfo)
        {
            logger.Info($"MDBX value compression for {path}: {(enabled ? "enabled" : "disabled")} minBytes={minValueLength}");
        }

        return new MdbxValueCompression(enabled, minValueLength);
    }

    public bool TryEncode(ReadOnlySpan<byte> value, out byte[]? buffer, out int length) =>
        TryEncode(value, out buffer, out length, out _);

    public bool TryEncode(ReadOnlySpan<byte> value, out byte[]? buffer, out int length, out MdbxValueEncodingKind encodingKind)
    {
        buffer = null;
        length = value.Length;
        encodingKind = MdbxValueEncodingKind.Raw;
        if (!Enabled || value.Length < MinValueLength)
        {
            return TryEscapeRaw(value, MdbxValueEncodingKind.Raw, out buffer, out length, out encodingKind);
        }

        Compressor compressor = _zstdCompressors.Value!;
        int maxCompressedLength = Compressor.GetCompressBound(value.Length);
        byte[] candidate = GetZstdEncodeBuffer(HeaderLength + maxCompressedLength);
        Magic.CopyTo(candidate);
        candidate[4] = Version;
        candidate[5] = ZstdCodec;
        BinaryPrimitives.WriteInt32LittleEndian(candidate.AsSpan(6), value.Length);

        if (!compressor.TryWrap(value, candidate.AsSpan(HeaderLength), out int compressedLength))
        {
            encodingKind = MdbxValueEncodingKind.CompressionRejected;
            return TryEscapeRaw(value, MdbxValueEncodingKind.CompressionRejected, out buffer, out length, out encodingKind);
        }

        int storedLength = HeaderLength + compressedLength;
        if (storedLength >= value.Length)
        {
            encodingKind = MdbxValueEncodingKind.CompressionRejected;
            return TryEscapeRaw(value, MdbxValueEncodingKind.CompressionRejected, out buffer, out length, out encodingKind);
        }

        buffer = candidate;
        length = storedLength;
        encodingKind = MdbxValueEncodingKind.Compressed;
        return true;
    }

    public byte[] Decode(ReadOnlySpan<byte> stored)
    {
        if (!LooksCompressed(stored))
        {
            return CopyRaw(stored);
        }

        byte version = stored[4];
        if (version == RawEscapedVersion)
        {
            return CopyRaw(stored[RawEscapedHeaderLength..]);
        }

        int expectedLength = version == LegacySnappyVersion
            ? BinaryPrimitives.ReadInt32LittleEndian(stored.Slice(5, sizeof(int)))
            : BinaryPrimitives.ReadInt32LittleEndian(stored.Slice(6, sizeof(int)));
        if (expectedLength < 0 || expectedLength > MaxDecodedLength)
        {
            return CopyRaw(stored);
        }

        try
        {
            return version switch
            {
                LegacySnappyVersion => DecodeSnappy(stored[LegacyHeaderLength..], expectedLength, stored),
                Version when stored[5] == SnappyCodec => DecodeSnappy(stored[HeaderLength..], expectedLength, stored),
                Version when stored[5] == ZstdCodec => DecodeZstd(stored[HeaderLength..], expectedLength, stored),
                _ => CopyRaw(stored),
            };
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException or ZstdException)
        {
            return CopyRaw(stored);
        }
    }

    private static bool IsCompressionConfigured(IRocksDbConfig rocksDbConfig)
    {
        string normalized = DbOnTheRocks.NormalizeRocksDbOptions(rocksDbConfig.RocksDbOptions + rocksDbConfig.AdditionalRocksDbOptions);
        if (!DbOnTheRocks.ExtractOptions(normalized).TryGetValue("compression", out string? compression))
        {
            return true;
        }

        return !compression.Equals("kNoCompression", StringComparison.OrdinalIgnoreCase);
    }

    private static int ReadMinValueLength(string variableName, int fallback, ILogger logger)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0)
        {
            return parsed;
        }

        if (logger.IsWarn)
        {
            logger.Warn($"Ignoring invalid {variableName} value '{value}'. Use a non-negative integer.");
        }

        return fallback;
    }

    private static bool IsStateDbPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/state/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksCompressed(ReadOnlySpan<byte> stored)
    {
        if (stored.Length <= RawEscapedHeaderLength || !stored[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        return stored[4] switch
        {
            RawEscapedVersion => true,
            LegacySnappyVersion => true,
            Version => stored.Length > HeaderLength,
            _ => false,
        };
    }

    private static bool TryEscapeRaw(ReadOnlySpan<byte> value, MdbxValueEncodingKind fallbackKind, out byte[]? buffer, out int length, out MdbxValueEncodingKind encodingKind)
    {
        if (!value.StartsWith(Magic))
        {
            buffer = null;
            length = value.Length;
            encodingKind = fallbackKind;
            return false;
        }

        buffer = GC.AllocateUninitializedArray<byte>(RawEscapedHeaderLength + value.Length);
        Magic.CopyTo(buffer);
        buffer[4] = RawEscapedVersion;
        value.CopyTo(buffer.AsSpan(RawEscapedHeaderLength));
        length = buffer.Length;
        encodingKind = MdbxValueEncodingKind.EscapedRaw;
        return true;
    }

    private byte[] GetZstdEncodeBuffer(int length)
    {
        byte[] buffer = _zstdEncodeBuffers.Value!;
        if (buffer.Length < length)
        {
            buffer = GC.AllocateUninitializedArray<byte>(length);
            _zstdEncodeBuffers.Value = buffer;
        }

        return buffer;
    }

    private static byte[] DecodeSnappy(ReadOnlySpan<byte> compressed, int expectedLength, ReadOnlySpan<byte> stored)
    {
        if (Snappy.GetUncompressedLength(compressed) != expectedLength)
        {
            return CopyRaw(stored);
        }

        byte[] decoded = GC.AllocateUninitializedArray<byte>(expectedLength);
        return Snappy.TryDecompress(compressed, decoded, out int written) && written == expectedLength
            ? decoded
            : CopyRaw(stored);
    }

    private byte[] DecodeZstd(ReadOnlySpan<byte> compressed, int expectedLength, ReadOnlySpan<byte> stored)
    {
        Decompressor decompressor = _zstdDecompressors.Value!;
        byte[] decoded = GC.AllocateUninitializedArray<byte>(expectedLength);
        return decompressor.TryUnwrap(compressed, decoded, out int written) && written == expectedLength
            ? decoded
            : CopyRaw(stored);
    }

    private static byte[] CopyRaw(ReadOnlySpan<byte> stored)
    {
        if (stored.Length == 0)
        {
            return [];
        }

        byte[] copy = GC.AllocateUninitializedArray<byte>(stored.Length);
        stored.CopyTo(copy);
        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _zstdEncodeBuffers.Dispose();
        DisposeThreadLocalValues(_zstdCompressors);
        DisposeThreadLocalValues(_zstdDecompressors);
    }

    private static void DisposeThreadLocalValues<T>(ThreadLocal<T> threadLocal)
        where T : IDisposable
    {
        foreach (T value in threadLocal.Values)
        {
            value.Dispose();
        }

        threadLocal.Dispose();
    }
}

internal enum MdbxValueEncodingKind
{
    Raw,
    CompressionRejected,
    Compressed,
    EscapedRaw,
}
