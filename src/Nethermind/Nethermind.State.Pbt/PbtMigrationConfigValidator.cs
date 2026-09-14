// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.State.Pbt;

internal static class PbtMigrationConfigValidator
{
    internal static void Validate(IPbtConfig config, IFlatDbConfig flatConfig, ChainSpec chainSpec, string targetPath)
    {
        if (!config.MigrationEnabled)
        {
            if (config.MigrationExportPath is not null) Fail("MigrationExportPath requires MigrationEnabled.");
            return;
        }

        if (config.Enabled || config.MirrorFlat || config.FakeMatchingStateRoot || config.ImportFromPreimageFlat || config.ScanTree)
            Fail("MigrationEnabled cannot be combined with standalone PBT, mirror, fake-root, offline import or scan modes.");
        if (!flatConfig.Enabled || flatConfig.Layout != FlatLayout.Flat)
            Fail("MigrationEnabled requires FlatDb.Enabled and FlatLayout.Flat; preimage-flat is an offline source only.");
        if (flatConfig.HistoryEnabled)
            Fail("Migration uses its own retention and cannot enable native FlatDb.HistoryEnabled.");
        if (chainSpec.Parameters.Eip8347TransitionTimestamp is not { } activation)
            Fail("MigrationEnabled requires binaryTrieTime in the chain specification.");
        else
        {
            if (chainSpec.Genesis is null || activation <= chainSpec.Genesis.Timestamp)
                Fail("Migration requires an MPT genesis before binaryTrieTime; use standalone PBT for PBT-at-genesis.");
            if (chainSpec.Parameters.Eip7928TransitionTimestamp is not { } balActivation || balActivation >= activation)
                Fail("EIP-7928 must activate before binaryTrieTime for migration replay.");
            if (chainSpec.Parameters.Eip6780TransitionTimestamp is not { } deletionActivation || deletionActivation >= activation)
                Fail("EIP-6780 must activate before binaryTrieTime for migration deletion semantics.");
            if (config.MigrationGenesisBootstrap &&
                (chainSpec.Parameters.Eip7928TransitionTimestamp > chainSpec.Genesis!.Timestamp ||
                 chainSpec.Parameters.Eip6780TransitionTimestamp > chainSpec.Genesis.Timestamp))
                Fail("Genesis migration bootstrap requires EIP-7928 and EIP-6780 at genesis.");
        }

        bool snapshot = HasPath(config.MigrationSnapshotPath);
        bool preimages = HasPath(config.MigrationPreimagesPath);
        bool source = HasPath(config.MigrationPreimageSourcePath);
        if (snapshot != preimages)
            Fail("MigrationSnapshotPath and MigrationPreimagesPath must be supplied together.");
        if (config.MigrationGenesisBootstrap ? snapshot || source : snapshot && source)
            Fail("Select at most one migration source: snapshot/preimages, offline preimage-flat, or genesis bootstrap.");
        if ((snapshot || source) && !HasPath(config.MigrationManifestPath))
            Fail("MigrationManifestPath is required for an external migration source.");

        if (config.MigrationExportPath is { } exportPath)
        {
            if (!HasPath(exportPath) || snapshot || !(source || config.MigrationGenesisBootstrap))
                Fail("MigrationExportPath requires genesis bootstrap or an offline preimage source, not portable input.");
            string output = Path.GetFullPath(exportPath);
            RejectLinks(output);
            if (Directory.Exists(output) || File.Exists(output)) Fail("MigrationExportPath must be a new directory.");
            foreach (string? input in new[] { targetPath, config.MigrationManifestPath, config.MigrationPreimageSourcePath })
            {
                if (input is null) continue;
                RejectLinks(Path.GetFullPath(input));
                if (ContainsPath(Path.GetFullPath(input), output) || ContainsPath(output, Path.GetFullPath(input)))
                    Fail("Migration export must not overlap target or source paths.");
            }
        }

        foreach (string? path in new[] { config.MigrationManifestPath, config.MigrationSnapshotPath, config.MigrationPreimagesPath, config.MigrationPreimageSourcePath })
        {
            if (path is null) continue;
            if (!HasPath(path)) Fail("Migration input paths must not be empty or whitespace.");
            string input = Path.GetFullPath(path);
            string target = Path.GetFullPath(targetPath);
            if (ContainsPath(target, input) || ContainsPath(input, target))
                Fail("Migration input paths must not overlap the writable target database path.");
        }
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null)
                Fail("Migration export paths must not contain symbolic links.");
    }

    private static bool HasPath(string? path) => !string.IsNullOrWhiteSpace(path);

    private static bool ContainsPath(string parent, string child)
    {
        string relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static void Fail(string message) => throw new InvalidConfigurationException(message, -1);
}
