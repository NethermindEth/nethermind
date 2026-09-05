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
    ILogManager logManager) : ICommitmentEmitterSource
{
    private readonly ILogger _logger = logManager.GetClassLogger<ArchiveProofRetrofit>();
    private readonly CommitmentPruner _pruner = new(history, policy, metadata, settings, logManager);

    public bool Enabled => settings.RetrofitEnabled;

    public CommitmentDepthPolicy Policy => policy;

    public ulong WindowGranularity => policy.Interval;

    public CommitmentEmitter CreateEmitter() => CommitmentEmitter.ForWalk(history, policy, metadata, StorageSnapshotDepth);

    private int StorageSnapshotDepth => settings.RecentEpochs > 0 ? policy.StorageCheckpointDepth : CommitmentEmitter.DefaultStorageSnapshotDepth;

    public void Prepare()
    {
        metadata.EnsureLayout(policy, settings.DiscardMismatchedLayout, _logger);
        if (_logger.IsInfo) _logger.Info($"Archive proof commitments will be emitted along the history walk ({policy}).");
    }

    public void PruneBelow(ulong headBlock) => _pruner.PruneBelow(headBlock);

    public ulong FirstBlockToBuild(ulong headBlock)
    {
        if (settings.RecentEpochs <= 0) return 0;

        ulong headEpoch = policy.Epoch(headBlock);
        if (headEpoch + 1 <= (ulong)settings.RecentEpochs) return 0;

        ulong floorEpoch = headEpoch + 1 - (ulong)settings.RecentEpochs;
        ulong retained = metadata.RetainedFromEpoch;
        if (floorEpoch <= retained) return policy.EpochStart(retained);

        metadata.SetRetainedFromEpoch(floorEpoch);
        if (metadata.FineFromEpoch < floorEpoch) metadata.SetFineFromEpoch(floorEpoch);
        return policy.EpochStart(floorEpoch);
    }

    public void PublishCoverage(ulong fromInclusive, ulong toInclusive)
    {
        if (!metadata.TryPublishVerifiedCoverage(fromInclusive, toInclusive, out ulong coveredFrom, out ulong coveredTo))
        {
            if (_logger.IsWarn) _logger.Warn(
                $"Archive proof commitments for blocks {fromInclusive} to {toInclusive} were built but not published: they do not touch the already published coverage {coveredFrom} to {coveredTo}, and coverage is one contiguous range. Build the gap between them to join the two.");
            return;
        }

        if (_logger.IsInfo) _logger.Info(
            $"Archive proof commitments cover blocks {coveredFrom} to {coveredTo}; eth_getProof serves that range once FlatDb.ArchiveProofServeEnabled is on.");
    }
}
