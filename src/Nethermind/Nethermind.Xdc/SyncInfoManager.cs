// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Xdc.Errors;
using Nethermind.Xdc.RPC;
using Nethermind.Xdc.Types;
using System.Collections.Generic;
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
    private readonly object _syncInfoCacheLock = new();

    private readonly Dictionary<(ulong Round, Hash256 Hash), SyncInfoTypes> _syncInfoCache = [];

    public IDictionary<(ulong Round, Hash256 Hash), SyncInfoTypes> GetReceivedSyncInfos()
    {
        lock (_syncInfoCacheLock)
        {
            return new Dictionary<(ulong Round, Hash256 Hash), SyncInfoTypes>(_syncInfoCache);
        }
    }

    public SyncInfo GetSyncInfo() => new(xdcContext.HighestQC, xdcContext.HighestTC);

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
        finally
        {
            (ulong _, Hash256 Hash) key = syncInfo.GetSyncInfoKey();
            lock (_syncInfoCacheLock)
            {
                _syncInfoCache[key] = new SyncInfoTypes
                {
                    Hash = key.Hash,
                    QCSigners = syncInfo.HighestQuorumCert?.Signatures?.Length ?? 0,
                    TCSigners = syncInfo.HighestTimeoutCert?.Signatures?.Length ?? 0
                };
                HygieneSyncInfoCache();
            }
        }
    }

    private void HygieneSyncInfoCache()
    {
        ulong currentRound = xdcContext.CurrentRound;
        if (currentRound <= XdcConstants.PoolHygieneRound)
        {
            return;
        }

        ulong lowerBoundRound = currentRound - XdcConstants.PoolHygieneRound;
        foreach ((ulong Round, Hash256 Hash) key in _syncInfoCache.Keys)
        {
            if (key.Round < lowerBoundRound)
            {
                _syncInfoCache.Remove(key);
            }
        }
    }

    public bool VerifySyncInfo(SyncInfo syncInfo, [NotNullWhen(false)] out string? error)
    {
        if (xdcContext.HighestQC.ProposedBlockInfo.Round >= syncInfo.HighestQuorumCert.ProposedBlockInfo.Round &&
            xdcContext.HighestTC is not null && xdcContext.HighestTC?.Round >= syncInfo.HighestTimeoutCert.Round)
        {
            error = $"SyncInfo rounds are equal or lower than already known - QC={xdcContext.HighestQC.ProposedBlockInfo.Round}/{syncInfo.HighestQuorumCert.ProposedBlockInfo.Round} TC={xdcContext.HighestTC.Round}/{syncInfo.HighestTimeoutCert.Round}";
            return false;
        }

        if (!qcManager.VerifyCertificate(syncInfo.HighestQuorumCert, out error) ||
            !timeoutManager.VerifyTimeoutCertificate(syncInfo.HighestTimeoutCert, out error))
        {
            error ??= "SyncInfo contains invalid certificates.";
            return false;
        }
        return true;
    }
}
