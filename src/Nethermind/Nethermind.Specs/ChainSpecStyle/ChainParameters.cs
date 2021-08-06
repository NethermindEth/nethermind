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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Specs.ChainSpecStyle
{
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
        public long? Eip7Transition { get; set; }
        public long? Eip150Transition { get; set; }
        public long? Eip152Transition { get; set; }
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
        public long? Eip1108Transition { get; set; }
        public long? Eip1283Transition { get; set; }
        public long? Eip1283DisableTransition { get; set; }
        public long? Eip1283ReenableTransition { get; set; }
        public long? Eip1344Transition { get; set; }
        public long? Eip1706Transition { get; set; }
        public long? Eip1884Transition { get; set; }
        public long? Eip2028Transition { get; set; }
        public long? Eip2200Transition { get; set; }
        public long? Eip1559Transition { get; set; }
        public long? Eip2315Transition { get; set; }
        public long? Eip2537Transition { get; set; }
        public long? Eip2565Transition { get; set; }
        public long? Eip2929Transition { get; set; }
        public long? Eip2930Transition { get; set; }
        public long? Eip3198Transition { get; set; }
        public long? Eip3529Transition { get; set; }
        
        public long? Eip3541Transition { get; set; }
        
        public UInt256 Eip1559BaseFeeInitialValue { get; set; }

        public UInt256 Eip1559BaseFeeMaxChangeDenominator { get; set; }    
            
        public long Eip1559ElasticityMultiplier { get; set; }
        
        /// <summary>
        ///  Transaction permission managing contract address.
        /// </summary>
        public Address TransactionPermissionContract { get; set; }
        /// <summary>
        /// Block at which the transaction permission contract should start being used.
        /// </summary>
        public long? TransactionPermissionContractTransition { get; set; }

        /// <summary>
        /// Optional, will be included for block 0 by default - Block before which any chain_id in the signature of a replay-protected transaction is accepted.
        /// After this transition block, the transactions’ chain_id must match with the spec chain_id to be considered valid.
        /// </summary>
        /// <remarks>Backward compatibility for early Kovan blocks.</remarks>
        public long? ValidateChainIdTransition { get; set; }
        
        /// <summary>
        /// Optional, will be included for block 0 by default - Transition block before which the state root in transaction’s receipt can be stripped.
        /// </summary>
        /// <returns></returns>
        public long? ValidateReceiptsTransition { get; set; }
    }
}
