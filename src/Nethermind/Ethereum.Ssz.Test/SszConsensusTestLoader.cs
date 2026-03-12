// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Snappier;

namespace Ethereum.Ssz.Test;

public static class SszConsensusTestLoader
{
    private const string ArchiveUrlTemplate = "https://github.com/ethereum/consensus-specs/releases/download/{0}/{1}";
    private const string DefaultVersion = "v1.6.1";
    private const string DefaultArchive = "general.tar.gz";

    private static readonly string TestsRoot = Path.Combine(
        AppContext.BaseDirectory, "SszTests", DefaultVersion, "general");

    public static string EnsureExtracted()
    {
        if (Directory.Exists(TestsRoot))
            return TestsRoot;

        using HttpClient httpClient = new();
        string url = string.Format(ArchiveUrlTemplate, DefaultVersion, DefaultArchive);
        HttpResponseMessage response = httpClient.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using Stream contentStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using GZipStream gzStream = new(contentStream, CompressionMode.Decompress);

        Directory.CreateDirectory(TestsRoot);
        TarFile.ExtractToDirectory(gzStream, TestsRoot, overwriteFiles: true);

        return TestsRoot;
    }

    /// <summary>
    /// Returns the path to the ssz_generic test directory for a given type handler.
    /// e.g. GetHandlerPath("uints") returns .../tests/general/phase0/ssz_generic/uints
    /// </summary>
    public static string GetHandlerPath(string handler)
    {
        string root = EnsureExtracted();
        return Path.Combine(root, "tests", "general", "phase0", "ssz_generic", handler);
    }

    /// <summary>
    /// Reads and decompresses a .ssz_snappy file.
    /// </summary>
    public static byte[] ReadSszSnappy(string filePath)
    {
        byte[] compressed = File.ReadAllBytes(filePath);
        return Snappy.DecompressToArray(compressed);
    }
}
