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
using System.Diagnostics;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Logging;

namespace Nethermind.AuRa.Validators
{
    public abstract class AuRaValidatorProcessorBase : IAuRaValidatorProcessor
    {
        private readonly IValidSealerStrategy _validSealerStrategy;
        private readonly ILogger _logger;

        protected AuRaValidatorProcessorBase(AuRaParameters.Validator validator, IValidSealerStrategy validSealerStrategy, ILogManager logManager)
        {
            if (validator == null) throw new ArgumentNullException(nameof(validator));
            _validSealerStrategy = validSealerStrategy ?? throw new ArgumentNullException(nameof(validSealerStrategy));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public Address[] Validators { get; protected set; }

        void IAuRaValidator.SetFinalizationManager(IBlockFinalizationManager finalizationManager, bool forProducing)
        {
            SetFinalizationManagerInternal(finalizationManager, forProducing);
        }

        protected virtual void SetFinalizationManagerInternal(IBlockFinalizationManager finalizationManager, in bool forSealing) { }

        public virtual void PreProcess(Block block, ProcessingOptions options = ProcessingOptions.None)
        {
            if (!options.IsProducingBlock())
            {
                var auRaStep = block.Header.AuRaStep.Value;
                if (!_validSealerStrategy.IsValidSealer(Validators, block.Beneficiary, auRaStep))
                {
                    if (_logger.IsError) _logger.Error($"Block from incorrect proposer at block {block.ToString(Block.Format.FullHashAndNumber)}, step {auRaStep} from author {block.Beneficiary}.");
                    throw new InvalidBlockException(block.Hash);
                }
            }
        }

        public virtual void PostProcess(Block block, TxReceipt[] receipts, ProcessingOptions options = ProcessingOptions.None) { }
    }
}