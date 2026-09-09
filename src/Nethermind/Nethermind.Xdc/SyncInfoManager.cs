// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc;
/// <summary>
/// Skeleton implementation
/// </summary>
internal class SyncInfoManager(
    IXdcConsensusContext xdcContext,
    IQuorumCertificateManager qcManager,
    ITimeoutCertificateManager timeoutManager) : ISyncInfoManager
{
    public SyncInfo GetSyncInfo() => new(xdcContext.HighestQC, xdcContext.HighestTC);

    public string? ProcessQuorumCertificate(QuorumCertificate quorumCert)
    {
        if (quorumCert is null)
            return "QC is missing";

        ulong knownRound = xdcContext.HighestQC.ProposedBlockInfo.Round;
        if (quorumCert.ProposedBlockInfo.Round <= knownRound)
            return $"QC round {quorumCert.ProposedBlockInfo.Round} is not above the already known {knownRound}";

        if (!qcManager.VerifyCertificate(quorumCert, out string error))
            return $"QC is invalid: {error}";

        try
        {
            qcManager.CommitCertificate(quorumCert);
        }
        catch (IncomingMessageBlockNotFoundException e)
        {
            //We can get SyncInfo while syncing
            return $"QC block {e.IncomingBlockHash.ToShortString()} #{e.IncomingBlockNumber} not found";
        }

        return null;
    }

    public string? ProcessTimeoutCertificate(TimeoutCertificate timeoutCert)
    {
        if (timeoutCert is null)
            return "TC is missing";

        ulong? knownRound = xdcContext.HighestTC?.Round;
        if (timeoutCert.Round <= knownRound)
            return $"TC round {timeoutCert.Round} is not above the already known {knownRound}";

        if (!timeoutManager.VerifyTimeoutCertificate(timeoutCert, out string? error))
            return $"TC is invalid: {error}";

        timeoutManager.ProcessTimeoutCertificate(timeoutCert);
        return null;
    }
}
