// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Ethereum.Test.Base;
using Nethermind.Int256;
using Snappier;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Beacon.Test;

public static class BeaconConsensusTestLoader
{
    private const string ArchiveUrlTemplate = "https://github.com/ethereum/consensus-specs/releases/download/{0}/{1}";
    private const string DefaultVersion = "v1.6.1";
    private const string DefaultArchive = "mainnet.tar.gz";

    private static string? s_testsRoot;

    private static string GetTestsRoot() =>
        s_testsRoot ??= TestFixtureDownloader.EnsureDownloaded(
            "BeaconTests", ArchiveUrlTemplate, DefaultVersion, DefaultArchive);

    /// <summary>
    /// Returns the path to the ssz_static test directory for a given fork and type handler,
    /// e.g. GetHandlerPath("electra", "Checkpoint") returns .../tests/mainnet/electra/ssz_static/Checkpoint.
    /// </summary>
    public static string GetHandlerPath(string fork, string handler) =>
        GetTestPath(fork, "ssz_static", handler);

    /// <summary>
    /// Returns the path to a test handler directory, e.g. GetTestPath("fulu", "epoch_processing", "slashings")
    /// returns .../tests/mainnet/fulu/epoch_processing/slashings.
    /// </summary>
    public static string GetTestPath(string fork, string runner, string handler) =>
        Path.Combine(GetTestsRoot(), "tests", "mainnet", fork, runner, handler);

    /// <summary>
    /// Reads and decompresses a .ssz_snappy file.
    /// </summary>
    public static byte[] ReadSszSnappy(string filePath)
    {
        byte[] compressed = File.ReadAllBytes(filePath);
        return Snappy.DecompressToArray(compressed);
    }

    /// <summary>
    /// Parses the "root" field from a roots.yaml file and returns it as a UInt256.
    /// </summary>
    public static UInt256 ParseRoot(string rootsFilePath)
    {
        using StreamReader reader = new(rootsFilePath);
        YamlStream yaml = [];
        yaml.Load(reader);
        YamlMappingNode mapping = (YamlMappingNode)yaml.Documents[0].RootNode;
        string hexRoot = ((YamlScalarNode)mapping[new YamlScalarNode("root")]).Value!;
        return new UInt256(Convert.FromHexString(hexRoot[2..]));
    }
}
