// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Ethereum.Ssz.Test;
using Ethereum.Test.Base;

namespace Ethereum.ConsensusSpec.Test;

public enum ConsensusPreset
{
    Minimal,
    Mainnet,
}

public static class ConsensusSpecArchive
{
    public const string Version = "v1.7.0-beta.2";

    // Controls full-suite enumeration; required operation cases run regardless.
    public static bool MainnetEnabled { get; } =
        Environment.GetEnvironmentVariable("NETHERMIND_CONSENSUS_SPEC_MAINNET") == "1";

    private static readonly Dictionary<ConsensusPreset, string> Roots = [];
    private static readonly Lock RootsLock = new();

    public static readonly string[] StateTransitionForks = ["electra", "fulu", "gloas"];

    public static readonly string[] ForkUpgradeForks = ["gloas"];

    public static readonly string[] TransitionForks = ["gloas"];

    // Extract complete suites: fork-choice steps reference additional SSZ files.
    private static readonly (string Suite, string[]? Forks)[] ExtractedSuites =
    [
        ("ssz_static", null),
        ("operations", StateTransitionForks),
        ("epoch_processing", StateTransitionForks),
        ("sanity", StateTransitionForks),
        ("fork_choice", ["fulu", "gloas"]),
        ("fork", ForkUpgradeForks),
        ("transition", TransitionForks),
        ("networking", ["fulu", "gloas"]),
        ("finality", StateTransitionForks),
        ("random", StateTransitionForks),
        ("rewards", StateTransitionForks),
        ("shuffling", ["phase0"]),
        ("merkle_proof", ["electra", "fulu"]),
        ("sync", ["fulu"]),
        ("genesis", null),
    ];

    // Invalidate narrower cached extractions, which would silently pass new suites with zero vectors.
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

    public static string? SuitePath(ConsensusPreset preset, string fork, string suite)
    {
        string path = Path.Combine(GetRoot(preset), "tests", PresetDirName(preset), fork, suite);
        return Directory.Exists(path) ? path : null;
    }

    public static IEnumerable<string> SubDirs(string? path)
    {
        if (path is null || !Directory.Exists(path))
            yield break;

        foreach (string dir in Directory.GetDirectories(path))
            yield return dir;
    }

    public static IEnumerable<string> LeafDirs(string? path, string marker)
    {
        if (path is null || !Directory.Exists(path))
            yield break;

        foreach (string dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (File.Exists(Path.Combine(dir, marker)))
                yield return dir;
        }
    }
}
