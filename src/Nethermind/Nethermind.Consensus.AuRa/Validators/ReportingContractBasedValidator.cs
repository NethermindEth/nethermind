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
        
        public ReportingContractBasedValidator(AuRaParameters.Validator validator,
            IStateProvider stateProvider,
            IAbiEncoder abiEncoder,
            ITransactionProcessor transactionProcessor,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource,
            IBlockTree blockTree,
            IReceiptFinder receiptFinder,
            IValidatorStore validatorStore,
            IValidSealerStrategy validSealerStrategy,
            IBlockFinalizationManager finalizationManager, 
            BlockHeader parentHeader,
            ILogManager logManager,
            long startBlockNumber,
            bool forSealing = false) 
            : base(validator, stateProvider, abiEncoder, transactionProcessor, readOnlyTransactionProcessorSource, blockTree, receiptFinder, validatorStore, validSealerStrategy, finalizationManager, parentHeader, logManager, startBlockNumber, forSealing)
        {
            // TODO: Provide proper address
            ValidatorContract = new ReportingValidatorContract(transactionProcessor, abiEncoder, GetContractAddress(validator), Address.Zero);
            _logger = logManager?.GetClassLogger<ReportingContractBasedValidator>() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private ReportingValidatorContract ValidatorContract { get; }
        
        public void ReportMalicious(Address validator, long block, byte[] proof, IReportingValidator.Cause cause)
        {
            throw new System.NotImplementedException();
        }

        public void ReportBenign(Address validator, long block, IReportingValidator.Cause cause)
        {
            if (_logger.IsTrace)
            {
                _logger.Trace($"Reporting benign misbehaviour (cause: {cause}) at block #{block} from {validator}");
            }
        }

        public void ReportSkipped(BlockHeader header, BlockHeader parent)
        {
            if (header.AuRaStep > parent.AuRaStep + 1 && header.Number != 1)
            {
                if (_logger.IsDebug) _logger.Debug($"Author {header.Beneficiary} built block with step gap. current step: {header.Author}, parent step: {parent.AuRaStep}");
                for (long i = parent.AuRaStep.Value + 1; i < header.AuRaStep.Value; i++)
                {
                    
                }
            }

        }
    }
}