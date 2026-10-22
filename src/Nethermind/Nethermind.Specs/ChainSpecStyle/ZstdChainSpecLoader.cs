// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Config;
using ZstdSharp;

namespace Nethermind.Specs.ChainSpecStyle;

public class ZstdChainSpecLoader(IChainSpecLoader decompressedLoader, string? dictionaryPath = null)
    : IChainSpecLoader
{
    private const string EmbeddedDictionaryPath = "Nethermind.Config.chainspec.dictionary";

    public ChainSpec Load(Stream streamData)
    {
        using DecompressionStream decompressedStream = new(streamData);

        if (dictionaryPath is not null)
        {
            using Stream dictionaryStream = File.OpenRead(dictionaryPath);
            LoadDictionary(decompressedStream, dictionaryStream);
        }
        else if (typeof(IConfig).Assembly.GetManifestResourceStream(EmbeddedDictionaryPath) is Stream dictionaryStream)
        {
            using (dictionaryStream)
            {
                LoadDictionary(decompressedStream, dictionaryStream);
            }
        }

        return decompressedLoader.Load(decompressedStream);
    }

    private static void LoadDictionary(DecompressionStream decompressedStream, Stream dictionaryStream)
    {
        int dictionaryLength = checked((int)dictionaryStream.Length);
        byte[] buffer = new byte[dictionaryLength];
        dictionaryStream.ReadExactly(buffer);
        decompressedStream.LoadDictionary(buffer);
    }
}
