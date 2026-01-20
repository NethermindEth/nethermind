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
    private readonly ChainSpecLoader _parityLoader = new(serializer);
    private readonly GethGenesisLoader _gethLoader = new(serializer);

    public ChainSpec Load(Stream streamData)
    {
        using MemoryStream memoryStream = new();
        streamData.CopyTo(memoryStream);
        memoryStream.Position = 0;

        GenesisFormat format = DetectFormat(memoryStream);
        memoryStream.Position = 0;

        return format switch
        {
            GenesisFormat.Geth => _gethLoader.Load(memoryStream),
            _ => _parityLoader.Load(memoryStream),
        };
    }

    private GenesisFormat DetectFormat(Stream stream)
    {
        GenesisFormat result = GenesisFormat.Unknown;

        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("config", out JsonElement config) ||
                root.TryGetProperty("alloc", out _))
            {
                result = GenesisFormat.Geth;
            }

            if (root.TryGetProperty("engine", out _) ||
                root.TryGetProperty("params", out _) ||
                root.TryGetProperty("accounts", out _))
            {
                result = GenesisFormat.Parity;
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error("Error parsing specification", e);
        }

        if (result is GenesisFormat.Unknown)
        {
            if (_logger.IsWarn) _logger.Warn("Failed to parse genesis file for format detection, assuming Parity-like style.");
        }

        return result;
    }

    private enum GenesisFormat
    {
        Unknown,
        Parity,
        Geth
    }
}
