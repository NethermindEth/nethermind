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
        try
        {
            using JsonDocument document = JsonDocument.Parse(streamData, new JsonDocumentOptions { AllowTrailingCommas = true });
            return document.RootElement.ValueKind is JsonValueKind.Object && document.RootElement.TryGetProperty("config", out _)
                ? GenesisFormat.Geth
                : GenesisFormat.Parity;
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
