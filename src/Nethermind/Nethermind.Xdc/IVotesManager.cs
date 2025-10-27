// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal interface IVotesManager
{
    Task CastVote(BlockRoundInfo blockInfo);
    Task HandleVote(Vote vote);
    Task OnReceiveVote(Vote vote);
    bool VerifyVotingRules(BlockRoundInfo roundInfo, QuorumCertificate certificate);
    bool VerifyVotingRules(XdcBlockHeader header);
    bool VerifyVotingRules(Hash256 blockHash, long blockNumber, ulong roundNumber, QuorumCertificate qc);
}
