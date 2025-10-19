// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc;
public interface IXdcConsensusContext
{
    ulong CurrentRound { get; }
    BlockRoundInfo HighestCommitBlock { get; set; }
    QuorumCertificate? HighestQC { get; set; }
    ulong HighestSelfMinedRound { get; set; }
    TimeoutCert? HighestTC { get; set; }
    ulong HighestVotedRound { get; set; }
    QuorumCertificate? LockQC { get; set; }
    int TimeoutCounter { get; set; }
    void SetNewRound(ulong round);
}
