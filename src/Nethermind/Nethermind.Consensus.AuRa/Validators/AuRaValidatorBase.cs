// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
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
            if (!options.ContainsFlag(ProcessingOptions.ProducingBlock) && !block.IsGenesis)
            {
                var auRaStep = block.Header.AuRaStep.Value;
                if (!_validSealerStrategy.IsValidSealer(Validators, block.Beneficiary, auRaStep))
                {
                    if (_logger.IsError) _logger.Error($"Block from incorrect proposer at block {block.ToString(Block.Format.FullHashAndNumber)}, step {auRaStep} from author {block.Beneficiary}.");
                    this.GetReportingValidator().ReportBenign(block.Beneficiary, block.Number, IReportingValidator.BenignCause.IncorrectProposer);
                    throw new InvalidBlockException(block);
                }
            }
        }

        public virtual void OnBlockProcessingEnd(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None) { }
    }
}
