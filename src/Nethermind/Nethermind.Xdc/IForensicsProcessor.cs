// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Xdc.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public interface IForensicsProcessor
{
    // Core monitoring
    Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCert incomingQC);

    // Committed QC handling
    Task SetCommittedQCs(IEnumerable<XdcBlockHeader> headers, QuorumCert incomingQC);

    // Forensics process
    Task ProcessForensics(QuorumCert incomingQC);

    // Proof handling
    Task SendForensicProof(QuorumCert firstQc, QuorumCert secondQc);

    // Block ancestry check
    (Hash256 AncestorHash, IList<string> FirstPath, IList<string> SecondPath)
        FindAncestorBlockHash(Types.BlockInfo firstBlockInfo, Types.BlockInfo secondBlockInfo);

    // Vote equivocation detection
    Task ProcessVoteEquivocation(Vote incomingVote);

    Task DetectEquivocationInVotePool(Vote vote, List<Vote> votePool);

    Task SendVoteEquivocationProof(Vote vote1, Vote vote2, Address signer);
}
