// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Init.Steps;
using Nethermind.Merge.AuRa.Withdrawals;
using Nethermind.State;

namespace Nethermind.Merge.AuRa.InitializationSteps
{
    public class InitializeBlockchainAuRaMerge : InitializeBlockchainAuRa
    {
        private readonly AuRaNethermindApi _api;
        private readonly AuRaChainSpecEngineParameters _parameters;
        private readonly IPoSSwitcher _poSSwitcher;

        public InitializeBlockchainAuRaMerge(AuRaNethermindApi api, IPoSSwitcher poSSwitcher) : base(api)
        {
            _api = api;
            _poSSwitcher = poSSwitcher;
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
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, worldState),
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

        protected override void InitSealEngine()
        {
            base.InitSealEngine();

            if (_api.SealValidator is null) throw new StepDependencyException(nameof(_api.SealValidator));

            _api.SealValidator = new Plugin.MergeSealValidator(_poSSwitcher!, _api.SealValidator!);
        }
    }
}
