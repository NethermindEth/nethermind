/*
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
using System.Diagnostics;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.ChainSpec
{
    /// <summary>
    /// https://github.com/ethereum/wiki/wiki/Ethereum-Chain-Spec-Format
    /// https://wiki.parity.io/Chain-specification 
    /// </summary>
    [DebuggerDisplay("{Name}, ChainId = {ChainId}")]
    public class ChainSpec
    {
        public Dictionary<Address, UInt256> Allocations { get; set; }
        public NetworkNode[] Bootnodes { get; set; }
        public Block Genesis { get; set; }

        public class EthashParameters
        {
            public UInt256 MinimumDifficulty { get; set; }
        
            public UInt256 DifficultyBoundDivisor { get; set; }
            
            public long DurationLimit { get; set; }
            
            public long HomesteadTransition { get; set; }
            
            public long? DaoHardforkTransition { get; set; }
            
            /// <summary>
            /// This is stored in the Nethermind.Blockchain.DaoData class instead.
            /// </summary>
            public Address DaoHardforkBeneficiary { get; set; }
            
            /// <summary>
            /// This is stored in the Nethermind.Blockchain.DaoData class instead.
            /// </summary>
            public Address[] DaoHardforkAccounts { get; set; }
            
            public long Eip100bTransition { get; set; }

            public Dictionary<long, UInt256> BlockRewards { get; set; }
            
            public Dictionary<long, long> DifficultyBombDelays { get; set; }
        }

        public class CliqueParameters
        {
            public ulong Epoch { get; set; }
        
            public ulong Period { get; set; }
        
            public UInt256? Reward { get; set; }
        }

        public CliqueParameters Clique { get; set; }
        
        public EthashParameters Ethash { get; set; }

        /// <summary>
        /// Not used in Nethermind
        /// </summary>
        public string DataDir { get; set; }

        public SealEngineType SealEngineType { get; set; }

        public long? DaoForkBlockNumber { get; set; }
        
        public long? HomesteadBlockNumber { get; set; }
        
        public long? TangerineWhistleBlockNumber { get; set; }
        
        public long? SpuriousDragonBlockNumber { get; set; }
        
        public long? ByzantiumBlockNumber { get; set; }
        
        public long? ConstantinopleBlockNumber { get; set; }

        public UInt256 MaxCodeSize { get; set; }
        
        public string Name { get; set; }

        public int ChainId { get; set; }
    }
}