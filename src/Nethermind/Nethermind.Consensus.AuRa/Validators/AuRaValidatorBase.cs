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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa.Validators
{
    public abstract class AuRaValidatorBase : IAuRaValidator
    {
        public const long DefaultStartBlockNumber = 1;
        
        private readonly IValidSealerStrategy _validSealerStrategy;
        private readonly ILogger _logger;
        
        protected AuRaValidatorBase(
            IValidSealerStrategy validSealerStrategy,
            IValidatorStore validatorStore,
            ILogManager logManager,
            long startBlockNumber,
            bool forSealing)
        {
            ValidatorStore = validatorStore ?? throw new ArgumentNullException(nameof(validatorStore));
            _validSealerStrategy = validSealerStrategy ?? throw new ArgumentNullException(nameof(validSealerStrategy));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            InitBlockNumber = startBlockNumber;
            ForSealing = forSealing;
        }
        
        public Address[] Validators { get; protected internal set; }
        
        protected long InitBlockNumber { get; }
        protected internal bool ForSealing { get; }
        protected IValidatorStore ValidatorStore { get; }

        protected void InitValidatorStore()
        {
            if (!ForSealing && InitBlockNumber == DefaultStartBlockNumber)
            {
                ValidatorStore.SetValidators(InitBlockNumber, Validators);
            }
        }

        public virtual void OnBlockProcessingStart(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            if (!options.IsProducingBlock() && !block.IsGenesis)
            {
                var auRaStep = block.Header.AuRaStep.Value;
                if (!_validSealerStrategy.IsValidSealer(Validators, block.Beneficiary, auRaStep))
                {
                    if (_logger.IsError) _logger.Error($"Block from incorrect proposer at block {block.ToString(Block.Format.FullHashAndNumber)}, step {auRaStep} from author {block.Beneficiary}.");
                    this.GetReportingValidator().ReportBenign(block.Beneficiary, block.Number, IReportingValidator.BenignCause.IncorrectProposer);
                    throw new InvalidBlockException(block.Hash);
                }
            }
        }

        public virtual void OnBlockProcessingEnd(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None) { }
    }
}
