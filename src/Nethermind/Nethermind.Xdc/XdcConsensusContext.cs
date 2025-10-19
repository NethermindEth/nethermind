// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public class XdcConsensusContext : IXdcConsensusContext
{
    public int TimeoutCounter { get; set; }
    public ulong CurrentRound { get; private set; }
    public ulong HighestSelfMinedRound { get; set; }
    public ulong HighestVotedRound { get; set; }
    public QuorumCertificate? HighestQC { get; set; }
    public QuorumCertificate? LockQC { get; set; }
    public TimeoutCert? HighestTC { get; set; }
    public BlockRoundInfo HighestCommitBlock { get; set; }
    public void SetNewRound(ulong round)
    {
        CurrentRound = round;
        TimeoutCounter = 0;
    }
}
