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
    // Public: Ethereum.ConsensusSpec.Test's preset-archive suites use the same release URL layout, just a
    // different asset name (mainnet.tar.gz / minimal.tar.gz instead of general.tar.gz).
    public const string ArchiveUrlTemplate = "https://github.com/ethereum/consensus-specs/releases/download/{0}/{1}";
    private const string SszGenericPrefix = "tests/general/phase0/ssz_generic";

    // The newest consensus-specs tag with ssz_generic vectors at all, not merely the newest one
    // whose general.tar.gz still has non-trivial content. PR ethereum/consensus-specs#5524 (merged
    // for v1.7.0-alpha.14) deleted the ssz_generic pytest source outright, in favor of a new,
    // separate repo, ethereum/ssz-specs. general.tar.gz shrinking to ~3KB from alpha.14 onward is a
    // downstream symptom of that removal, not a packaging regression: the mainnet/minimal preset
    // archives were checked directly and do not carry ssz_generic either, because it no longer
    // exists as a consensus-specs test format to package.
    //
    // ethereum/ssz-specs (as of its v0.1.0 release, the newest) is not yet a usable replacement:
    // its "ssz-test-vectors" archive holds 117 single-file JSON fixtures across 6 categories versus
    // the 2509 vectors across 11 categories (uints, basic_vector, bitlist, bitvector, boolean,
    // compatible_unions, containers, progressive_bitlist, progressive_containers,
    // basic_progressive_list, ...) this suite currently exercises, and its fixture schema
    // (one self-contained JSON document per case: typeName/value/serialized/root) has nothing in
    // common with the meta.yaml + serialized.ssz_snappy + roots.yaml layout SszConsensusTestLoader,
    // SszBasicTypeTests, SszBasicVectorTests, SszBitTests and SszContainerTests all parse today.
    // Adopting it would mean rewriting this suite against a fraction of today's coverage, not
    // advancing a pin - so the pin stays here until ssz-specs ships closer to parity.
    //
    // Do not pin back to the v1.6.x line either: it predates the EIP-7916 base-subtree change and
    // its progressive-container roots disagree with this implementation.
    private const string DefaultVersion = "v1.7.0-alpha.13";
    private const string DefaultArchive = "general.tar.gz";

    private static string? s_testsRoot;

    private static string GetTestsRoot() =>
        s_testsRoot ??= TestFixtureDownloader.EnsureDownloaded(
            "SszTests", ArchiveUrlTemplate, DefaultVersion, DefaultArchive,
            entry => TestFixtureDownloader.PathUnderPrefix(entry, SszGenericPrefix), extractionTag: SszGenericPrefix);

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
        YamlStream yaml = [];
        yaml.Load(reader);
        YamlMappingNode mapping = (YamlMappingNode)yaml.Documents[0].RootNode;
        string hexRoot = ((YamlScalarNode)mapping[new YamlScalarNode("root")]).Value!;
        return new UInt256(Convert.FromHexString(hexRoot[2..]));
    }
}
