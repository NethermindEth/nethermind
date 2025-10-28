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
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public class XdcConsensusContext : IXdcConsensusContext
{
    private ulong _currentRound;

    public DateTime RoundStarted { get; private set; }
    public int TimeoutCounter { get; set; }
    public ulong CurrentRound { get => _currentRound; set => _currentRound = value; }
    public ulong HighestSelfMinedRound { get; set; }
    public ulong HighestVotedRound { get; set; }
    public QuorumCertificate? HighestQC { get; set; }
    public QuorumCertificate? LockQC { get; set; }
    public TimeoutCertificate? HighestTC { get; set; }
    public BlockRoundInfo HighestCommitBlock { get; set; }

    public event Action<NewRoundEventArgs> NewRoundSetEvent;

    public void SetNewRound() => SetNewRound(Interlocked.Increment(ref _currentRound));
    public void SetNewRound(ulong round)
    {
        int previousTimeoutCounter = TimeoutCounter;
        CurrentRound = round;
        TimeoutCounter = 0;
        RoundStarted = DateTime.UtcNow;

        // timer should be reset outside
        NewRoundSetEvent.Invoke(new NewRoundEventArgs(round, previousTimeoutCounter));
    }
}
