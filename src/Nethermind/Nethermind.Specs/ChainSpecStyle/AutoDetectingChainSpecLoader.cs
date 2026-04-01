// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Text.Json;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// A chain spec loader that auto-detects the format of the input file and delegates
/// to either the regular ChainSpecLoader or the Geth-style GethGenesisLoader.
/// </summary>
public class AutoDetectingChainSpecLoader(IJsonSerializer serializer, ILogManager logManager) : IChainSpecLoader
{
    private readonly ILogger _logger = logManager.GetClassLogger<AutoDetectingChainSpecLoader>();
    private readonly ChainSpecLoader _parityLoader = new(serializer, logManager);
    private readonly GethGenesisLoader _gethLoader = new(serializer);

    public ChainSpec Load(Stream streamData)
    {
        static Stream RewindStream(Stream streamData, long startPosition)
        {
            streamData.Position = startPosition;
            return streamData;
        }

        Span<byte> header = stackalloc byte[256];
        long startPosition = streamData.CanSeek ? streamData.Position : -1;
        int headerLength = streamData.ReadAtLeast(header, 1, throwOnEndOfStream: false);
        header = header[..headerLength];
        GenesisFormat format = DetectFormat(header);

        Stream stream = streamData.CanSeek
            ? RewindStream(streamData, startPosition)
            : new PrefixedStream(header.ToArray(), streamData);

        return format switch
        {
            GenesisFormat.Geth => _gethLoader.Load(stream),
            _ => _parityLoader.Load(stream),
        };
    }

    /// <summary>
    /// Geth genesis always starts with <c>"config"</c> as the first property; parity chainspecs never do.
    /// </summary>
    private GenesisFormat DetectFormat(ReadOnlySpan<byte> data)
    {
        try
        {
            Utf8JsonReader reader = new(data, new JsonReaderOptions { AllowTrailingCommas = true });

            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.PropertyName && !reader.ValueTextEquals("$schema"u8))
                    return reader.ValueTextEquals("config"u8) ? GenesisFormat.Geth : GenesisFormat.Parity;
            }
        }
        catch (JsonException e)
        {
            if (_logger.IsError) _logger.Error("Error parsing specification", e);
        }

        if (_logger.IsWarn) _logger.Warn("Failed to detect genesis file format, assuming Parity-like style.");
        return GenesisFormat.Unknown;
    }

    /// <summary>
    /// A read-only stream that replays a prefix byte buffer before delegating to the inner stream.
    /// Avoids copying the entire inner stream to a MemoryStream just for format detection.
    /// </summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) : Stream
    {
        private int _prefixPosition;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixPosition < prefix.Length)
            {
                int toCopy = Math.Min(count, prefix.Length - _prefixPosition);
                prefix.Span.Slice(_prefixPosition, toCopy).CopyTo(buffer.AsSpan(offset, toCopy));
                _prefixPosition += toCopy;
                return toCopy;
            }

            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (_prefixPosition < prefix.Length)
            {
                int toCopy = Math.Min(buffer.Length, prefix.Length - _prefixPosition);
                prefix.Span.Slice(_prefixPosition, toCopy).CopyTo(buffer);
                _prefixPosition += toCopy;
                return toCopy;
            }

            return inner.Read(buffer);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length + inner.Length;
        public override long Position { get => _prefixPosition + inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private enum GenesisFormat
    {
        Unknown,
        Parity,
        Geth
    }
}
