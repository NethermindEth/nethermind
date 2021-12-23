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

using System.Threading.Tasks;
using Nethermind.Blockchain.Producers;
using Nethermind.Consensus;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;

namespace Nethermind.Api.Extensions
{
    public interface IConsensusPlugin : INethermindPlugin
    {
        /// <summary>
        /// Creates a block producer.
        /// </summary>
        /// <param name="blockProductionTrigger">Optional parameter. If present this should be the only block production trigger for created block producer. If absent <see cref="DefaultBlockProductionTrigger"/> should be used.</param>
        /// <param name="additionalTxSource">Optional parameter. If present this transaction source should be used before any other transaction sources, except consensus ones. Plugin still should use their own transaction sources.</param>
        /// <remarks>
        /// Can be called many times, with different parameters, each time should create a new instance. Example usage in MEV plugin.
        /// </remarks>
        Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger? blockProductionTrigger = null, ITxSource? additionalTxSource = null);
        
        string SealEngineType { get; }
        
        /// <summary>
        /// Default block production trigger for this consensus plugin.
        /// </summary>
        /// <remarks>
        /// Needed when this plugin is used in combination with other plugin that affects block production like MEV plugin.
        /// </remarks>
        IBlockProductionTrigger DefaultBlockProductionTrigger { get; }
		
		INethermindApi CreateApi() => new NethermindApi();
    }
}
