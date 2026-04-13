// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal class ForensicsProcessor : IForensicsProcessor
{
    public Task DetectEquivocationInVotePool(Vote vote, IEnumerable<Vote> votePool)
    {
        return Task.CompletedTask;
    }

    public (Hash256 AncestorHash, IList<string> FirstPath, IList<string> SecondPath) FindAncestorBlockHash(BlockRoundInfo firstBlockInfo, BlockRoundInfo secondBlockInfo)
    {
        return default;
    }

    public Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCertificate incomingQC)
    {
        return Task.CompletedTask;
    }

    public Task ProcessForensics(QuorumCertificate incomingQC)
    {
        return Task.CompletedTask;
    }

    public Task ProcessVoteEquivocation(Vote incomingVote)
    {
        return Task.CompletedTask;
    }

    public Task SendForensicProof(QuorumCertificate firstQc, QuorumCertificate secondQc)
    {
        return Task.CompletedTask;
    }

    public Task SendVoteEquivocationProof(Vote vote1, Vote vote2, Address signer)
    {
        return Task.CompletedTask;
    }

    public Task SetCommittedQCs(IEnumerable<XdcBlockHeader> headers, QuorumCertificate incomingQC)
    {
        return Task.CompletedTask;
    }
}
