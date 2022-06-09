using System.Collections.Generic;
using Nethermind;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.InitializationSteps;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Init.Steps;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Merge.AuRa.InitializationSteps
{
    public class InitializeBlockchainAuRaMerge : InitializeBlockchainAuRa
    {
        public InitializeBlockchainAuRaMerge(AuRaNethermindApi api) : base(api) { }

        protected override BlockProcessor NewBlockProcessor(AuRaNethermindApi api, ITxFilter txFilter, ContractRewriter contractRewriter)
        {
            return new AuRaMergeBlockProcessor(
                _api.PoSSwitcher!,
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!.Get(_api.TransactionProcessor!),
                new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor!, _api.StateProvider!),
                _api.StateProvider!,
                _api.StorageProvider!,
                _api.ReceiptStorage!,
                _api.LogManager,
                _api.BlockTree!,
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

            _api.SealValidator = new Merge.Plugin.MergeSealValidator(_api.PoSSwitcher!, _api.SealValidator!);
        }
    }
}
