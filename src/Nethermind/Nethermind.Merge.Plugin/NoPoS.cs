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
using Nethermind.Merge.Plugin.Data.V1;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin
{
    public class NoPoS : IPoSSwitcher, ITransitionProcessHandler
    {
        private NoPoS() { }

        public static NoPoS Instance { get; } = new();

        public void ForkchoiceUpdated(BlockHeader newBlockHeader) { }

        public void SetFinalizedBlockHash(Keccak finalizedBlockHash) { }

        public bool IsPos(BlockHeader header) => false;

        public bool HasEverReachedTerminalPoWBlock() => false;

        public event EventHandler? TerminalPoWBlockReached;

        public UInt256? TerminalTotalDifficulty
        {
            get
            {
                return UInt256.MaxValue;
            }

            set
            {
                throw new NotSupportedException();
            }
        }

        public void SetTerminalPoWHash(Keccak blockHash)
        {
            throw new NotSupportedException();
        }
    }
}
