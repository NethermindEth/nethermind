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
using static Org.BouncyCastle.Asn1.Cmp.Challenge;

namespace Nethermind.Xdc;
public class XdcContext
{
    public Address Leader { get; set; }
    public int TimeoutCounter { get; set; }
    public ulong CurrentRound { get; set; }
    public ulong HighestSelfMindeRound { get; set; }
    public ulong HighestVotedRound { get; set; }
    public QuorumCertificate HighestQC { get; set; }
    public QuorumCertificate LockQC { get; set; }
    public TimeoutCertificate HighestTC { get; set; }
    public BlockRoundInfo HighestCommitBlock { get; set; }
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
