// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Ethereum.Test.Base;
using Nethermind.Int256;
using Snappier;
using YamlDotNet.RepresentationModel;

namespace Ethereum.Ssz.Test;

public static class SszConsensusTestLoader
{
    private const string ArchiveUrlTemplate = "https://github.com/ethereum/consensus-specs/releases/download/{0}/{1}";
    private const string DefaultVersion = "v1.6.1";
    private const string DefaultArchive = "general.tar.gz";

    private static string? s_testsRoot;

    private static string GetTestsRoot() =>
        s_testsRoot ??= TestFixtureDownloader.EnsureDownloaded(
            "SszTests", ArchiveUrlTemplate, DefaultVersion, DefaultArchive);

    /// <summary>
    /// Returns the path to the ssz_generic test directory for a given type handler.
    /// e.g. GetHandlerPath("uints") returns .../tests/general/phase0/ssz_generic/uints
    /// </summary>
    public static string GetHandlerPath(string handler) =>
        Path.Combine(GetTestsRoot(), "tests", "general", "phase0", "ssz_generic", handler);

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
