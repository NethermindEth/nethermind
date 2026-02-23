// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Logging;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.Types;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Xdc;
/// <summary>
/// Skeleton implementation
/// </summary>
internal class SyncInfoManager(
    IXdcConsensusContext xdcContext,
    IQuorumCertificateManager qcManager,
    ITimeoutCertificateManager timeoutManager,
    ILogManager logManager) : ISyncInfoManager
{
    private readonly ILogger _logger = logManager.GetClassLogger<SyncInfoManager>();
    public void ProcessSyncInfo(SyncInfo syncInfo)
    {
        try
        {
            timeoutManager.ProcessTimeoutCertificate(syncInfo.HighestTimeoutCert);
            qcManager.CommitCertificate(syncInfo.HighestQuorumCert);
        }
        catch (IncomingMessageBlockNotFoundException e)
        {
            //We can get SyncInfo while syncing
            if (_logger.IsDebug)
                _logger.Debug($"Couldn't find {nameof(SyncInfo)} block {e.IncomingBlockHash.ToShortString()} #{e.IncomingBlockNumber} ");
        }
    }

    public bool VerifySyncInfo(SyncInfo syncInfo, [NotNullWhen(false)] out string error)
    {
        if (xdcContext.HighestQC.ProposedBlockInfo.Round >= syncInfo.HighestQuorumCert.ProposedBlockInfo.Round &&
            xdcContext.HighestTC?.Round >= syncInfo.HighestTimeoutCert.Round)
        {
            error = $"SyncInfo rounds are equal or lower than already known - QC={xdcContext.HighestQC.ProposedBlockInfo.Round}/{syncInfo.HighestQuorumCert.ProposedBlockInfo.Round} TC={xdcContext.HighestTC.Round}/{syncInfo.HighestTimeoutCert.Round}";
            return false;
        }

        if (qcManager.VerifyCertificate(syncInfo.HighestQuorumCert, out error) ||
            timeoutManager.VerifyTimeoutCertificate(syncInfo.HighestTimeoutCert, out error))
        {
            return false;
        }
        return true;
    }
}
