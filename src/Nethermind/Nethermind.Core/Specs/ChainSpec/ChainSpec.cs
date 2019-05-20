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
using Nethermind.Core.Crypto;
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
        public string Name { get; set; }
        
        /// <summary>
        /// Not used in Nethermind
        /// </summary>
        public string DataDir { get; set; }
        
        public int ChainId { get; set; }

        public NetworkNode[] Bootnodes { get; set; }
        
        public Block Genesis { get; set; }
        
        public SealEngineType SealEngineType { get; set; }
        
        public CliqueParameters Clique { get; set; }
        
        public EthashParameters Ethash { get; set; }
        
        public ChainParameters Parameters { get; set; }

        public Dictionary<Address, UInt256> Allocations { get; set; }

        public long? DaoForkBlockNumber { get; set; }

        public long? HomesteadBlockNumber { get; set; }

        public long? TangerineWhistleBlockNumber { get; set; }

        public long? SpuriousDragonBlockNumber { get; set; }

        public long? ByzantiumBlockNumber { get; set; }

        public long? ConstantinopleBlockNumber { get; set; }
    }

    public class ChainParameters
    {
        public long MaxCodeSize { get; set; }
        public long MaxCodeSizeTransition { get; set; }
        public long GasLimitBoundDivisor { get; set; }
        public Address Registrar { get; set; }
        public UInt256 AccountStartNonce { get; set; }
        public long MaximumExtraDataSize { get; set; }
        public long MinGasLimit { get; set; }
        public Keccak ForkCanonHash { get; set; }
        public long? ForkBlock { get; set; }
        public long? Eip150Transition { get; set; }
        public long? Eip160Transition { get; set; }
        public long? Eip161abcTransition { get; set; }
        public long? Eip161dTransition { get; set; }
        public long? Eip155Transition { get; set; }
        public long? Eip140Transition { get; set; }
        public long? Eip211Transition { get; set; }
        public long? Eip214Transition { get; set; }
        public long? Eip658Transition { get; set; }
        public long? Eip145Transition { get; set; }
        public long? Eip1014Transition { get; set; }
        public long? Eip1052Transition { get; set; }
        public long? Eip1283Transition { get; set; }
    }
}