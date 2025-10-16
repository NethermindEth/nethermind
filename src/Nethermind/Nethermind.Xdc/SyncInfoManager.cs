// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class SyncInfoManager : ISyncInfoManager
{
    public SyncInfoManager(XdcContext context, IQuorumCertificateManager quorumCertificateManager, ITimeoutCertificateManager timeoutCertificateProcessor, ILogger logger)
    {
        _context = context;
        _quorumCertificateManager = quorumCertificateManager;
        _timeoutCertificateManager = timeoutCertificateProcessor;
        _logger = logger;
    }

    private XdcContext _context { get; }
    private IQuorumCertificateManager _quorumCertificateManager { get; }
    private ITimeoutCertificateManager _timeoutCertificateManager { get; }
    public ILogger _logger { get; }

    public SyncInfo GetSyncInfo()
    {
        return new SyncInfo(_context.HighestQC, _context.HighestTC);
    }

    public void ProcessSyncInfo(SyncInfo syncInfo)
    {
        _quorumCertificateManager.CommitCertificate(syncInfo.HighestQuorumCert);
        _timeoutCertificateManager.ProcessTimeoutCertificate(syncInfo.HighestTimeoutCert);
    }

    public bool VerifySyncInfo(SyncInfo syncInfo)
    {
        if ((_context.HighestQC.ProposedBlockInfo.Round >= syncInfo.HighestQuorumCert.ProposedBlockInfo.Round)
            && (_context.HighestTC.Round >= syncInfo.HighestTimeoutCert.Round))
        {
            return false;
        }
        var result =  _quorumCertificateManager.VerifyCertificate(syncInfo.HighestQuorumCert, null, out string error) &&
            _timeoutCertificateManager.VerifyTimeoutCertificate(syncInfo.HighestTimeoutCert, out error);

        if (!result)
        {
            _logger.Error($"SyncInfo verification failed: {error}");
        }


        return result;
    }
}
