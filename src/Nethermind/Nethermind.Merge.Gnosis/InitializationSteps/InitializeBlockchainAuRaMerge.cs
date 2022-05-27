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

namespace Nethermind.Merge.Gnosis.InitializationSteps
{
    public class InitializeBlockchainAuRaMerge : InitializeBlockchainAuRa
    {
        public InitializeBlockchainAuRaMerge(AuRaNethermindApi api) : base(api) { }

        protected override BlockProcessor CreateBlockProcessor()
        {
            if (_api.SpecProvider == null) throw new StepDependencyException(nameof(_api.SpecProvider));
            if (_api.ChainHeadStateProvider == null) throw new StepDependencyException(nameof(_api.ChainHeadStateProvider));
            if (_api.BlockValidator == null) throw new StepDependencyException(nameof(_api.BlockValidator));
            if (_api.RewardCalculatorSource == null) throw new StepDependencyException(nameof(_api.RewardCalculatorSource));
            if (_api.TransactionProcessor == null) throw new StepDependencyException(nameof(_api.TransactionProcessor));
            if (_api.DbProvider == null) throw new StepDependencyException(nameof(_api.DbProvider));
            if (_api.StateProvider == null) throw new StepDependencyException(nameof(_api.StateProvider));
            if (_api.StorageProvider == null) throw new StepDependencyException(nameof(_api.StorageProvider));
            if (_api.TxPool == null) throw new StepDependencyException(nameof(_api.TxPool));
            if (_api.ReceiptStorage == null) throw new StepDependencyException(nameof(_api.ReceiptStorage));
            if (_api.BlockTree == null) throw new StepDependencyException(nameof(_api.BlockTree));
            if (_api.GasPriceOracle == null) throw new StepDependencyException(nameof(_api.GasPriceOracle));
            if (_api.ChainSpec == null) throw new StepDependencyException(nameof(_api.ChainSpec));

            var processingReadOnlyTransactionProcessorSource = CreateReadOnlyTransactionProcessorSource();
            var txPermissionFilterOnlyTxProcessorSource = CreateReadOnlyTransactionProcessorSource();
            ITxFilter auRaTxFilter = TxAuRaFilterBuilders.CreateAuRaTxFilter(
                _api,
                txPermissionFilterOnlyTxProcessorSource,
                _api.SpecProvider,
                new ServiceTxFilter(_api.SpecProvider));

            IDictionary<long, IDictionary<Address, byte[]>> rewriteBytecode = _api.ChainSpec.AuRa.RewriteBytecode;
            ContractRewriter? contractRewriter = rewriteBytecode?.Count > 0 ? new ContractRewriter(rewriteBytecode) : null;

            var processor = new AuRaMergeBlockProcessor(
                _api.PoSSwitcher!,
                _api.SpecProvider,
                _api.BlockValidator,
                _api.RewardCalculatorSource!.Get(_api.TransactionProcessor!),
                new BlockProcessor.BlockValidationTransactionsExecutor(_api.TransactionProcessor!, _api.StateProvider!),
                _api.StateProvider,
                _api.StorageProvider,
                _api.ReceiptStorage,
                _api.LogManager,
                _api.BlockTree,
                auRaTxFilter,
                GetGasLimitCalculator(),
                contractRewriter
            );

            var auRaValidator = CreateAuRaValidator(processor, processingReadOnlyTransactionProcessorSource);
            processor.AuRaValidator = auRaValidator;
            var reportingValidator = auRaValidator.GetReportingValidator();
            _api.ReportingValidator = reportingValidator;
            if (_sealValidator != null)
            {
                _sealValidator.ReportingValidator = reportingValidator;
            }

            return processor;
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
