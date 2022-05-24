//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
