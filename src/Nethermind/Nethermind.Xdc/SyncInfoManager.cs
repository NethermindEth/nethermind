// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class SyncInfoManager : ISyncInfoManager
{
    public SyncInfoManager(XdcContext context, IQuorumCertificateManager quorumCertificateManager, ITimeoutCertificateManager timeoutCertificateProcessor)
    {
        Context = context;
        QuorumCertificateManager = quorumCertificateManager;
        TimeoutCertificateManager = timeoutCertificateProcessor;
    }

    public XdcContext Context { get; }
    public IQuorumCertificateManager QuorumCertificateManager { get; }
    public ITimeoutCertificateManager TimeoutCertificateManager { get; }

    public SyncInfo GetSyncInfo()
    {
        return new SyncInfo(Context.HighestQC, Context.HighestTC);
    }

    public void ProcessSyncInfo(SyncInfo syncInfo)
    {
        QuorumCertificateManager.CommitCertificate(syncInfo.HighestQuorumCert);
        TimeoutCertificateManager.ProcessTimeoutCertificate(syncInfo.HighestTimeoutCert);
    }

    public bool VerifySyncInfo(SyncInfo syncInfo)
    {
        if ((Context.HighestQC.ProposedBlockInfo.Round >= syncInfo.HighestQuorumCert.ProposedBlockInfo.Round)
            && (Context.HighestTC.Round >= syncInfo.HighestTimeoutCert.Round))
        {
            return false;
        }
        try
        {
            QuorumCertificateManager.VerifyCertificate(syncInfo.HighestQuorumCert, null);
            TimeoutCertificateManager.VerifyTimeoutCertificate(syncInfo.HighestTimeoutCert);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
