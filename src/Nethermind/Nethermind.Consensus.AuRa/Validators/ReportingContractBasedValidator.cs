//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Db.Blooms;
using Nethermind.Dirichlet.Numerics;
using Nethermind.TxPool;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ReportingContractBasedValidator : IAuRaValidator, IReportingValidator
    {
        private delegate Transaction CreateReportTransactionDelegate(Address validator, long block, byte[] proof);
        
        private readonly ContractBasedValidator _contractValidator;
        private readonly long _posdaoTransition;
        private readonly ITxSender _posdaoTxSender;
        private readonly IStateProvider _stateProvider;
        private readonly ITxSender _nonPosdaoTxSender;
        private readonly ILogger _logger;
        
        public ReportingContractBasedValidator(
            ContractBasedValidator contractValidator,
            IReportingValidatorContract reportingValidatorContract,
            long posdaoTransition,
            ITxSender txSender,
            ITxPool txPool,
            IStateProvider stateProvider,
            Cache cache,
            ILogManager logManager)
        {
            _contractValidator = contractValidator ?? throw new ArgumentNullException(nameof(contractValidator));
            ValidatorContract = reportingValidatorContract ?? throw new ArgumentNullException(nameof(reportingValidatorContract));
            _posdaoTransition = posdaoTransition;
            _posdaoTxSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _nonPosdaoTxSender = new TxGasPriceSender(txSender, txPool);
            _persistentReports = cache?.PersistentReports ?? throw new ArgumentNullException(nameof(cache));
            _logger = logManager?.GetClassLogger<ReportingContractBasedValidator>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private IReportingValidatorContract ValidatorContract { get; }
        
        public void ReportMalicious(Address validator, long blockNumber, byte[] proof, IReportingValidator.MaliciousCause cause)
        {
            Report("malicious", validator, blockNumber, proof, cause.ToString(), CreateReportMaliciousTransaction);
        }

        private Transaction CreateReportMaliciousTransaction(Address validator, long blockNumber, byte[] proof)
        {
            if (!Validators.Contains(validator))
            {
                if (_logger.IsWarn) _logger.Warn($"Not reporting {validator} on block {blockNumber}: Not a validator");
                return null;
            }
            
            var persistentReport = new PersistentReport(validator, (UInt256) blockNumber, proof);
            
            if (IsPosdao(blockNumber))
            {
                _persistentReports.AddLast(persistentReport);
            }

            return CreateReportMaliciousTransactionCore(persistentReport);
        }

        private Transaction CreateReportMaliciousTransactionCore(PersistentReport persistentReport)
        {
            var transaction = ValidatorContract.ReportMalicious(persistentReport.ValidatorAddress, persistentReport.BlockNumber, persistentReport.Proof);
            transaction.Nonce = _stateProvider.GetNonce(ValidatorContract.NodeAddress);
            return transaction;
        }

        public void ReportBenign(Address validator, long blockNumber, IReportingValidator.BenignCause cause)
        {
            Report("benign", validator, blockNumber, Bytes.Empty, cause.ToString(), CreateReportBenignTransaction);
        }

        private Transaction CreateReportBenignTransaction(Address validator, long blockNumber, byte[] proof) => ValidatorContract.ReportBenign(validator, (UInt256) blockNumber);

        private void Report(string type, Address validator, long blockNumber, byte[] proof, string cause, CreateReportTransactionDelegate createReportTransactionDelegate)
        {
            try
            {
                if (!Validators.Contains(ValidatorContract.NodeAddress))
                {
                    if (_logger.IsTrace) _logger.Trace($"Skipping reporting {type} misbehaviour (cause: {cause}) at block #{blockNumber} from {validator} as we are not validator");
                }
                else
                {
                    if (_logger.IsTrace) _logger.Trace($"Reporting {type} misbehaviour (cause: {cause}) at block #{blockNumber} from {validator}");

                    var transaction = createReportTransactionDelegate(validator, blockNumber, proof);
                    if (transaction != null)
                    {
                        var posdao = IsPosdao(blockNumber);
                        var txSender = posdao ? _posdaoTxSender : _nonPosdaoTxSender;
                        SendTransaction(txSender, transaction);
                        if (_logger.IsWarn) _logger.Warn($"Reported {type} validator {validator} misbehaviour (cause: {cause}) at block {blockNumber}");
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Validator {validator} could not be reported on block {blockNumber} with cause {cause}", e);
            }
        }

        private static void SendTransaction(ITxSender txSender, Transaction transaction)
        {
            txSender.SendTransaction(transaction, TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast);
        }

        private bool IsPosdao(long blockNumber) => _posdaoTransition <= blockNumber;

        public void TryReportSkipped(BlockHeader header, BlockHeader parent)
        {
            var areThereSkipped = header.AuRaStep > parent.AuRaStep + 1;
            var firstBlock = header.Number == 1;
            if (areThereSkipped && !firstBlock)
            {
                if (_logger.IsDebug) _logger.Debug($"Author {header.Beneficiary} built block with step gap. current step: {header.AuRaStep}, parent step: {parent.AuRaStep}");
                ISet<Address> reported = new HashSet<Address>();
                for (long step = parent.AuRaStep.Value + 1; step < header.AuRaStep.Value; step++)
                {
                    var skippedValidator = Validators.GetItemRoundRobin(step);
                    if (skippedValidator != ValidatorContract.NodeAddress)
                    {
                        if (reported.Contains(skippedValidator))
                        {
                            break;
                        }
                        
                        ReportBenign(skippedValidator, header.Number, IReportingValidator.BenignCause.SkippedStep);
                        reported.Add(skippedValidator);
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace("Primary that skipped is self, not self-reporting.");
                    }
                }
            }
        }

        public Address[] Validators => _contractValidator.Validators;

        public void OnBlockProcessingStart(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            _contractValidator.OnBlockProcessingStart(block, options);
        }

        public void OnBlockProcessingEnd(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None)
        {
            _contractValidator.OnBlockProcessingEnd(block, receipts, options);
            if (!_contractValidator.ForSealing)
            {
                ResendPersistedReports(block.Header);
            }
        }
    }
}
