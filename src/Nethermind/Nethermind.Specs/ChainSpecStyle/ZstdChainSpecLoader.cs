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
        using Stream stream = dictionaryPath is null
            ? typeof(IConfig).Assembly.GetManifestResourceStream(EmbeddedDictionaryPath)!
            : File.OpenRead(dictionaryPath);

        byte[] buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);

        using var decompressedStream = new DecompressionStream(streamData);
        decompressedStream.LoadDictionary(buffer);

        return decompressedLoader.Load(decompressedStream);
    }
}
