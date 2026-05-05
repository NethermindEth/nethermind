// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System.Threading.Tasks;

namespace Nethermind.Xdc;

public interface IVotesManager
{
    Task CastVote(BlockRoundInfo blockInfo);
    Task HandleVote(Vote vote);
    Task OnReceiveVote(Vote vote);
    bool VerifyVotingRules(BlockRoundInfo roundInfo, QuorumCertificate certificate, [NotNullWhen(false)] out string? error);
    bool VerifyVotingRules(XdcBlockHeader header, [NotNullWhen(false)] out string? error);
    bool VerifyVotingRules(Hash256 blockHash, long blockNumber, ulong roundNumber, QuorumCertificate qc, [NotNullWhen(false)] out string? error);
}
