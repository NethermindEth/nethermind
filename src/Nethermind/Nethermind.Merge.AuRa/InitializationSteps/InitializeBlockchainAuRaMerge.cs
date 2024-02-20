// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.AuRa.Validators;
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

        public InitializeBlockchainAuRaMerge(AuRaNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override AuRaBlockProcessor NewAuraBlockProcessor(ITxFilter txFilter)
        {
            IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = _api.ChainSpec.AuRa.RewriteBytecode;
            ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

            WithdrawalContractFactory withdrawalContractFactory = new WithdrawalContractFactory(_api.ChainSpec!.AuRa, _api.AbiEncoder);
            IWorldState worldState = _api.WorldState!;
            ITransactionProcessor transactionProcessor = _api.TransactionProcessor!;

            return new AuRaMergeBlockProcessor(
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!.Get(_api.TransactionProcessor!),
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor!, worldState),
                worldState,
                _api.ReceiptStorage!,
                _api.LogManager,
                _api.BlockTree!,
                new AuraWithdrawalProcessor(
                    withdrawalContractFactory.Create(transactionProcessor!), _api.LogManager),
                CreateAuRaValidator(),
                txFilter,
                GetGasLimitCalculator(),
                contractRewriter
            );
        }

        protected override void InitSealEngine()
        {
            base.InitSealEngine();

            if (_api.PoSSwitcher is null) throw new StepDependencyException(nameof(_api.PoSSwitcher));
            if (_api.SealValidator is null) throw new StepDependencyException(nameof(_api.SealValidator));

            _api.SealValidator = new Plugin.MergeSealValidator(_api.PoSSwitcher!, _api.SealValidator!);
        }
    }
}
