// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public interface IQuorumCertificateManager
{
    QuorumCertificate HighestKnownCertificate { get; }
    QuorumCertificate LockCertificate { get; }

    bool VerifyVotingRule(XdcBlockHeader header);
    void CommitCertificate(QuorumCertificate qc);
    bool VerifyCertificate(QuorumCertificate qc, XdcBlockHeader certificateTarget, out string error);
}
