// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Transactions;
using Nethermind.Init.Steps;
using Nethermind.Merge.AuRa.Withdrawals;

namespace Nethermind.Merge.AuRa.InitializationSteps
{
    public class InitializeBlockchainAuRaMerge : InitializeBlockchainAuRa
    {
        private readonly AuRaNethermindApi _api;

        public InitializeBlockchainAuRaMerge(AuRaNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override BlockProcessor NewBlockProcessor(AuRaNethermindApi api, ITxFilter txFilter, ContractRewriter contractRewriter)
        {
            var withdrawalContractFactory = new WithdrawalContractFactory(_api.ChainSpec!.AuRa, _api.AbiEncoder);

            return new AuRaMergeBlockProcessor(
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!.Get(_api.TransactionProcessor!),
                new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor!, _api.WorldState!),
                _api.WorldState!,
                _api.ReceiptStorage!,
                _api.LogManager,
                _api.BlockTree!,
                new AuraWithdrawalProcessor(
                    withdrawalContractFactory.Create(_api.TransactionProcessor!), _api.LogManager),
                _api.TransactionProcessor!,
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
