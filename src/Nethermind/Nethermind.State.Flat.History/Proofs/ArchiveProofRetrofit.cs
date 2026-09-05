// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Proofs;

public sealed class ArchiveProofRetrofit(
    IColumnsDb<FlatHistoryColumns> history,
    CommitmentDepthPolicy policy,
    CommitmentMetadata metadata,
    ArchiveProofSettings settings,
    CommitmentReclaimer reclaimer,
    ILogManager logManager) : ICommitmentEmitterSource
{
    private readonly ILogger _logger = logManager.GetClassLogger<ArchiveProofRetrofit>();

    public bool Enabled => settings.RetrofitEnabled;

    public CommitmentDepthPolicy Policy => policy;

    public ulong WindowGranularity => policy.Interval;

    public CommitmentEmitter CreateEmitter() => CommitmentEmitter.ForWalk(history, policy, metadata);

    public void Prepare()
    {
        metadata.EnsureLayout(policy, settings.DiscardMismatchedLayout, _logger);
        if (_logger.IsInfo) _logger.Info($"Archive proof commitments will be emitted along the history walk ({policy}).");
    }

    public void PruneBelow(ulong headBlock) => reclaimer.PruneBelow(headBlock);

    public ulong FirstBlockToBuild(ulong headBlock)
    {
        if (settings.RecentEpochs <= 0) return 0;

        ulong headEpoch = policy.Epoch(headBlock);
        if (headEpoch + 1 <= (ulong)settings.RecentEpochs) return 0;

        ulong floorEpoch = headEpoch + 1 - (ulong)settings.RecentEpochs;
        if (metadata.TryRaiseRetainedFromEpoch(floorEpoch)) metadata.TryRaiseFineFromEpoch(floorEpoch);
        return policy.EpochStart(metadata.RetainedFromEpoch);
    }

    public bool PublishCoverage(ulong fromInclusive, ulong toInclusive)
    {
        if (!metadata.TryPublishVerifiedCoverage(fromInclusive, toInclusive, out ulong coveredFrom, out ulong coveredTo))
        {
            if (_logger.IsWarn) _logger.Warn(
                $"Archive proof commitments for blocks {fromInclusive} to {toInclusive} were built but not published: they do not touch the already published coverage {coveredFrom} to {coveredTo}, and coverage is one contiguous range. Build the gap between them to join the two.");
            return false;
        }

        if (_logger.IsInfo) _logger.Info(
            $"Archive proof commitments cover blocks {coveredFrom} to {coveredTo}; eth_getProof serves that range once FlatDb.ArchiveProofServeEnabled is on.");
        return true;
    }
}
