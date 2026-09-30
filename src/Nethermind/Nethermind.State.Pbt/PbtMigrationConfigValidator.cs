// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.State.Pbt;

internal static class PbtMigrationConfigValidator
{
    /// <summary>Whether the chain specification schedules an EIP-8347 migration, i.e. a binaryTrieTime after the MPT genesis.</summary>
    internal static bool IsScheduledMigration(ChainSpec chainSpec) =>
        chainSpec.Parameters.Eip8347TransitionTimestamp is { } activation && chainSpec.Genesis is { } genesis && activation > genesis.Timestamp;

    internal static void Validate(IPbtConfig config, IFlatDbConfig flatConfig, ChainSpec chainSpec, string targetPath)
    {
        if (config.MigrationAnchor is < 0) Fail("MigrationAnchor must not be negative.");
        ValidateExport(config, flatConfig, chainSpec, targetPath);
        ValidateFakeRootSnapshotImport(config);
        if (!IsScheduledMigration(chainSpec)) return;

        if (config.MirrorFlat || config.FakeMatchingStateRoot || config.ImportFromPreimageFlat || config.ScanTree || config.ImportMigrationSnapshotWithFakeRoots)
            Fail("A scheduled binaryTrieTime migration cannot be combined with mirror, fake-root, offline import, scan or fake-root snapshot import modes.");
        if (!flatConfig.Enabled)
            Fail("A scheduled binaryTrieTime migration requires FlatDb.Enabled.");
        if (flatConfig.HistoryEnabled)
            Fail("Migration uses its own retention and cannot enable native FlatDb.HistoryEnabled.");
        ulong activation = chainSpec.Parameters.Eip8347TransitionTimestamp!.Value;
        if (chainSpec.Parameters.Eip7928TransitionTimestamp is not { } balActivation || balActivation >= activation)
            Fail("EIP-7928 must activate before binaryTrieTime for migration replay.");
        if (chainSpec.Parameters.Eip6780TransitionTimestamp is not { } deletionActivation || deletionActivation >= activation)
            Fail("EIP-6780 must activate before binaryTrieTime for migration deletion semantics.");
        if (config.MigrationGenesisBootstrap &&
            (chainSpec.Parameters.Eip7928TransitionTimestamp > chainSpec.Genesis!.Timestamp ||
             chainSpec.Parameters.Eip6780TransitionTimestamp > chainSpec.Genesis.Timestamp))
            Fail("Genesis migration bootstrap requires EIP-7928 and EIP-6780 at genesis.");

        bool snapshot = HasPath(config.MigrationSnapshotPath);
        bool preimages = HasPath(config.MigrationPreimagesPath);
        bool source = HasPath(config.MigrationPreimageSourcePath);
        // Preimages beside a snapshot only verify it, so they are not a source of their own.
        if ((snapshot ? 1 : 0) + (preimages && !snapshot ? 1 : 0) + (source ? 1 : 0) + (config.MigrationGenesisBootstrap ? 1 : 0) > 1)
            Fail("Select at most one migration source: snapshot (optionally verified by preimages), preimages, offline preimage-flat, or genesis bootstrap.");
        if ((snapshot || preimages || source) && config.MigrationAnchor is null)
            Fail("MigrationAnchor is required for an external migration source.");

        foreach (string? path in new[] { config.MigrationSnapshotPath, config.MigrationPreimagesPath, config.MigrationPreimageSourcePath })
        {
            if (path is null) continue;
            if (!HasPath(path)) Fail("Migration input paths must not be empty or whitespace.");
            string input = Path.GetFullPath(path);
            string target = Path.GetFullPath(targetPath);
            if (ContainsPath(target, input) || ContainsPath(input, target))
                Fail("Migration input paths must not overlap the writable target database path.");
        }
    }

    /// <remarks>The export reads this node's own state, so unlike the import it does not depend on a scheduled
    /// binaryTrieTime; it does depend on a preimage layout, since EIP-8297 keys are derived from the addresses and
    /// slot keys that a hash-keyed flat database does not retain.</remarks>
    private static void ValidateExport(IPbtConfig config, IFlatDbConfig flatConfig, ChainSpec chainSpec, string targetPath)
    {
        if (config.MigrationExportPath is not { } exportPath) return;
        if (!HasPath(exportPath)) Fail("MigrationExportPath must not be empty or whitespace.");
        if (config.Enabled || config.MirrorFlat || config.ImportFromPreimageFlat || config.ScanTree || IsScheduledMigration(chainSpec))
            Fail("MigrationExportPath reads the flat state instead of running the binary tree, so it cannot be combined with the PBT backend, a scheduled binaryTrieTime, or the other one-shot modes.");
        if (!flatConfig.Enabled || flatConfig.Layout is not (FlatLayout.PreimageFlat or FlatLayout.PreimageFlatV1))
            Fail("MigrationExportPath requires FlatDb.Enabled and a preimage-flat layout.");
        if (config.ExportStepDistance < 0) Fail("ExportStepDistance must not be negative.");
        string output = Path.GetFullPath(exportPath);
        RejectLinks(output);
        if (Directory.Exists(output) || File.Exists(output)) Fail("MigrationExportPath must be a new directory.");
        foreach (string? input in new[] { targetPath, config.MigrationPreimageSourcePath })
        {
            if (input is null) continue;
            RejectLinks(Path.GetFullPath(input));
            if (ContainsPath(Path.GetFullPath(input), output) || ContainsPath(output, Path.GetFullPath(input)))
                Fail("Migration export must not overlap target or source paths.");
        }
    }

    private static void ValidateFakeRootSnapshotImport(IPbtConfig config)
    {
        if (!config.ImportMigrationSnapshotWithFakeRoots) return;
        if (config.MirrorFlat) Fail("ImportMigrationSnapshotWithFakeRoots runs the PBT backend alone, so it cannot be combined with MirrorFlat.");
        if (!HasPath(config.MigrationSnapshotPath) || !HasPath(config.MigrationPreimagesPath) || config.MigrationAnchor is null)
            Fail("ImportMigrationSnapshotWithFakeRoots requires MigrationSnapshotPath, MigrationPreimagesPath and MigrationAnchor.");
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
