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

using System.Collections.Generic;
using System.Diagnostics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
    /// <summary>
    /// https://github.com/ethereum/wiki/wiki/Ethereum-Chain-Spec-Format
    /// https://wiki.parity.io/Chain-specification 
    /// </summary>
    [DebuggerDisplay("{Name}, ChainId = {ChainId}")]
    public class ChainSpec
    {
        public string Name { get; set; }
        
        /// <summary>
        /// Not used in Nethermind
        /// </summary>
        public string DataDir { get; set; }
        
        public ulong ChainId { get; set; }

        public NetworkNode[] Bootnodes { get; set; }
        
        public Block Genesis { get; set; }
        
        public string SealEngineType { get; set; }
        
        public AuRaParameters AuRa { get; set; }
        
        public CliqueParameters Clique { get; set; }
        
        public EthashParameters Ethash { get; set; }
        
        public ChainParameters Parameters { get; set; }

        public Dictionary<Address, ChainSpecAllocation> Allocations { get; set; }
        
        public long? FixedDifficulty { get; set; }
        
        public long? DaoForkBlockNumber { get; set; }

        public long? HomesteadBlockNumber { get; set; }

        public long? TangerineWhistleBlockNumber { get; set; }

        public long? SpuriousDragonBlockNumber { get; set; }

        public long? ByzantiumBlockNumber { get; set; }

        public long? ConstantinopleBlockNumber { get; set; }
        
        public long? ConstantinopleFixBlockNumber { get; set; }

        public long? IstanbulBlockNumber { get; set; }
        
        public long? MuirGlacierNumber { get; set; }
        
        public long? BerlinBlockNumber { get; set; }
        
        public long? LondonBlockNumber { get; set; }
    }
}
