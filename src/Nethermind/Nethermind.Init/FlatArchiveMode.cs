// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;

namespace Nethermind.Init;

/// <summary>
/// Gives <c>Pruning.Mode=None</c> the same meaning on the flat backend as on patricia: an archive that answers state
/// reads and <c>eth_getProof</c> at every height it holds, from genesis when synced from genesis and from the pivot
/// on when snap-synced. On flat that is unbounded history with archive proofs, so those are switched on here; a
/// bounded <c>FlatDb.HistoryRetention</c> is left alone, since windowed history cannot serve proofs.
/// </summary>
public static class FlatArchiveMode
{
    public static bool Apply(IFlatDbConfig flatDbConfig, IPruningConfig pruningConfig)
    {
        if (!flatDbConfig.Enabled || pruningConfig.Mode != PruningMode.None) return false;

        flatDbConfig.HistoryEnabled = true;
        if (flatDbConfig.HistoryRetention != HistoryRetentionMode.None) return true;

        flatDbConfig.ArchiveProofBuildEnabled = true;
        flatDbConfig.ArchiveProofServeEnabled = true;
        return true;
    }
}
