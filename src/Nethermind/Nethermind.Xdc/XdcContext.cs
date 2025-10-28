// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Concurrent;
using BlockInfo = Nethermind.Xdc.Types.BlockRoundInfo;
using Round = ulong;

namespace Nethermind.Xdc;

public class XdcContext
{
    public ConcurrentDictionary<Hash256, Address> Signatures { get; set; }
    public Address Leader { get; set; }
    public int TimeoutCounter { get; set; } = 0;
    public Round CurrentRound { get; set; }
    public Round HighestSelfMindeRound { get; set; }
    public Round HighestVotedRound { get; set; }
    public QuorumCertificate HighestQC { get; set; }
    public QuorumCertificate LockQC { get; set; }
    public TimeoutCertificate HighestTC { get; set; }
    public BlockInfo HighestCommitBlock { get; set; }
    public SignFn SignFun { get; set; }

    public bool IsInitialized { get; set; } = false;

    public event Action<IBlockTree, ulong> NewRoundSetEvent;
    internal void SetNewRound(IBlockTree chain, ulong round)
    {
        CurrentRound = round;
        TimeoutCounter = 0;

        // timer should be reset outside
        NewRoundSetEvent.Invoke(chain, round);
    }
}
