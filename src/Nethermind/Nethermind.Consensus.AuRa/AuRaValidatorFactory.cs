// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.TxPool;
using Nethermind.Config;
using Nethermind.Evm.State;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaValidatorFactory(IAbiEncoder abiEncoder,
        IWorldState stateProvider,
        ITransactionProcessor transactionProcessor,
        IBlockTree blockTree,
        IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
        IReceiptFinder receiptFinder,
        IValidatorStore validatorStore,
        IAuRaBlockFinalizationManager finalizationManager,
        ITxSender txSender,
        ITxPool txPool,
        IBlocksConfig blocksConfig,
        ILogManager logManager,
        ISigner signer,
        ISpecProvider specProvider,
        IGasPriceOracle gasPriceOracle,
        ReportingContractBasedValidator.Cache reportingValidatorCache,
        long posdaoTransition, bool forSealing = false) : IAuRaValidatorFactory
    {
        private readonly IWorldState _stateProvider = stateProvider;
        private readonly IAbiEncoder _abiEncoder = abiEncoder;
        private readonly ITransactionProcessor _transactionProcessor = transactionProcessor;
        private readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource = readOnlyTxProcessorSource;
        private readonly IBlockTree _blockTree = blockTree;
        private readonly IReceiptFinder _receiptFinder = receiptFinder;
        private readonly IValidatorStore _validatorStore = validatorStore;
        private readonly IAuRaBlockFinalizationManager _finalizationManager = finalizationManager;
        private readonly ITxSender _txSender = txSender;
        private readonly ITxPool _txPool = txPool;
        private readonly IBlocksConfig _blocksConfig = blocksConfig;
        private readonly ILogManager _logManager = logManager;
        private readonly ISigner _signer = signer;
        private readonly ISpecProvider _specProvider = specProvider;
        private readonly IGasPriceOracle _gasPriceOracle = gasPriceOracle;
        private readonly ReportingContractBasedValidator.Cache _reportingValidatorCache = reportingValidatorCache;
        private readonly long _posdaoTransition = posdaoTransition;
        private readonly bool _forSealing = forSealing;

        public IAuRaValidator CreateValidatorProcessor(AuRaParameters.Validator validator, BlockHeader parentHeader = null, long? startBlock = null)
        {
            IValidatorContract GetValidatorContract() => new ValidatorContract(_transactionProcessor, _abiEncoder, validator.GetContractAddress(), _stateProvider, _readOnlyTxProcessorSource, _signer);
            IReportingValidatorContract GetReportingValidatorContract() => new ReportingValidatorContract(_abiEncoder, validator.GetContractAddress(), _signer);

            ValidSealerStrategy validSealerStrategy = new();
            long startBlockNumber = startBlock ?? AuRaValidatorBase.DefaultStartBlockNumber;

            ContractBasedValidator GetContractBasedValidator() =>
                new(
                    GetValidatorContract(),
                    _blockTree,
                    _receiptFinder,
                    _validatorStore,
                    validSealerStrategy,
                    _finalizationManager,
                    parentHeader,
                    _logManager,
                    startBlockNumber,
                    _posdaoTransition,
                    _forSealing);

            return validator.ValidatorType switch
            {
                AuRaParameters.ValidatorType.List =>
                    new ListBasedValidator(
                        validator,
                        validSealerStrategy,
                        _validatorStore,
                        _logManager,
                        startBlockNumber,
                        _forSealing),

                AuRaParameters.ValidatorType.Contract => GetContractBasedValidator(),

                AuRaParameters.ValidatorType.ReportingContract =>
                    new ReportingContractBasedValidator(
                        GetContractBasedValidator(),
                        GetReportingValidatorContract(),
                        _posdaoTransition,
                        _txSender,
                        _txPool,
                        _blocksConfig,
                        _stateProvider,
                        _reportingValidatorCache,
                        _specProvider,
                        _gasPriceOracle,
                        _logManager),

                AuRaParameters.ValidatorType.Multi =>
                    new MultiValidator(
                        validator,
                        this,
                        _blockTree,
                        _validatorStore,
                        _finalizationManager,
                        parentHeader,
                        _logManager,
                        _forSealing),

                _ => throw new ArgumentOutOfRangeException(nameof(validator), validator.ValidatorType, "Unknown validator type.")
            };
        }
    }
}
