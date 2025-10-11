// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

internal interface IVotesManager
{
    Task CastVote(BlockRoundInfo blockInfo);
    Task HandleVote(Vote vote);
    Task VerifyVotes(List<Vote> votes, XdcBlockHeader header);
    bool VerifyVotingRules(BlockRoundInfo blockInfo, QuorumCertificate qc);
    List<Vote> GetVotes();
}
