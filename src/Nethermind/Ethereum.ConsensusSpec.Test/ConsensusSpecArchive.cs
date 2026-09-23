// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Ethereum.Ssz.Test;
using Ethereum.Test.Base;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>Which consensus-specs preset archive a suite streams from.</summary>
public enum ConsensusPreset
{
    Minimal,
    Mainnet,
}

/// <summary>
/// Streams the consensus-specs preset archives (mainnet.tar.gz / minimal.tar.gz), pinned to
/// <see cref="Version"/>. Reuses <see cref="TestFixtureDownloader"/>'s selective-extraction path so only the suite
/// subtrees this driver can exercise ever hit disk - the mainnet archive is multi-gigabyte once
/// decompressed, and the gzip stream itself cannot be seeked, so every byte is still read and
/// decompressed regardless of what gets kept (see TestFixtureDownloader's own remarks).
/// </summary>
public static class ConsensusSpecArchive
{
    /// <summary>The consensus-specs release tag the preset archives are downloaded from.</summary>
    /// <remarks>
    /// Ahead of <see cref="SszConsensusTestLoader"/>'s pin, which cannot pass the last release that carries
    /// ssz_generic. v1.7.0-alpha.14 is the first release whose <c>upgrade_to_gloas</c> sets every field of
    /// the upgraded execution payload bid (consensus-specs #5550 and #5553), as <c>GloasForkTransition</c> does.
    /// </remarks>
    public const string Version = "v1.7.0-alpha.14";

    /// <summary>
    /// Set NETHERMIND_CONSENSUS_SPEC_MAINNET=1 to include the mainnet-preset vectors. Off by default:
    /// mainnet.tar.gz is on the order of 900 MB compressed and several GB decompressed even before
    /// selective extraction, which is too slow for a default `dotnet test` run.
    /// </summary>
    public static bool MainnetEnabled { get; } =
        Environment.GetEnvironmentVariable("NETHERMIND_CONSENSUS_SPEC_MAINNET") == "1";

    private static readonly Dictionary<ConsensusPreset, string> Roots = [];
    private static readonly Lock RootsLock = new();

    /// <summary>
    /// The forks whose operations/epoch_processing/sanity vectors are extracted and enumerated: exactly
    /// the forks <see cref="ForkDriver"/> can decode a beacon state for and carry through this repo's
    /// process_block/process_epoch pipeline. Phase0..Deneb have no state container in this repo at all,
    /// so their vectors are neither extracted (about 1 GB more of mainnet fixtures) nor enumerated.
    /// </summary>
    public static readonly string[] StateTransitionForks = ["electra", "fulu"];

    /// <summary>
    /// The post-fork names whose <c>fork</c> vectors (a pre-fork <c>pre</c> state upgraded to the named
    /// fork's <c>post</c>) are extracted and enumerated: exactly the forks <see cref="ForkTests"/> has an
    /// upgrade for. Fulu and Electra fork vectors are out of reach, as this repo has no <c>upgrade_to_fulu</c>
    /// and no Deneb state container to upgrade from.
    /// </summary>
    public static readonly string[] ForkUpgradeForks = ["gloas"];

    /// <summary>
    /// The suite subtrees this driver knows how to run: ssz_static for every fork this repo models a
    /// container for, the state-driven suites for <see cref="StateTransitionForks"/>, fork for <see cref="ForkUpgradeForks"/>, and fork_choice
    /// for fulu only. fork_choice needs its full fixture set (steps.yaml plus the anchor/block/attestation
    /// SSZ files it references), not just manifest.yaml. <see cref="ExtractionTag"/> is derived from this
    /// same table, so widening it invalidates the cached extraction by itself.
    /// </summary>
    private static readonly (string Suite, string[]? Forks)[] ExtractedSuites =
    [
        ("ssz_static", null),
        ("operations", StateTransitionForks),
        ("epoch_processing", StateTransitionForks),
        ("sanity", StateTransitionForks),
        ("fork_choice", ["fulu"]),
        ("fork", ForkUpgradeForks),
    ];

    /// <summary>
    /// Written into the cache's completion marker. A cache extracted under a narrower filter is
    /// complete for the subtrees it kept and silently empty for the ones it dropped - a suite over the
    /// missing subtree enumerates zero vectors and passes - so a marker carrying another tag is stale.
    /// </summary>
    public static readonly string ExtractionTag = string.Join(";",
        ExtractedSuites.Select(s => $"{s.Suite}={(s.Forks is null ? "*" : string.Join(",", s.Forks))}"));

    private static bool ShouldExtract(string entryPath)
    {
        int firstSlash = entryPath.IndexOf('/');
        if (firstSlash < 0) return false;
        // entryPath looks like "tests/{preset}/{fork}/{suite}/...".
        string[] parts = entryPath.Split('/');
        if (parts.Length < 4 || parts[0] != "tests") return false;
        string fork = parts[2];
        string suite = parts[3];

        foreach ((string Suite, string[]? Forks) extracted in ExtractedSuites)
        {
            if (extracted.Suite == suite)
                return extracted.Forks is null || Array.IndexOf(extracted.Forks, fork) >= 0;
        }
        return false;
    }

    /// <summary>Returns the extraction root for <paramref name="preset"/>, downloading and unpacking it on first use.</summary>
    public static string GetRoot(ConsensusPreset preset)
    {
        lock (RootsLock)
        {
            if (Roots.TryGetValue(preset, out string? cached))
                return cached;

            string archiveName = preset == ConsensusPreset.Mainnet ? "mainnet.tar.gz" : "minimal.tar.gz";
            string suiteName = preset == ConsensusPreset.Mainnet ? "ConsensusMainnet" : "ConsensusMinimal";
            string root = TestFixtureDownloader.EnsureDownloaded(
                suiteName, SszConsensusTestLoader.ArchiveUrlTemplate, Version, archiveName, ShouldExtract, ExtractionTag);
            Roots[preset] = root;
            return root;
        }
    }

    public static string PresetDirName(ConsensusPreset preset) => preset == ConsensusPreset.Mainnet ? "mainnet" : "minimal";

    /// <summary>Path to <c>tests/{preset}/{fork}/{suite}</c> under the preset's extraction root, or null if absent.</summary>
    public static string? SuitePath(ConsensusPreset preset, string fork, string suite)
    {
        string path = Path.Combine(GetRoot(preset), "tests", PresetDirName(preset), fork, suite);
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>Every immediate subdirectory of <paramref name="path"/>, or empty if it does not exist.</summary>
    public static IEnumerable<string> SubDirs(string? path)
    {
        if (path is null || !Directory.Exists(path))
            yield break;

        foreach (string dir in Directory.GetDirectories(path))
            yield return dir;
    }

    /// <summary>
    /// Recursively finds every directory under <paramref name="path"/> that directly contains a file
    /// named <paramref name="marker"/> (e.g. a leaf test case directory, marked by its own
    /// <c>serialized.ssz_snappy</c> or <c>meta.yaml</c>).
    /// </summary>
    public static IEnumerable<string> LeafDirs(string? path, string marker)
    {
        if (path is null || !Directory.Exists(path))
            yield break;

        foreach (string dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, marker)))
                yield return dir;
        }
    }
}
