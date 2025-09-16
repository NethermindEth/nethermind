// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BlockInfo = Nethermind.Xdc.Types.BlockInfo;
using Round = ulong;

using Nethermind.Blockchain;
using Nethermind.Int256;
using Nethermind.Core.Extensions;
using System.Threading;
using System.Net.Http.Headers;
using Org.BouncyCastle.Asn1.Mozilla;
using Nethermind.Evm.State;
using Nethermind.Blockchain.Receipts;
using Nethermind.State;
using Nethermind.Core.Collections;
using Snapshot = Nethermind.Xdc.Types.Snapshot;
using Nethermind.Crypto;
using Nethermind.Xdc.Errors;
using Nethermind.Consensus.Rewards;

namespace Nethermind.Xdc;
public class XdcContext
{
    public Address Leader { get; set; }
    public int TimeoutCounter { get; set; } = 0;
    public Round CurrentRound { get; set; }
    public Round HighestSelfMindeRound { get; set; }
    public Round HighestVotedRound { get; set; }
    public QuorumCert HighestQC { get; set; }
    public QuorumCert LockQC { get; set; }
    public TimeoutCert HighestTC { get; set; }
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
