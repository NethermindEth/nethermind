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

using System.Numerics;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json;

namespace Nethermind.Core.Specs.ChainSpec
{
    internal class ChainSpecParamsJson
    {
        public UInt256 NetworkId { get; set; }
        
        [JsonProperty(PropertyName = "registrar")]
        public string EnsRegistrar { get; set; }
        
        public BigInteger? GasLimitBoundDivisor { get; set; }
        
        public BigInteger? AccountStartNonce { get; set; }
        
        public BigInteger? MaximumExtraDataSize { get; set; }
        
        public string MinGasLimit { get; set; }
        
        public BigInteger? ForkBlock { get; set; }
        
        public string ForkCanonHash { get; set; }
        
        public BigInteger? Eip150Transition { get; set; }
        
        public BigInteger? Eip160Transition { get; set; }
        
        public BigInteger? Eip161AbcTransition { get; set; }
        
        public BigInteger? Eip161DTransition { get; set; }
        
        public BigInteger? Eip155Transition { get; set; }
        
        public BigInteger? MaxCodeSize { get; set; }
        
        public BigInteger? MaxCodeSizeTransition { get; set; }
        
        public BigInteger? Eip140Transition { get; set; }
        
        public BigInteger? Eip211Transition { get; set; }
        
        public BigInteger? Eip214Transition { get; set; }
        
        public BigInteger? Eip658Transition { get; set; }
        
        public BigInteger? Eip145Transition { get; set; }
        
        public BigInteger? Eip1014Transition { get; set; }
        
        public BigInteger? Eip1052Transition { get; set; }
        
        public BigInteger? Eip1283Transition { get; set; }
    }
}