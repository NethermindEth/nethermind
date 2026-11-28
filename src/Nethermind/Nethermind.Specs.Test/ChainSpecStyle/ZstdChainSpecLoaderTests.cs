// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Text;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;
using ZstdSharp;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[TestFixture]
public class ZstdChainSpecLoaderTests
{
    [Test]
    public void Loads_a_plain_zstd_stream_without_an_embedded_dictionary()
    {
        byte[] payload = Encoding.UTF8.GetBytes("plain zstd chain spec payload");
        using MemoryStream compressed = new();
        using (CompressionStream compressor = new(compressed, leaveOpen: true))
        using (MemoryStream source = new(payload))
        {
            source.CopyTo(compressor);
        }

        compressed.Position = 0;
        RecordingLoader recordingLoader = new();

        _ = new ZstdChainSpecLoader(recordingLoader).Load(compressed);

        Assert.That(recordingLoader.Payload, Is.EqualTo(payload));
    }

    [Test]
    public void Loads_a_zstd_stream_with_an_explicit_dictionary()
    {
        byte[] dictionary = Encoding.UTF8.GetBytes("generic chain specification dictionary data");
        byte[] payload = Encoding.UTF8.GetBytes(
            "generic chain specification payload using generic chain specification dictionary data");
        string dictionaryPath = Path.GetTempFileName();

        try
        {
            File.WriteAllBytes(dictionaryPath, dictionary);

            using MemoryStream compressed = new();
            using (CompressionStream compressor = new(compressed, leaveOpen: true))
            using (MemoryStream source = new(payload))
            {
                compressor.LoadDictionary(dictionary);
                source.CopyTo(compressor);
            }

            compressed.Position = 0;
            RecordingLoader recordingLoader = new();

            _ = new ZstdChainSpecLoader(recordingLoader, dictionaryPath).Load(compressed);

            Assert.That(recordingLoader.Payload, Is.EqualTo(payload));
        }
        finally
        {
            File.Delete(dictionaryPath);
        }
    }

    private sealed class RecordingLoader : IChainSpecLoader
    {
        public byte[]? Payload { get; private set; }

        public ChainSpec Load(Stream streamData)
        {
            using MemoryStream payload = new();
            streamData.CopyTo(payload);
            Payload = payload.ToArray();
            return new ChainSpec();
        }
    }
}
