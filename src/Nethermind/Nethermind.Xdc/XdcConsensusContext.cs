// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc;

public class XdcConsensusContext : IXdcConsensusContext
{
    private ulong _currentRound;
    private readonly object _gate = new();

    public XdcConsensusContext()
    {
        HighestQC = new QuorumCertificate(new BlockRoundInfo(Hash256.Zero, 0, 0), [], 0);
        HighestTC = new TimeoutCertificate(0, [], 0);
    }

    public DateTime? RoundStarted { get; private set; }
    public int TimeoutCounter { get; set; }
    public ulong CurrentRound { get => _currentRound; set => _currentRound = value; }
    public QuorumCertificate HighestQC { get; set; }
    public QuorumCertificate? LockQC { get; set; }
    public TimeoutCertificate HighestTC { get; set; }
    public BlockRoundInfo? HighestCommitBlock { get; set; }

    public event EventHandler<NewRoundEventArgs>? NewRoundSetEvent;

    public void SetNewRound() => SetNewRound(CurrentRound + 1);
    public void SetNewRound(ulong round)
    {
        NewRoundEventArgs? eventArgs = null;
        lock (_gate)
        {
            if (round <= CurrentRound) return;

            int previousTimeoutCounter = TimeoutCounter;
            ulong last = CurrentRound;
            CurrentRound = round;
            TimeoutCounter = 0;
            DateTime? lastRoundStarted = RoundStarted;
            RoundStarted = DateTime.UtcNow;
            eventArgs = new NewRoundEventArgs(round, last, previousTimeoutCounter, RoundStarted - lastRoundStarted);
        }
        NewRoundSetEvent?.Invoke(this, eventArgs);
    }
}
