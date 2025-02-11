// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Reflection;
using Nethermind.Config;
using ZstdSharp;

namespace Nethermind.Specs.ChainSpecStyle;

public class ZstdChainSpecLoader : IChainSpecLoader
{
    private const string EmbeddedDictionaryPath = "Nethermind.Config.chainspec.dictionary";

    private readonly string? _dictionaryPath;
    private readonly IChainSpecLoader _decompressedLoader;

    public ZstdChainSpecLoader(IChainSpecLoader decompressedLoader, string? dictionaryPath = null)
    {
        _decompressedLoader = decompressedLoader;
        _dictionaryPath = dictionaryPath;
    }

    public ChainSpec Load(Stream streamData)
    {
        using Stream stream = _dictionaryPath is null
            ? typeof(IConfig).Assembly.GetManifestResourceStream(EmbeddedDictionaryPath)!
            : File.OpenRead(_dictionaryPath);

        byte[] buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);

        using var decompressedStream = new DecompressionStream(streamData);
        decompressedStream.LoadDictionary(buffer);

        return _decompressedLoader.Load(decompressedStream);
    }
}
