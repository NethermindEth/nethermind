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

using System.Diagnostics.CodeAnalysis;
using Nethermind.Dirichlet.Numerics;
using Newtonsoft.Json;

namespace Nethermind.Core.Specs.ChainSpec
{
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    internal class ChainSpecParamsJson
    {
        public UInt256 NetworkId { get; set; }
        
        [JsonProperty(PropertyName = "registrar")]
        public string EnsRegistrar { get; set; }
        
        public UInt256? GasLimitBoundDivisor { get; set; }
        
        public UInt256? AccountStartNonce { get; set; }
        
        public UInt256? MaximumExtraDataSize { get; set; }
        
        public string MinGasLimit { get; set; }
        
        public long? ForkBlock { get; set; }
        
        public string ForkCanonHash { get; set; }
        
        public long? Eip150Transition { get; set; }
        
        public long? Eip160Transition { get; set; }
        
        public long? Eip161AbcTransition { get; set; }
        
        public long? Eip161DTransition { get; set; }
        
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
        
        public long? Eip1283Transition { get; set; }
    }
}