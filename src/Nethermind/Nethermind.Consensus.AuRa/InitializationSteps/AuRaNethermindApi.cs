// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public LruCache<ValueKeccak, UInt256> TransactionPermissionContractVersions { get; }
            = new(
                PermissionBasedTxFilter.Cache.MaxCacheSize,
                nameof(TransactionPermissionContract));

        public AuRaContractGasLimitOverride.Cache? GasLimitCalculatorCache { get; set; }

        public IReportingValidator? ReportingValidator { get; set; }

        public ReportingContractBasedValidator.Cache ReportingContractValidatorCache { get; } = new ReportingContractBasedValidator.Cache();
        public TxPriorityContract.LocalDataSource? TxPriorityContractLocalDataSource { get; set; }
    }
}
