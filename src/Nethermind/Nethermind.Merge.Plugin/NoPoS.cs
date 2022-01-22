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
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin
{
    public class NoPoS : IPoSSwitcher
    {
        private NoPoS() { }

        public static NoPoS Instance { get; } = new();

        public void ForkchoiceUpdated(BlockHeader newHeadHash, Keccak finalizedBlockHash) 
        {
            throw new NotImplementedException();
        }

        public bool IsPoS(BlockHeader header) => false;

        public bool HasEverReachedTerminalBlock() => false;

        public event EventHandler? TerminalBlockReached;
        public UInt256? TerminalTotalDifficulty => null;
        public bool TryUpdateTerminalBlock(BlockHeader header, BlockHeader? parent = null)
        {
            throw new NotImplementedException();
        }

        public (bool IsTerminal, bool IsPostMerge) GetBlockSwitchInfo(BlockHeader header, BlockHeader? parent = null) =>
            (false, false);

        public bool IsPostMerge(BlockHeader header, BlockHeader? parent = null) => false;
    }
}
