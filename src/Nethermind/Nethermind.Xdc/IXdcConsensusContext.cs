// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using System;

namespace Nethermind.Xdc;
public interface IXdcConsensusContext
{
    ulong CurrentRound { get; }
    BlockRoundInfo HighestCommitBlock { get; set; }
    QuorumCertificate? HighestQC { get; set; }
    TimeoutCertificate? HighestTC { get; set; }
    QuorumCertificate? LockQC { get; set; }
    int TimeoutCounter { get; set; }
    DateTime RoundStarted { get; }

    event EventHandler<NewRoundEventArgs> NewRoundSetEvent;

    void SetNewRound();
    void SetNewRound(ulong round);
}
