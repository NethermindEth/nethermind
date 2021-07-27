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

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Newtonsoft.Json;

namespace Nethermind.Specs.ChainSpecStyle.Json
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    internal class ChainSpecParamsJson
    {
        public ulong NetworkId { get; set; }
        
        [JsonProperty(PropertyName = "registrar")]
        public Address EnsRegistrar { get; set; }
        
        public long? GasLimitBoundDivisor { get; set; }
        
        public UInt256? AccountStartNonce { get; set; }
        
        public long? MaximumExtraDataSize { get; set; }
        
        public long? MinGasLimit { get; set; }
        
        public long? ForkBlock { get; set; }
        
        public Keccak ForkCanonHash { get; set; }
        
        public long? Eip7Transition { get; set; }
        
        public long? Eip150Transition { get; set; }
        
        public long? Eip152Transition { get; set; }
        
        public long? Eip160Transition { get; set; }
        
        public long? Eip161abcTransition { get; set; }
        
        public long? Eip161dTransition { get; set; }
        
        public long? Eip155Transition { get; set; }
        
        public long? MaxCodeSize { get; set; }
        
        public long? MaxCodeSizeTransition { get; set; }
        
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

        public UInt256? Eip1559BaseFeeInitialValue { get; set; }

        public UInt256? Eip1559BaseFeeMaxChangeDenominator { get; set; }    
            
        public long? Eip1559ElasticityMultiplier { get; set; }
        
        public Address TransactionPermissionContract { get; set; }

        public long ? TransactionPermissionContractTransition { get; set; }
        
        public long? ValidateChainIdTransition { get; set; }
        
        public long? ValidateReceiptsTransition { get; set; }
    }
}
