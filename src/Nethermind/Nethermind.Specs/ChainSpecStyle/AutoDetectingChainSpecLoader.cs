// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.IO;
using System.Text.Json;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// A chain spec loader that auto-detects the format of the input file and delegates
/// to either the regular ChainSpecLoader or the Geth-style GethGenesisLoader.
/// </summary>
public class AutoDetectingChainSpecLoader(IJsonSerializer serializer, ILogManager logManager) : IChainSpecLoader
{
    private const int DetectionBufferSize = 4096;

    private readonly ILogger _logger = logManager.GetClassLogger<AutoDetectingChainSpecLoader>();
    private readonly ChainSpecLoader _parityLoader = new(serializer, logManager);
    private readonly GethGenesisLoader _gethLoader = new(serializer);

    public ChainSpec Load(Stream streamData)
    {
        if (!streamData.CanSeek)
        {
            using Stream bufferedStream = RecyclableStream.GetStream(nameof(AutoDetectingChainSpecLoader));
            GenesisFormat format;
            using (CapturingStream detectionStream = new(streamData, bufferedStream))
            {
                format = DetectFormat(detectionStream);
            }

            bufferedStream.Position = 0;
            using PrefixedStream replayStream = new(bufferedStream, streamData);
            return LoadDetected(format, replayStream);
        }

        return LoadSeekable(streamData);
    }

    private ChainSpec LoadSeekable(Stream streamData)
    {
        long startPosition = streamData.Position;
        GenesisFormat format = DetectFormat(streamData);
        streamData.Position = startPosition;

        return LoadDetected(format, streamData);
    }

    private ChainSpec LoadDetected(GenesisFormat format, Stream streamData) => format switch
    {
        GenesisFormat.Geth => _gethLoader.Load(streamData),
        _ => _parityLoader.Load(streamData),
    };

    /// <summary>
    /// Geth genesis contains a top-level <c>"config"</c> property, while Parity-style chainspecs
    /// are identified by their top-level <c>"engine"</c>, <c>"params"</c>, <c>"genesis"</c>, or
    /// <c>"accounts"</c> properties.
    /// </summary>
    private GenesisFormat DetectFormat(Stream streamData)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(DetectionBufferSize);

        try
        {
            int bytesInBuffer = 0;
            bool hasGethConfig = false;
            JsonReaderState readerState = new(new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                MaxDepth = EthereumJsonSerializer.DefaultMaxDepth,
            });

            while (true)
            {
                if (bytesInBuffer == buffer.Length)
                {
                    byte[] largerBuffer = ArrayPool<byte>.Shared.Rent(checked(buffer.Length * 2));
                    buffer.AsSpan(0, bytesInBuffer).CopyTo(largerBuffer);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = largerBuffer;
                }

                int bytesRead = streamData.Read(buffer.AsSpan(bytesInBuffer));
                bytesInBuffer += bytesRead;
                bool isFinalBlock = bytesRead == 0;

                Utf8JsonReader reader = new(buffer.AsSpan(0, bytesInBuffer), isFinalBlock, readerState);
                while (reader.Read())
                {
                    if (reader.TokenType is JsonTokenType.EndObject && reader.CurrentDepth == 0)
                    {
                        return hasGethConfig ? GenesisFormat.Geth : GenesisFormat.Parity;
                    }

                    if (reader.TokenType is not JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    {
                        continue;
                    }

                    if (reader.ValueTextEquals("engine"u8) ||
                        reader.ValueTextEquals("params"u8) ||
                        reader.ValueTextEquals("genesis"u8) ||
                        reader.ValueTextEquals("accounts"u8))
                    {
                        return GenesisFormat.Parity;
                    }

                    hasGethConfig |= reader.ValueTextEquals("config"u8);

                    if (!reader.Read() || !reader.TrySkip())
                    {
                        break;
                    }
                }

                readerState = reader.CurrentState;
                int bytesConsumed = checked((int)reader.BytesConsumed);
                if (bytesConsumed > 0)
                {
                    buffer.AsSpan(bytesConsumed, bytesInBuffer - bytesConsumed).CopyTo(buffer);
                    bytesInBuffer -= bytesConsumed;
                }

                if (isFinalBlock)
                {
                    break;
                }
            }
        }
        catch (JsonException e)
        {
            if (_logger.IsError) _logger.Error("Error parsing specification", e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (_logger.IsWarn) _logger.Warn("Failed to detect genesis file format, assuming Parity-like style.");
        return GenesisFormat.Unknown;
    }

    private sealed class CapturingStream(Stream inner, Stream capture) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = inner.Read(buffer, offset, count);
            if (bytesRead > 0)
            {
                capture.Write(buffer, offset, bytesRead);
            }

            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            int bytesRead = inner.Read(buffer);
            if (bytesRead > 0)
            {
                capture.Write(buffer[..bytesRead]);
            }

            return bytesRead;
        }

        public override int ReadByte()
        {
            int value = inner.ReadByte();
            if (value >= 0)
            {
                capture.WriteByte((byte)value);
            }

            return value;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PrefixedStream(Stream prefix, Stream inner) : Stream
    {
        private bool _prefixExhausted;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_prefixExhausted)
            {
                int bytesRead = prefix.Read(buffer, offset, count);
                if (bytesRead > 0)
                {
                    return bytesRead;
                }

                _prefixExhausted = true;
            }

            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (!_prefixExhausted)
            {
                int bytesRead = prefix.Read(buffer);
                if (bytesRead > 0)
                {
                    return bytesRead;
                }

                _prefixExhausted = true;
            }

            return inner.Read(buffer);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
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
