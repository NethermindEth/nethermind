// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth.GasPrice;
using Nethermind.TxPool;
using Nethermind.Config;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ReportingContractBasedValidator : IAuRaValidator, IReportingValidator
    {
        private delegate Transaction CreateReportTransactionDelegate(Address validator, long block, byte[] proof);

        private readonly ContractBasedValidator _contractValidator;
        private readonly long _posdaoTransition;
        private readonly ITxSender _posdaoTxSender;
        private readonly IReadOnlyStateProvider _stateProvider;
        private readonly Cache _cache;
        private readonly ISpecProvider _specProvider;
        private readonly ITxSender _nonPosdaoTxSender;
        private readonly ILogger _logger;

        public ReportingContractBasedValidator(
            ContractBasedValidator contractValidator,
            IReportingValidatorContract reportingValidatorContract,
            long posdaoTransition,
            ITxSender txSender,
            ITxPool txPool,
            IBlocksConfig blocksConfig,
            IReadOnlyStateProvider stateProvider,
            Cache cache,
            ISpecProvider specProvider,
            IGasPriceOracle gasPriceOracle,
            ILogManager logManager)
        {
            _contractValidator = contractValidator ?? throw new ArgumentNullException(nameof(contractValidator));
            ValidatorContract = reportingValidatorContract ?? throw new ArgumentNullException(nameof(reportingValidatorContract));
            _posdaoTransition = posdaoTransition;
            _posdaoTxSender = txSender ?? throw new ArgumentNullException(nameof(txSender));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _nonPosdaoTxSender = new TxGasPriceSender(txSender, gasPriceOracle);
            _persistentReports = cache.PersistentReports;
            _logger = logManager?.GetClassLogger<ReportingContractBasedValidator>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private IReportingValidatorContract ValidatorContract { get; }

        public void ReportMalicious(Address validator, long blockNumber, byte[] proof, IReportingValidator.MaliciousCause cause)
        {
            Report(ReportType.Malicious, validator, blockNumber, proof, cause, CreateReportMaliciousTransaction);
        }

        private Transaction CreateReportMaliciousTransaction(Address validator, long blockNumber, byte[] proof)
        {
            if (!Validators.Contains(validator))
            {
                if (_logger.IsWarn) _logger.Warn($"Not reporting {validator} on block {blockNumber}: Not a validator");
                return null;
            }

            var persistentReport = new PersistentReport(validator, (UInt256)blockNumber, proof);

            if (IsPosdao(blockNumber))
            {
                _persistentReports.AddLast(persistentReport);
                _sentReportsInBlock = blockNumber;
            }

            return CreateReportMaliciousTransactionCore(persistentReport);
        }

        private Transaction CreateReportMaliciousTransactionCore(PersistentReport persistentReport)
        {
            var transaction = ValidatorContract.ReportMalicious(persistentReport.MaliciousValidator, persistentReport.BlockNumber, persistentReport.Proof);
            transaction.Nonce = _stateProvider.GetNonce(ValidatorContract.NodeAddress);
            return transaction;
        }

        public void ReportBenign(Address validator, long blockNumber, IReportingValidator.BenignCause cause)
        {
            Report(ReportType.Benign, validator, blockNumber, Array.Empty<byte>(), cause.ToString(), CreateReportBenignTransaction);
        }

        private Transaction CreateReportBenignTransaction(Address validator, long blockNumber, byte[] proof) => ValidatorContract.ReportBenign(validator, (UInt256)blockNumber);

        private void Report(ReportType reportType, Address validator, long blockNumber, byte[] proof, object cause, CreateReportTransactionDelegate createReportTransactionDelegate)
        {
            try
            {
                if (_cache.AlreadyReported(reportType, validator, blockNumber))
                {
                    if (_logger.IsDebug) _logger.Debug($"Skipping report of {validator} at {blockNumber} with {cause} as its already reported.");
                }
                else
                {
                    if (!Validators.Contains(ValidatorContract.NodeAddress))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Skipping reporting {reportType} misbehaviour (cause: {cause}) at block #{blockNumber} from {validator} as we are not validator");
                    }
                    else
                    {
                        if (_logger.IsTrace) _logger.Trace($"Reporting {reportType} misbehaviour (cause: {cause}) at block #{blockNumber} from {validator}");

                        Transaction transaction = createReportTransactionDelegate(validator, blockNumber, proof);
                        if (transaction is not null)
                        {
                            ITxSender txSender = SetSender(blockNumber);
                            SendTransaction(reportType, txSender, transaction);
                            if (_logger.IsWarn) _logger.Warn($"Reported {reportType} validator {validator} misbehaviour (cause: {cause}) at block {blockNumber} with transaction {transaction.Hash}.");
                            if (reportType == ReportType.Malicious)
                            {
                                Metrics.ReportedMaliciousMisbehaviour++;
                            }
                            else
                            {
                                Metrics.ReportedBenignMisbehaviour++;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Validator {validator} could not be reported on block {blockNumber} with cause {cause}", e);
            }

        }

        private static void SendTransaction(ReportType reportType, ITxSender txSender, Transaction transaction)
        {
            TxHandlingOptions handlingOptions = reportType switch
            {
                ReportType.Benign => TxHandlingOptions.ManagedNonce,
                ReportType.Malicious => TxHandlingOptions.ManagedNonce | TxHandlingOptions.PersistentBroadcast,
                _ => TxHandlingOptions.ManagedNonce
            };

            txSender.SendTransaction(transaction, handlingOptions);
        }

        private ITxSender SetSender(long blockNumber)
        {
            bool posdao = IsPosdao(blockNumber);
            return posdao ? _posdaoTxSender : _nonPosdaoTxSender;
        }

        private bool IsPosdao(long blockNumber) => _posdaoTransition <= blockNumber;

        public void TryReportSkipped(BlockHeader header, BlockHeader parent)
        {
            if (Validators is null)
            {
                return;
            }

            var areThereSkipped = header.AuRaStep > parent.AuRaStep + 1;
            var firstBlock = header.Number == 1;
            if (areThereSkipped && !firstBlock)
            {
                Address[] validators = Validators;

                if (_logger.IsDebug) _logger.Debug($"Author {header.Beneficiary} built block with step gap indicating skipped steps. " +
                                                   $"Current step: {header.AuRaStep} at block {header.Number}, parent step: {parent.AuRaStep} at block {parent.Number}. " +
                                                   $"CurrentValidators [{(string.Join(", ", validators.AsEnumerable()))}");

                ISet<Address> reported = new HashSet<Address>();
                for (long step = parent.AuRaStep.Value + 1; step < header.AuRaStep.Value; step++)
                {
                    Address? skippedValidator = validators.GetItemRoundRobin(step);
                    if (skippedValidator is not null)
                    {
                        if (skippedValidator != ValidatorContract.NodeAddress)
                        {
                            if (reported.Contains(skippedValidator))
                            {
                                break;
                            }

                            ReportBenign(skippedValidator, header.Number, IReportingValidator.BenignCause.SkippedStep);
                            reported.Add(skippedValidator);
                            if (_logger.IsDebug) _logger.Debug($"Found skipped step {step} by author {skippedValidator}, actual author {header.Beneficiary} at block {header.Number}.");
                        }
                        else
                        {
                            if (_logger.IsDebug) _logger.Debug($"Found skipped step {step} by self {skippedValidator}, actual author {header.Beneficiary} at block {header.Number}. Not self-reporting.");
                        }
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
                var parentHeader = _contractValidator.BlockTree.FindParentHeader(block.Header, BlockTreeLookupOptions.None);
                if (parentHeader is not null)
                {
                    ResendPersistedReports(parentHeader);
                }
            }
        }
    }
}
