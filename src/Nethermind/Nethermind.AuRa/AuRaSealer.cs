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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Mining;
using Nethermind.Serialization.Rlp;
using Nethermind.Wallet;

namespace Nethermind.AuRa
{
    public class AuRaSealer : ISealer
    {
        private readonly IBlockTree _blockTree;
        private readonly IValidatorStore _validatorStore;
        private readonly IAuRaValidator _validator;
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private readonly Address _nodeAddress;
        private readonly IBasicWallet _wallet;
        private readonly IValidSealerStrategy _validSealerStrategy;
        private readonly ILogger _logger;
        
        public AuRaSealer(
            IBlockTree blockTree,
            IAuRaValidator validator,
            IValidatorStore validatorStore,
            IAuRaStepCalculator auRaStepCalculator,
            Address nodeAddress,
            IBasicWallet wallet,
            IValidSealerStrategy validSealerStrategy,
            ILogManager logManager)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _validatorStore = validatorStore ?? throw new ArgumentNullException(nameof(validatorStore));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _auRaStepCalculator = auRaStepCalculator ?? throw new ArgumentNullException(nameof(auRaStepCalculator));
            _nodeAddress = nodeAddress ?? throw new ArgumentNullException(nameof(nodeAddress));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
            _validSealerStrategy = validSealerStrategy ?? throw new ArgumentNullException(nameof(validSealerStrategy));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public Task<Block> SealBlock(Block block, CancellationToken cancellationToken)
        {
            Block sealedBlock = Seal(block);
            if (sealedBlock != null)
            {
                sealedBlock.Hash = sealedBlock.Header.CalculateHash();
            }

            return Task.FromResult(sealedBlock);
        }

        private Block Seal(Block block)
        {
            // Bail out if we're unauthorized to sign a block
            if (!CanSeal(block.Number, block.ParentHash))
            {
                if (_logger.IsInfo) _logger.Info($"Not authorized to seal the block {block.ToString(Block.Format.Short)}");
                return null;
            }

            var headerHash = block.Header.CalculateHash(RlpBehaviors.ForSealing);
            var signature = _wallet.Sign(headerHash, _nodeAddress);
            block.Header.AuRaSignature = signature.BytesWithRecovery;
            
            return block;
        }

        public bool CanSeal(long blockNumber, Keccak parentHash)
        {
            bool StepNotYetProduced(long step) => !_blockTree.Head.AuRaStep.HasValue
                ? throw new InvalidOperationException("Head block doesn't have AuRaStep specified.'")
                : _blockTree.Head.AuRaStep.Value < step;

            bool IsThisNodeTurn(long blockLevel, long step) => _validSealerStrategy.IsValidSealer(_validatorStore.GetValidators(), _nodeAddress, step);

            var currentStep = _auRaStepCalculator.CurrentStep;
            var stepNotYetProduced = StepNotYetProduced(currentStep);
            var isThisNodeTurn = IsThisNodeTurn(blockNumber, currentStep);
            if (isThisNodeTurn)
            {
                if (_logger.IsWarn && !stepNotYetProduced) _logger.Warn($"Cannot seal block {blockNumber}: AuRa step {currentStep} already produced.");
                else if (_logger.IsDebug && stepNotYetProduced) _logger.Debug($"Can seal block {blockNumber}: {_nodeAddress} is correct proposer of AuRa step {currentStep}.");
            }
            else if (_logger.IsDebug) _logger.Debug($"Skip seal block {blockNumber}: {_nodeAddress} is not proposer of AuRa step {currentStep}.");

            return stepNotYetProduced && isThisNodeTurn;
        }
    }
}