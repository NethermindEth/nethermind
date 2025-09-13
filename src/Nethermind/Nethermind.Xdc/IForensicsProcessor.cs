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
    Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCert incomingQC);

    Task SetCommittedQCs(IEnumerable<XdcBlockHeader> headers, QuorumCert incomingQC);

    Task ProcessForensics(QuorumCert incomingQC);

    Task SendForensicProof(QuorumCert firstQc, QuorumCert secondQc);

    (Hash256 AncestorHash, IList<string> FirstPath, IList<string> SecondPath)
        FindAncestorBlockHash(Types.BlockInfo firstBlockInfo, Types.BlockInfo secondBlockInfo);

    Task ProcessVoteEquivocation(Vote incomingVote);

    Task DetectEquivocationInVotePool(Vote vote, List<Vote> votePool);

    Task SendVoteEquivocationProof(Vote vote1, Vote vote2, Address signer);
}
