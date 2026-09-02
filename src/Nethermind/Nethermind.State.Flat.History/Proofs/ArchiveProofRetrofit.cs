// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;
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

    public bool Enabled => settings.RetrofitEnabled;

    public ulong WindowGranularity => policy.Interval;

    public CommitmentEmitter CreateEmitter() => CommitmentEmitter.ForWalk(history, policy);

    public void Prepare()
    {
        if (metadata.TryReadStamp(policy, out bool matches) && !matches)
        {
            throw new InvalidConfigurationException(
                "The archive proof commitment columns were written under a different layout than this node is " +
                $"configured for ({policy}). Rows from the two layouts cannot be read together: delete the " +
                "flatHistory commitment columns and rebuild, or restore the previous FlatDb.ArchiveProof settings.", -1);
        }

        metadata.WriteStamp(policy);
        if (_logger.IsInfo) _logger.Info($"Archive proof commitments will be emitted along the history walk ({policy}).");
    }

    public void PublishCoverage(ulong fromInclusive, ulong toInclusive)
    {
        metadata.PublishVerifiedCoverage(fromInclusive, toInclusive);
        if (_logger.IsInfo) _logger.Info(
            $"Archive proof commitments cover blocks {fromInclusive} to {toInclusive}; eth_getProof serves that range once FlatDb.ArchiveProofServeEnabled is on.");
    }
}
