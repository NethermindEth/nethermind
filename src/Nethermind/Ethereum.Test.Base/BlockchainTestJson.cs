﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Ethereum.Test.Base
{
    public class HalfBlockchainTestJson : BlockchainTestJson
    {
        public new Keccak PostState { get; set; }
    }
    
    public class BlockchainTestJson
    {
        public string Network { get; set; }
        public IReleaseSpec EthereumNetwork { get; set; }
        public IReleaseSpec EthereumNetworkAfterTransition { get; set; }
        public int TransitionBlockNumber { get; set; }
        public string LastBlockHash { get; set; }
        public string GenesisRlp { get; set; }

        public TestBlockJson[] Blocks { get; set; }
        public TestBlockHeaderJson GenesisBlockHeader { get; set; }

        public Dictionary<string, AccountStateJson> Pre { get; set; }
        public Dictionary<string, AccountStateJson> PostState { get; set; }
        
        public Keccak PostStateHash { get; set; }
        
        public string SealEngine { get; set; }
        public string LoadFailure { get; set; }
    }
}