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

using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class AuRaNethermindApi : NethermindApi
    {
        public new IAuRaBlockFinalizationManager? FinalizationManager
        {
            get => base.FinalizationManager as IAuRaBlockFinalizationManager; 
            set => base.FinalizationManager = value;
        }

        public PermissionBasedTxFilter.Cache? TxFilterCache { get; set; }

        public IValidatorStore? ValidatorStore { get; set; }

        public ICache<Keccak, UInt256> TransactionPermissionContractVersions { get; }
            = new LruCache<Keccak, UInt256>(
                PermissionBasedTxFilter.Cache.MaxCacheSize,
                nameof(TransactionPermissionContract));

        public AuRaContractGasLimitOverride.Cache? GasLimitCalculatorCache { get; set; }

        public IReportingValidator? ReportingValidator { get; set; }

        public ReportingContractBasedValidator.Cache ReportingContractValidatorCache { get; } = new ReportingContractBasedValidator.Cache();
        public TxPriorityContract.LocalDataSource? TxPriorityContractLocalDataSource { get; set; }
    }
}
