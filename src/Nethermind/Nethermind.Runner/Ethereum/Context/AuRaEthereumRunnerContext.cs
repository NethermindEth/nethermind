//  Copyright (c) 2018 Demerzel Solutions Limited
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

using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.Runner.Ethereum.Context
{
    public class AuRaEthereumRunnerContext : EthereumRunnerContext
    {
        public AuRaEthereumRunnerContext(IConfigProvider configProvider, ILogManager logManager)
            : base(configProvider, logManager)
        {
        }
        
        public IBlockFinalizationManager? FinalizationManager { get; set; }
        public ITxPermissionFilter.Cache? TxFilterCache { get; set; }
        
        public ICache<Keccak, UInt256> TransactionPermissionContractVersions { get; } = new LruCacheWithRecycling<Keccak, UInt256>(ITxPermissionFilter.Cache.MaxCacheSize, nameof(TransactionPermissionContract));
        public IGasLimitOverride.Cache? GasLimitOverrideCache { get; set; }
        public IReportingValidator? ReportingValidator { get; set; }
        
        public ReportingContractBasedValidator.Cache ReportingContractValidatorCache { get; } = new ReportingContractBasedValidator.Cache();
    }
}
