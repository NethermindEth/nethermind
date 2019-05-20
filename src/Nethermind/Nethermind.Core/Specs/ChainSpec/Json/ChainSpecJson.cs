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
using System.Diagnostics.CodeAnalysis;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Specs.ChainSpec.Json
{
    [SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
    [SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Global")]
    internal class ChainSpecJson
    {
        public string Name { get; set; }
        public string DataDir { get; set; }
        public EngineJson Engine { get; set; }
        public ChainSpecParamsJson Params { get; set; }
        public ChainSpecGenesisJson Genesis { get; set; }
        public string[] Nodes { get; set; }
        public Dictionary<string, AllocationJson> Accounts { get; set; }
        
        internal class EthashEngineJson
        {
            public long? HomesteadTransition => Params?.HomesteadTransition;
            public long? DaoHardforkTransition => Params?.DaoHardforkTransition;
            public Address DaoHardforkBeneficiary => Params?.DaoHardforkBeneficiary;
            public Address[] DaoHardforkAccounts => Params?.DaoHardforkAccounts;
            public long? DaoHardForkTransition => Params?.DaoHardforkTransition;
            public long? Eip100bTransition => Params?.Eip100bTransition;
            public UInt256? DifficultyBoundDivisor => Params?.DifficultyBoundDivisor;
            public long? DurationLimit => Params?.DurationLimit;
            public UInt256? MinimumDifficulty => Params?.MinimumDifficulty;
            public Dictionary<long, UInt256> BlockReward => Params?.BlockReward;
            public Dictionary<long, long> DifficultyBombDelays => Params?.DifficultyBombDelays;
            public EthashEngineParamsJson Params { get; set; }
        }
        
        internal class EthashEngineParamsJson
        {
            public UInt256? MinimumDifficulty { get; set; }
            public UInt256? DifficultyBoundDivisor { get; set; }
            public long? DurationLimit { get; set; }
            public long HomesteadTransition { get; set; }
            public long? DaoHardforkTransition { get; set; }
            public Address DaoHardforkBeneficiary { get; set; }
            public Address[] DaoHardforkAccounts { get; set; }
            public long Eip100bTransition { get; set; }
            public Dictionary<long, UInt256> BlockReward { get; set; }
            public Dictionary<long, long> DifficultyBombDelays { get; set; }
        }
    
        internal class CliqueEngineJson
        {
            public ulong Period => Params.Period;
            public ulong Epoch => Params.Epoch;
            public UInt256? BlockReward => Params.BlockReward;
            public CliqueEngineParamsJson Params { get; set; }
        }
        
        internal class CliqueEngineParamsJson
        {
            public ulong Period { get; set; }
            public ulong Epoch { get; set; }
            public UInt256? BlockReward { get; set; }
        }
        
        internal class AuraEngineJson
        {
            public Dictionary<string, object> Params { get; set; }
        }
        
        internal class NethDevJson
        {
        }
    
        internal class EngineJson
        {
            public EthashEngineJson Ethash { get; set; }
            public CliqueEngineJson Clique { get; set; }
            public AuraEngineJson AuthorityRound { get; set; }
            public NethDevJson NethDev { get; set; }
        }
    }
}