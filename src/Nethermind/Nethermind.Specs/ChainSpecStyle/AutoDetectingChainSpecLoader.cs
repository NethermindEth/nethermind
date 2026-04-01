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
        if (!streamData.CanSeek)
        {
            using MemoryStream buffer = new();
            streamData.CopyTo(buffer);
            buffer.Position = 0;
            return Load(buffer);
        }

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
    /// Geth genesis always starts with <c>"config"</c> as the first property; parity chainspecs never do.
    /// Reading just the first property name is enough to distinguish the two formats.
    /// </summary>
    private GenesisFormat DetectFormat(Stream stream)
    {
        try
        {
            Span<byte> buf = stackalloc byte[256];
            int bytesRead = stream.Read(buf);

            Utf8JsonReader reader = new(buf[..bytesRead], new JsonReaderOptions { AllowTrailingCommas = true });

            // Find the first non-metadata property name (skip $schema etc.)
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.PropertyName && !reader.ValueTextEquals("$schema"u8))
                {
                    return reader.ValueTextEquals("config"u8) ? GenesisFormat.Geth : GenesisFormat.Parity;
                }
            }
        }
        catch (JsonException e)
        {
            if (_logger.IsError) _logger.Error("Error parsing specification", e);
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
