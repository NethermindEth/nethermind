// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus;

public class NoPoS : IPoSSwitcher
{
    private NoPoS() { }

    public static NoPoS Instance { get; } = new();

    public void ForkchoiceUpdated(BlockHeader newHeadHash, Keccak finalizedBlockHash)
    {
        throw new NotImplementedException();
    }

    public bool HasEverReachedTerminalBlock() => false;

#pragma warning disable 67
    public event EventHandler? TerminalBlockReached;
#pragma warning restore 67

    public UInt256? TerminalTotalDifficulty => null;
    public UInt256? FinalTotalDifficulty => null;
    public bool TransitionFinished => false;
    public Keccak ConfiguredTerminalBlockHash => Keccak.Zero;
    public long? ConfiguredTerminalBlockNumber => null;

    public bool TryUpdateTerminalBlock(BlockHeader header)
    {
        throw new NotImplementedException();
    }

    public (bool IsTerminal, bool IsPostMerge) GetBlockConsensusInfo(BlockHeader header) =>
        (false, false);

    public bool IsPostMerge(BlockHeader header) => false;
}
