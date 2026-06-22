// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Snappier;

namespace Nethermind.Db.Rocks;

internal sealed class MdbxValueCompression(bool enabled)
{
    private const int HeaderLength = 9;
    private const int MinValueLength = 64;
    private const int MaxDecodedLength = 1024 * 1024 * 1024;
    private const byte Version = 1;
    private const string EnabledVariable = "NETHERMIND_MDBX_COMPRESSION";

    private static ReadOnlySpan<byte> Magic => [0xFF, (byte)'N', (byte)'M', (byte)'X'];

    public bool Enabled { get; } = enabled;

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

        if (logger.IsInfo)
        {
            logger.Info($"MDBX value compression for {path}: {(enabled ? "enabled" : "disabled")}");
        }

        return new MdbxValueCompression(enabled);
    }

    public bool TryEncode(ReadOnlySpan<byte> value, out byte[]? buffer, out int length)
    {
        buffer = null;
        length = value.Length;
        if (!Enabled || value.Length < MinValueLength)
        {
            return false;
        }

        int maxCompressedLength = Snappy.GetMaxCompressedLength(value.Length);
        byte[] candidate = GC.AllocateUninitializedArray<byte>(HeaderLength + maxCompressedLength);
        Magic.CopyTo(candidate);
        candidate[4] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(candidate.AsSpan(5), value.Length);

        int compressedLength = Snappy.Compress(value, candidate.AsSpan(HeaderLength));
        int storedLength = HeaderLength + compressedLength;
        if (storedLength >= value.Length)
        {
            return false;
        }

        buffer = candidate;
        length = storedLength;
        return true;
    }

    public byte[] Decode(ReadOnlySpan<byte> stored)
    {
        if (!LooksCompressed(stored))
        {
            return CopyRaw(stored);
        }

        int expectedLength = BinaryPrimitives.ReadInt32LittleEndian(stored.Slice(5, sizeof(int)));
        if (expectedLength < 0 || expectedLength > MaxDecodedLength)
        {
            return CopyRaw(stored);
        }

        ReadOnlySpan<byte> compressed = stored[HeaderLength..];
        try
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
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or ArgumentException)
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

    private static bool LooksCompressed(ReadOnlySpan<byte> stored) =>
        stored.Length > HeaderLength &&
        stored[4] == Version &&
        stored[..Magic.Length].SequenceEqual(Magic);

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
}
