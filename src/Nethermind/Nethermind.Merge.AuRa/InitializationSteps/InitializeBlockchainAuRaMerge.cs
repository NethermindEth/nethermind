// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.State;

namespace Nethermind.Merge.AuRa.InitializationSteps
{
    public class InitializeBlockchainAuRaMerge : InitializeBlockchainAuRa
    {
        private readonly AuRaNethermindApi _api;
        private readonly AuRaChainSpecEngineParameters _parameters;

        public InitializeBlockchainAuRaMerge(AuRaNethermindApi api) : base(api)
        {
            _api = api;
            _parameters = _api.ChainSpec.EngineChainSpecParametersProvider
                .GetChainSpecParameters<AuRaChainSpecEngineParameters>();
        }

        protected override AuRaBlockProcessor NewAuraBlockProcessor(ITxFilter txFilter, BlockCachePreWarmer? preWarmer, ITransactionProcessor transactionProcessor, IWorldState worldState)
        {
            IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = _parameters.RewriteBytecode;
            ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

            WithdrawalContractFactory withdrawalContractFactory = new WithdrawalContractFactory(_parameters, _api.AbiEncoder);

            return new AuRaMergeBlockProcessor(
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!.Get(transactionProcessor),
                new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(transactionProcessor), worldState),
                worldState,
                _api.ReceiptStorage!,
                new BeaconBlockRootHandler(transactionProcessor, worldState),
                _api.LogManager,
                _api.BlockTree!,
                new AuraWithdrawalProcessor(withdrawalContractFactory.Create(transactionProcessor), _api.LogManager),
                new ExecutionRequestsProcessor(transactionProcessor),
                CreateAuRaValidator(worldState, transactionProcessor),
                txFilter,
                GetGasLimitCalculator(),
                contractRewriter,
                preWarmer: preWarmer);
        }
    }
}
