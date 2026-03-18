// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using Nethermind.Int256;
using Snappier;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Ssz.Test;

public static class SszConsensusTestLoader
{
    private const string ArchiveUrlTemplate = "https://github.com/ethereum/consensus-specs/releases/download/{0}/{1}";
    private const string DefaultVersion = "v1.6.1";
    private const string DefaultArchive = "general.tar.gz";
    private const string DefaultArchiveStem = "general";

    private static readonly string TestsRoot = Path.Combine(
        GetFixturesCacheDirectory(), "SszTests", DefaultVersion, DefaultArchiveStem);

    /// <summary>
    /// Returns a stable cache directory for SSZ consensus test fixtures that survives dotnet rebuilds.
    /// Uses the system temp directory with a stable subfolder name, matching the EEST fixture pattern.
    /// </summary>
    private static string GetFixturesCacheDirectory() =>
        Path.Combine(Path.GetTempPath(), "nethermind-eest");

    /// <summary>
    /// Thread- and process-safe fixture download. Uses a named mutex so that concurrent
    /// test processes sharing the same cache directory do not race on download/extract.
    /// A <c>.completed</c> marker file is written after a successful extraction so that
    /// a partial extraction from a previous crash is detected and retried.
    /// </summary>
    public static string EnsureExtracted()
    {
        string markerPath = Path.Combine(TestsRoot, ".completed");
        if (File.Exists(markerPath))
            return TestsRoot;

        string mutexName = $"ssz_{DefaultVersion}_{DefaultArchive}".Replace('/', '_').Replace('\\', '_');
        using Mutex mutex = new(false, mutexName);
        Console.WriteLine($"Waiting for SSZ fixture lock ({DefaultArchive} {DefaultVersion})...");
        if (!mutex.WaitOne(TimeSpan.FromMinutes(10)))
            throw new TimeoutException($"Timed out waiting for SSZ fixture mutex ({mutexName})");
        try
        {
            // Re-check after acquiring the mutex — another process may have finished.
            if (File.Exists(markerPath))
            {
                Console.WriteLine("SSZ fixtures were downloaded by another process.");
                return TestsRoot;
            }

            Console.WriteLine($"Downloading SSZ consensus test fixtures ({DefaultArchive} {DefaultVersion})...");
            DownloadAndExtract();
            File.WriteAllText(markerPath, DefaultVersion);
            Console.WriteLine($"SSZ fixtures extracted to {TestsRoot}");
        }
        finally
        {
            mutex.ReleaseMutex();
        }

        return TestsRoot;
    }

    private static void DownloadAndExtract()
    {
        // Clean up any partial extraction from a previous interrupted attempt.
        if (Directory.Exists(TestsRoot))
            Directory.Delete(TestsRoot, true);

        Directory.CreateDirectory(TestsRoot);

        using HttpClient httpClient = new();
        string url = string.Format(ArchiveUrlTemplate, DefaultVersion, DefaultArchive);
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        using HttpResponseMessage response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using Stream contentStream = response.Content.ReadAsStream();
        using GZipStream gzStream = new(contentStream, CompressionMode.Decompress);

        TarFile.ExtractToDirectory(gzStream, TestsRoot, overwriteFiles: true);
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

    /// <summary>
    /// Parses the "root" field from a meta.yaml file and returns it as a UInt256.
    /// </summary>
    public static UInt256 ParseRoot(string metaFilePath)
    {
        using StreamReader reader = new(metaFilePath);
        YamlStream yaml = new();
        yaml.Load(reader);
        YamlMappingNode mapping = (YamlMappingNode)yaml.Documents[0].RootNode;
        string hexRoot = ((YamlScalarNode)mapping[new YamlScalarNode("root")]).Value!;
        return new UInt256(Convert.FromHexString(hexRoot[2..]));
    }
}
