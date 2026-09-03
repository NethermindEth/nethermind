// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class ArchiveProofSettings
{
    public ArchiveProofSettings(IFlatDbConfig config, HistoryRowFormat rowFormat, ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger<ArchiveProofSettings>();
        bool supported = !rowFormat.IsV3;

        BuildEnabled = config.ArchiveProofBuildEnabled && supported;
        ServeEnabled = config.ArchiveProofServeEnabled && supported;
        RetrofitEnabled = BuildEnabled && config.HistoryVerifyEveryBlock;
        DiscardMismatchedLayout = config.ArchiveProofDiscardMismatchedLayout;
        RecentEpochs = config.ArchiveProofRecentEpochs < 0 ? 0 : config.ArchiveProofRecentEpochs;
        FineEpochs = config.ArchiveProofFineEpochs < 0 ? 0 : config.ArchiveProofFineEpochs;

        if (!supported && (config.ArchiveProofBuildEnabled || config.ArchiveProofServeEnabled) && logger.IsWarn)
        {
            logger.Warn(
                "Archive proofs are configured but this database holds windowed (v3) history rows, which are pre-values behind a " +
                "retention floor rather than the post-values a proof resolution replays. Nothing is built or served.");
        }

        if (BuildEnabled && !RetrofitEnabled && logger.IsInfo)
        {
            logger.Info(
                "Archive proof commitments are built from the tip only (FlatDb.HistoryVerifyEveryBlock is off). That is complete for a " +
                "node syncing from genesis; an already-synced archive also needs the every-block walk to retrofit the blocks below its watermark.");
        }
    }

    public bool BuildEnabled { get; }

    public bool ServeEnabled { get; }

    public bool RetrofitEnabled { get; }

    public bool DiscardMismatchedLayout { get; }

    public int RecentEpochs { get; }

    public int FineEpochs { get; }
}
