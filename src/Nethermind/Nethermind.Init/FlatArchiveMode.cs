// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Init;

/// <summary>
/// Gives <c>Pruning.Mode=None</c> on the flat backend the meaning it has on patricia: an archive, which keeps the state
/// of every block it holds. On flat that is unbounded history, from genesis when synced from genesis and from the pivot
/// on when snap-synced. A snap-synced archive also builds and serves <c>eth_getProof</c> from its pivot on: the history
/// starts there in any case, and the walk that builds the pivot's commitments runs once. A node synced from genesis keeps
/// its own proof settings, since building them over the whole chain is a separate choice.
/// </summary>
/// <remarks>Only settings the operator left unset change: an explicit value always stands.</remarks>
public static class FlatArchiveMode
{
    // Arguments name the category as written ("FlatDb.X"); JSON and environment sources key it by the config type.
    private const string Category = "FlatDb";
    private const string TypeCategory = nameof(FlatDbConfig);

    /// <summary>Turns on unbounded history, and for a snap-synced archive also proof building and serving, when the
    /// flat backend runs with <c>Pruning.Mode=None</c>. Does nothing on patricia or with any other pruning mode.</summary>
    /// <param name="flatDbConfig">Changed in place; only the settings <paramref name="configProvider"/> reports unset.</param>
    /// <param name="pruningConfig">Selects archive mode with <see cref="PruningMode.None"/>.</param>
    /// <param name="syncConfig">Snap sync (fast sync with snap) starts the history at the pivot.</param>
    /// <param name="configProvider">Tells an explicit setting, from any config source, from one left unset.</param>
    /// <param name="logger">Receives the resulting mode and any setting that keeps it from being a full archive.</param>
    public static void Apply(IFlatDbConfig flatDbConfig, IPruningConfig pruningConfig, ISyncConfig syncConfig, IConfigProvider configProvider, ILogger logger)
    {
        if (!flatDbConfig.Enabled || pruningConfig.Mode != PruningMode.None) return;

        if (!IsSet(configProvider, nameof(IFlatDbConfig.HistoryEnabled))) flatDbConfig.HistoryEnabled = true;
        if (!flatDbConfig.HistoryEnabled)
        {
            if (logger.IsWarn) logger.Warn(
                "Pruning.Mode=None on the flat backend keeps only the state at the head, because FlatDb.HistoryEnabled is false. Unset it, or set it to true, to run an archive.");
            return;
        }

        bool snapSync = syncConfig.FastSync && syncConfig.SnapSync;
        bool unbounded = flatDbConfig.HistoryRetention == HistoryRetentionMode.None;
        if (snapSync && unbounded)
        {
            if (!IsSet(configProvider, nameof(IFlatDbConfig.ArchiveProofBuildEnabled))) flatDbConfig.ArchiveProofBuildEnabled = true;
            if (!IsSet(configProvider, nameof(IFlatDbConfig.ArchiveProofServeEnabled))) flatDbConfig.ArchiveProofServeEnabled = true;
        }

        if (snapSync && unbounded && flatDbConfig.Layout != FlatLayout.Flat && logger.IsWarn) logger.Warn(
            $"Pruning.Mode=None with snap sync starts the state history at the sync pivot, which needs FlatDb.Layout={nameof(FlatLayout.Flat)}; " +
            $"this node runs {flatDbConfig.Layout}, so history capture stops at the pivot. Set FlatDb.Layout={nameof(FlatLayout.Flat)} on a fresh database, " +
            "or sync from genesis with Sync.FastSync=false.");

        if (logger.IsInfo) logger.Info(
            $"Pruning.Mode=None on the flat backend: archive mode. State history is kept for every block from {(snapSync ? "the snap-sync pivot" : "genesis")} on; " +
            $"eth_getProof below the head is {(flatDbConfig.ArchiveProofBuildEnabled && flatDbConfig.ArchiveProofServeEnabled ? "served" : "not served")}.");
    }

    // A blank value binds as unset, so it does not count as a choice either.
    private static bool IsSet(IConfigProvider configProvider, string name) =>
        IsValue(configProvider.GetRawValue(Category, name)) || IsValue(configProvider.GetRawValue(TypeCategory, name));

    private static bool IsValue(object? raw) => raw is string value && !string.IsNullOrWhiteSpace(value);
}
