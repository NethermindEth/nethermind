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

using System;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Db.Blooms;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaValidatorFactory : IAuRaValidatorFactory
    {
        private readonly IStateProvider _stateProvider;
        private readonly IAbiEncoder _abiEncoder;
        private readonly ITransactionProcessor _transactionProcessor;
        private readonly IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;
        private readonly IBlockTree _blockTree;
        private readonly IReceiptFinder _receiptFinder;
        private readonly IValidatorStore _validatorStore;
        private readonly IAuRaBlockFinalizationManager _finalizationManager;
        private readonly ITxSender _txSender;
        private readonly ITxPool _txPool;
        private readonly IMiningConfig _miningConfig;
        private readonly ILogManager _logManager;
        private readonly ISigner _signer;
        private readonly ISpecProvider _specProvider;
        private readonly ReportingContractBasedValidator.Cache _reportingValidatorCache;
        private readonly long _posdaoTransition;
        private readonly bool _forSealing;

        public AuRaValidatorFactory(IAbiEncoder abiEncoder,
            IStateProvider stateProvider,
            ITransactionProcessor transactionProcessor,
            IBlockTree blockTree,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            IReceiptFinder receiptFinder,
            IValidatorStore validatorStore,
            IAuRaBlockFinalizationManager finalizationManager,
            ITxSender txSender,
            ITxPool txPool,
            IMiningConfig miningConfig,
            ILogManager logManager,
            ISigner signer,
            ISpecProvider specProvider,
            ReportingContractBasedValidator.Cache reportingValidatorCache,
            long posdaoTransition,
            bool forSealing = false)
        {
            _stateProvider = stateProvider;
            _abiEncoder = abiEncoder;
            _transactionProcessor = transactionProcessor;
            _readOnlyTxProcessorSource = readOnlyTxProcessorSource;
            _blockTree = blockTree;
            _receiptFinder = receiptFinder;
            _validatorStore = validatorStore;
            _finalizationManager = finalizationManager;
            _txSender = txSender;
            _txPool = txPool;
            _miningConfig = miningConfig;
            _logManager = logManager;
            _signer = signer;
            _reportingValidatorCache = reportingValidatorCache;
            _posdaoTransition = posdaoTransition;
            _forSealing = forSealing;
            _specProvider = specProvider;
        }

        public IAuRaValidator CreateValidatorProcessor(AuRaParameters.Validator validator, BlockHeader parentHeader = null, long? startBlock = null)
        {
            IValidatorContract GetValidatorContract() => new ValidatorContract(_transactionProcessor, _abiEncoder, validator.GetContractAddress(), _stateProvider, _readOnlyTxProcessorSource, _signer);
            IReportingValidatorContract GetReportingValidatorContract() => new ReportingValidatorContract(_abiEncoder, validator.GetContractAddress(), _signer);

            var validSealerStrategy = new ValidSealerStrategy();
            long startBlockNumber = startBlock ?? AuRaValidatorBase.DefaultStartBlockNumber;
            
            ContractBasedValidator GetContractBasedValidator() =>
                new ContractBasedValidator(
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
                        _miningConfig,
                        _stateProvider,
                        _reportingValidatorCache,
                        _specProvider,
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
                
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }
}
