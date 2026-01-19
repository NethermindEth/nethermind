// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text.Json;
using Nethermind.Serialization.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// A chain spec loader that auto-detects the format of the input file and delegates
/// to either the regular ChainSpecLoader or the Geth-style GethGenesisLoader.
/// </summary>
public class AutoDetectingChainSpecLoader(IJsonSerializer serializer) : IChainSpecLoader
{
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
            GenesisFormat.Parity => _parityLoader.Load(memoryStream),
            _ => throw new InvalidDataException("Unable to detect genesis file format")
        };
    }

    private static GenesisFormat DetectFormat(Stream stream)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
            JsonElement root = document.RootElement;

            // Geth-style (EIP-7949) has "config" and "alloc" at top level
            // The "config" object contains chainId and hardfork blocks/times
            if (root.TryGetProperty("config", out JsonElement config) &&
                root.TryGetProperty("alloc", out _))
            {
                // Additional validation: check for chainId in config (required in EIP-7949)
                if (config.TryGetProperty("chainId", out _))
                {
                    return GenesisFormat.Geth;
                }
            }

            // Parity-style has "engine" and "params" at top level
            if (root.TryGetProperty("engine", out _) &&
                root.TryGetProperty("params", out _))
            {
                return GenesisFormat.Parity;
            }

            // Additional check: if it has "accounts" (Parity) vs "alloc" (Geth)
            if (root.TryGetProperty("accounts", out _))
            {
                return GenesisFormat.Parity;
            }

            // If we have "alloc" without "engine", assume Geth format
            if (root.TryGetProperty("alloc", out _))
            {
                return GenesisFormat.Geth;
            }

            return GenesisFormat.Unknown;
        }
        catch
        {
            return GenesisFormat.Unknown;
        }
    }

    private enum GenesisFormat
    {
        Unknown,
        Parity,
        Geth
    }
}
