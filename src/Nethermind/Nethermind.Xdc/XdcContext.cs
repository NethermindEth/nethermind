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

using static Nethermind.Xdc.HotStuffConfigExtensions;
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

namespace Nethermind.Xdc;
public class XdcContext
{
    public XdcContext()
    {
        VerifiedHeader = new ConcurrentDictionary<ValueHash256, BlockHeader>();

        CurrentRound = 0;
        HighestSelfMindeRound = 0;
        HighestVotedRound = 0;
        HighestQC = default;
        LockQC = default;
        HighestTC = default;

        LockQC = default;
        HighestTC = default;
        HighestCommitBlock = null;

    }

    public ConcurrentDictionary<ValueHash256, BlockHeader> VerifiedHeader { get; set; }
    public ConcurrentDictionary<Hash256, Address> Signatures { get; set; }
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

    public Func<IBlockTree, IWorldState, IWorldState, XdcBlockHeader, string[]> HookReward { get; set; }
    public Func<IBlockTree, UInt256, Hash256, Address[], Address[]> HookPenalty { get; set; }

    internal void BroadcastToBftChannel(object syncInfo)
    {
        throw new NotImplementedException();
    }

    internal bool IsAllowedToSend(IBlockTree tree, XdcBlockHeader header)
    {
        throw new NotImplementedException();
    }


    internal void SetNewRound(IBlockTree chain, ulong round)
    {
        CurrentRound = round;
        TimeoutCounter = 0;

        // timer should be reset outside
        NewRoundSetEvent.Invoke(chain, round);
    }
}
