// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
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
    private const int DetectionBufferSize = 4096;

    private readonly ILogger _logger = logManager.GetClassLogger<AutoDetectingChainSpecLoader>();
    private readonly ChainSpecLoader _parityLoader = new(serializer, logManager);
    private readonly GethGenesisLoader _gethLoader = new(serializer);

    public ChainSpec Load(Stream streamData)
    {
        if (!streamData.CanSeek)
        {
            using MemoryStream bufferedStream = new();
            streamData.CopyTo(bufferedStream);
            bufferedStream.Position = 0;
            return LoadSeekable(bufferedStream);
        }

        return LoadSeekable(streamData);
    }

    private ChainSpec LoadSeekable(Stream streamData)
    {
        long startPosition = streamData.Position;
        GenesisFormat format = DetectFormat(streamData);
        streamData.Position = startPosition;

        return format switch
        {
            GenesisFormat.Geth => _gethLoader.Load(streamData),
            _ => _parityLoader.Load(streamData),
        };
    }

    /// <summary>
    /// Geth genesis contains a top-level <c>"config"</c> property; parity chainspecs do not.
    /// </summary>
    private GenesisFormat DetectFormat(Stream streamData)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(DetectionBufferSize);

        try
        {
            int bytesInBuffer = 0;
            JsonReaderState readerState = new(new JsonReaderOptions { AllowTrailingCommas = true });

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
                        return GenesisFormat.Parity;
                    }

                    if (reader.TokenType is not JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    {
                        continue;
                    }

                    if (reader.ValueTextEquals("config"u8))
                    {
                        return GenesisFormat.Geth;
                    }

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

    private enum GenesisFormat
    {
        Unknown,
        Parity,
        Geth
    }
}
