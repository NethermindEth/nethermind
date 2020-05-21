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
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Db.Blooms;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class ReportingContractBasedValidator : ContractBasedValidator, IReportingValidator
    {
        private readonly ILogger _logger;
        
        public ReportingContractBasedValidator(
            ValidatorContract validatorContract,
            ReportingValidatorContract reportingValidatorContract,
            IBlockTree blockTree,
            IReceiptFinder receiptFinder,
            IValidatorStore validatorStore,
            IValidSealerStrategy validSealerStrategy,
            IBlockFinalizationManager finalizationManager, 
            BlockHeader parentHeader,
            ILogManager logManager,
            long startBlockNumber,
            bool forSealing = false) 
            : base(validatorContract, blockTree, receiptFinder, validatorStore, validSealerStrategy, finalizationManager, parentHeader, logManager, startBlockNumber, forSealing)
        {
            // TODO: Provide proper address
            ValidatorContract = reportingValidatorContract ?? throw new ArgumentNullException(nameof(reportingValidatorContract));
            _logger = logManager?.GetClassLogger<ReportingContractBasedValidator>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private ReportingValidatorContract ValidatorContract { get; }
        
        public void ReportMalicious(Address validator, long block, byte[] proof, IReportingValidator.MaliciousCause cause)
        {
            try
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Reporting malicious misbehaviour (cause: {cause}) at block #{block} from {validator}");
                }

                
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Validator {validator} could not be reported on block {block} with cause {cause}", e);
            }

        }

        public void ReportBenign(Address validator, long block, IReportingValidator.BenignCause cause)
        {
            try
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace($"Reporting benign misbehaviour (cause: {cause}) at block #{block} from {validator}");
                }

                
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Validator {validator} could not be reported on block {block} with cause {cause}", e);
            }
        }

        public void TryReportSkipped(BlockHeader header, BlockHeader parent)
        {
            var areThereSkipped = header.AuRaStep > parent.AuRaStep + 1;
            var firstBlock = header.Number == 1;
            if (areThereSkipped && !firstBlock)
            {
                if (_logger.IsDebug) _logger.Debug($"Author {header.Beneficiary} built block with step gap. current step: {header.Author}, parent step: {parent.AuRaStep}");
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
    }
}