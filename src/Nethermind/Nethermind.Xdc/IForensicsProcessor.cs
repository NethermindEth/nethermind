// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public interface IForensicsProcessor
{
    Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCertificate incomingQC);

    Task SetCommittedQCs(IEnumerable<XdcBlockHeader> headers, QuorumCertificate incomingQC);

    Task ProcessForensics(QuorumCertificate incomingQC);

    Task SendForensicProof(QuorumCertificate firstQc, QuorumCertificate secondQc);

    (Hash256 AncestorHash, IList<string> FirstPath, IList<string> SecondPath)
        FindAncestorBlockHash(BlockRoundInfo firstBlockInfo, BlockRoundInfo secondBlockInfo);

    Task ProcessVoteEquivocation(Vote incomingVote);

    Task DetectEquivocationInVotePool(Vote vote, IEnumerable<Vote> votePool);

    Task SendVoteEquivocationProof(Vote vote1, Vote vote2, Address signer);
}
