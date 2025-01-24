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
        using var buffer = new MemoryStream();
        if (_dictionaryPath is null)
        {
            Assembly assembly = typeof(IConfig).Assembly;
            using Stream stream = assembly.GetManifestResourceStream(EmbeddedDictionaryPath)!;
            stream.CopyTo(buffer);
        }
        else
        {
            using Stream stream = File.OpenRead(_dictionaryPath);
            stream.CopyTo(buffer);
        }

        using var decompressedStream = new DecompressionStream(streamData);
        decompressedStream.LoadDictionary(buffer.ToArray());
        return _decompressedLoader.Load(decompressedStream);
    }
}
