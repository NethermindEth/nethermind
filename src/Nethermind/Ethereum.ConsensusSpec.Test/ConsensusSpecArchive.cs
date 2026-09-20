// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
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
/// Streams the consensus-specs preset archives (mainnet.tar.gz / minimal.tar.gz), pinned to the same
/// release tag as <see cref="SszConsensusTestLoader"/> (see that type's remarks for why the pin sits
/// there). Reuses <see cref="TestFixtureDownloader"/>'s selective-extraction path so only the suite
/// subtrees this driver can exercise ever hit disk - the mainnet archive is multi-gigabyte once
/// decompressed, and the gzip stream itself cannot be seeked, so every byte is still read and
/// decompressed regardless of what gets kept (see TestFixtureDownloader's own remarks).
/// </summary>
public static class ConsensusSpecArchive
{
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
    /// The suite subtrees this driver knows how to run: ssz_static for every fork this repo models a
    /// container for, and operations/epoch_processing/sanity/fork_choice for the fulu fork only (the
    /// only fork whose beacon state this repo's process_block/process_epoch pipeline accepts - see
    /// ForkedStateTransition's remarks). fork_choice needs its full fixture set (steps.yaml plus the
    /// anchor/block/attestation SSZ files it references), not just manifest.yaml.
    /// </summary>
    private static bool ShouldExtract(string entryPath)
    {
        int firstSlash = entryPath.IndexOf('/');
        if (firstSlash < 0) return false;
        // entryPath looks like "tests/{preset}/{fork}/{suite}/...".
        string[] parts = entryPath.Split('/');
        if (parts.Length < 4 || parts[0] != "tests") return false;
        string fork = parts[2];
        string suite = parts[3];

        return suite switch
        {
            "ssz_static" => true,
            "operations" or "epoch_processing" or "sanity" or "fork_choice" => fork == "fulu",
            _ => false,
        };
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
                suiteName, SszConsensusTestLoader.ArchiveUrlTemplate, SszConsensusTestLoader.DefaultVersion, archiveName, ShouldExtract);
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
