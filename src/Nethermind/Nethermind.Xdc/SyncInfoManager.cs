// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Xdc;
/// <summary>
/// Skeleton implementation
/// </summary>
internal class SyncInfoManager(
    IXdcConsensusContext xdcContext,
    IQuorumCertificateManager qcManager,
    ITimeoutCertificateManager timeoutManager) : ISyncInfoManager
{
    public SyncInfo GetSyncInfo() => new SyncInfo(xdcContext.HighestQC, xdcContext.HighestTC);

    public void ProcessSyncInfo(SyncInfo syncInfo)
    {
        qcManager.CommitCertificate(syncInfo.HighestQuorumCert);
        timeoutManager.ProcessTimeoutCertificate(syncInfo.HighestTimeoutCert);
    }

    public bool VerifySyncInfo(SyncInfo syncInfo, [NotNullWhen(false)] out string error)
    {
        if (xdcContext.HighestQC.ProposedBlockInfo.Round >= syncInfo.HighestQuorumCert.ProposedBlockInfo.Round &&
            xdcContext.HighestTC.Round >= syncInfo.HighestTimeoutCert.Round)
        {
            error = $"SyncInfo rounds are equal or lower than already known - QC={xdcContext.HighestQC.ProposedBlockInfo.Round}/{syncInfo.HighestQuorumCert.ProposedBlockInfo.Round} TC={xdcContext.HighestTC.Round}/{syncInfo.HighestTimeoutCert.Round}";
            return false;
        }

        if(qcManager.VerifyCertificate(syncInfo.HighestQuorumCert, out error) ||
            timeoutManager.VerifyTimeoutCertificate(syncInfo.HighestTimeoutCert, out error))
        {
            return false;
        }
        return true;
    }
}
