// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentPruner(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata, ArchiveProofSettings settings, ILogManager logManager)
{
    private readonly CommitmentStore _accounts = new(history.GetColumnDb(FlatHistoryColumns.AccountCommitments), policy, 0);
    private readonly CommitmentStore _storages = new(history.GetColumnDb(FlatHistoryColumns.StorageCommitments), policy, CommitmentKeyLayout.IdentityLength);
    private readonly ILogger _logger = logManager.GetClassLogger<CommitmentPruner>();

    public bool Enabled => settings.RecentEpochs > 0;

    public void PruneBelow(ulong headBlock)
    {
        if (!Enabled) return;

        ulong headEpoch = policy.Epoch(headBlock);
        ulong recent = (ulong)settings.RecentEpochs;
        if (headEpoch + 1 <= recent) return;

        ulong keepFrom = headEpoch + 1 - recent;
        ulong retained = metadata.RetainedFromEpoch;
        if (keepFrom <= retained) return;

        for (ulong epoch = retained; epoch < keepFrom; epoch++)
        {
            _accounts.RemoveEpoch(epoch, CommitmentKeyLayout.FineTier);
            _storages.RemoveEpoch(epoch, CommitmentKeyLayout.FineTier);
        }

        metadata.SetRetainedFromEpoch(keepFrom);
        if (_logger.IsInfo) _logger.Info(
            $"Archive proof commitments below epoch {keepFrom} (block {policy.EpochStart(keepFrom)}) were dropped; historical proofs are served from that block on, keeping the {recent} most recent epochs of 2^{policy.EpochLog2} blocks.");
    }
}
