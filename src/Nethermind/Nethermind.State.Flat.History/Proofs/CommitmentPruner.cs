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

    public bool Enabled => settings.RecentEpochs > 0 || settings.FineEpochs > 0;

    public void PruneBelow(ulong headBlock)
    {
        if (!Enabled) return;

        ulong headEpoch = policy.Epoch(headBlock);
        Demote(headEpoch);
        Drop(headEpoch);
    }

    private void Demote(ulong headEpoch)
    {
        if (settings.FineEpochs <= 0 || !TryFloor(headEpoch, settings.FineEpochs, metadata.FineFromEpoch, out ulong keepFrom, out ulong from)) return;

        for (ulong epoch = from; epoch < keepFrom; epoch++)
        {
            _accounts.RemoveEpoch(epoch, CommitmentKeyLayout.FineTier);
            _storages.RemoveEpoch(epoch, CommitmentKeyLayout.FineTier);
        }

        metadata.SetFineFromEpoch(keepFrom);
        if (_logger.IsInfo) _logger.Info(
            $"Archive proof commitments below epoch {keepFrom} (block {policy.EpochStart(keepFrom)}) dropped their per-block rows; proofs there are still served, rebuilt from the checkpoint rows, which costs a second or so instead of a hundred milliseconds.");
    }

    private void Drop(ulong headEpoch)
    {
        if (settings.RecentEpochs <= 0 || !TryFloor(headEpoch, settings.RecentEpochs, metadata.RetainedFromEpoch, out ulong keepFrom, out ulong from)) return;

        for (ulong epoch = from; epoch < keepFrom; epoch++)
        {
            _accounts.RemoveEpoch(epoch, CommitmentKeyLayout.FineTier);
            _accounts.RemoveEpoch(epoch, CommitmentKeyLayout.CoarseTier);
            _storages.RemoveEpoch(epoch, CommitmentKeyLayout.FineTier);
            _storages.RemoveEpoch(epoch, CommitmentKeyLayout.CoarseTier);
        }

        metadata.SetRetainedFromEpoch(keepFrom);
        if (metadata.FineFromEpoch < keepFrom) metadata.SetFineFromEpoch(keepFrom);
        if (_logger.IsInfo) _logger.Info(
            $"Archive proof commitments below epoch {keepFrom} (block {policy.EpochStart(keepFrom)}) were dropped; historical proofs are served from that block on, keeping the {settings.RecentEpochs} most recent epochs of 2^{policy.EpochLog2} blocks.");
    }

    private static bool TryFloor(ulong headEpoch, int keep, ulong current, out ulong keepFrom, out ulong from)
    {
        keepFrom = 0;
        from = current;
        if (headEpoch + 1 <= (ulong)keep) return false;

        keepFrom = headEpoch + 1 - (ulong)keep;
        return keepFrom > current;
    }
}
