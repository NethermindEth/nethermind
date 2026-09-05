// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class CommitmentPruner(IColumnsDb<FlatHistoryColumns> history, CommitmentDepthPolicy policy, CommitmentMetadata metadata, ArchiveProofSettings settings, ILogManager logManager)
{
    private const int MaxEpochsPerPass = 4;

    private readonly CommitmentStore _accounts = new(history.GetColumnDb(FlatHistoryColumns.AccountCommitments), policy, 0);
    private readonly CommitmentStore _storages = new(history.GetColumnDb(FlatHistoryColumns.StorageCommitments), policy, CommitmentKeyLayout.IdentityLength);
    private readonly ILogger _logger = logManager.GetClassLogger<CommitmentPruner>();
    private ulong _heldAt;

    public bool Enabled => settings.RecentEpochs > 0 || settings.FineEpochs > 0;

    public void PruneBelow(ulong headBlock)
    {
        if (!Enabled) return;

        ulong headEpoch = policy.Epoch(headBlock);
        if (settings.FineEpochs > 0 && TryFloor(headEpoch, settings.FineEpochs, out ulong fineFrom) && metadata.TryRaiseFineFromEpoch(fineFrom) && _logger.IsInfo)
        {
            _logger.Info(
                $"Archive proof commitments below epoch {fineFrom} (block {policy.EpochStart(fineFrom)}) are losing their per-block rows; proofs there are still served, rebuilt from the checkpoint rows, which costs a second or so instead of a hundred milliseconds.");
        }

        if (settings.RecentEpochs > 0 && TryFloor(headEpoch, settings.RecentEpochs, out ulong retainedFrom) && metadata.TryRaiseRetainedFromEpoch(retainedFrom))
        {
            metadata.TryRaiseFineFromEpoch(retainedFrom);
            if (_logger.IsInfo) _logger.Info(
                $"Archive proof commitments below epoch {retainedFrom} (block {policy.EpochStart(retainedFrom)}) are being dropped; historical proofs are served from that block on, keeping the {settings.RecentEpochs} most recent epochs of 2^{policy.EpochLog2} blocks.");
        }

        Reclaim();
    }

    private void Reclaim()
    {
        int budget = MaxEpochsPerPass;
        ulong retained = metadata.RetainedFromEpoch;
        ulong verified = VerifiedEpochs();
        if (retained > verified && retained != _heldAt)
        {
            _heldAt = retained;
            if (_logger.IsInfo) _logger.Info(
                $"Archive proof commitments below epoch {retained} are no longer served but stay on disk until the every-block walk has verified the start of that epoch (it has reached epoch {verified}): only the walk writes the snapshot a retained epoch needs to answer for nodes that have not moved since.");
        }

        ulong dropped = metadata.DroppedThroughEpoch;
        ulong safe = Math.Min(retained, verified);
        while (dropped < safe && budget-- > 0)
        {
            _accounts.RemoveEpoch(dropped, CommitmentKeyLayout.FineTier);
            _accounts.RemoveEpoch(dropped, CommitmentKeyLayout.CoarseTier);
            _storages.RemoveEpoch(dropped, CommitmentKeyLayout.FineTier);
            _storages.RemoveEpoch(dropped, CommitmentKeyLayout.CoarseTier);
            metadata.TryRaiseDroppedThroughEpoch(++dropped);
        }

        ulong fine = metadata.FineFromEpoch;
        ulong demoted = Math.Max(metadata.DemotedThroughEpoch, dropped);
        while (demoted < fine && budget-- > 0)
        {
            _accounts.RemoveEpoch(demoted, CommitmentKeyLayout.FineTier);
            _storages.RemoveEpoch(demoted, CommitmentKeyLayout.FineTier);
            metadata.TryRaiseDemotedThroughEpoch(++demoted);
        }
    }

    private static bool TryFloor(ulong headEpoch, int keep, out ulong keepFrom)
    {
        keepFrom = 0;
        if (headEpoch + 1 <= (ulong)keep) return false;

        keepFrom = headEpoch + 1 - (ulong)keep;
        return true;
    }

    private ulong VerifiedEpochs()
    {
        if (!metadata.TryGetWalkVerified(out ulong from, out ulong to)) return 0;

        ulong epoch = policy.Epoch(to);
        return policy.EpochStart(epoch) >= from ? epoch : 0;
    }
}
