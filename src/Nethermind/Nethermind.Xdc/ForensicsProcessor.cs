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
    public Task DetectEquivocationInVotePool(Vote vote, IEnumerable<Vote> votePool) => Task.CompletedTask;

    public (Hash256 AncestorHash, IList<string> FirstPath, IList<string> SecondPath) FindAncestorBlockHash(BlockRoundInfo firstBlockInfo, BlockRoundInfo secondBlockInfo) => default;

    public Task ForensicsMonitoring(IEnumerable<XdcBlockHeader> headerQcToBeCommitted, QuorumCertificate incomingQC) => Task.CompletedTask;

    public Task ProcessForensics(QuorumCertificate incomingQC) => Task.CompletedTask;

    public Task ProcessVoteEquivocation(Vote incomingVote) => Task.CompletedTask;

    public Task SendForensicProof(QuorumCertificate firstQc, QuorumCertificate secondQc) => Task.CompletedTask;

    public Task SendVoteEquivocationProof(Vote vote1, Vote vote2, Address signer) => Task.CompletedTask;

    public Task SetCommittedQCs(IEnumerable<XdcBlockHeader> headers, QuorumCertificate incomingQC) => Task.CompletedTask;
}
