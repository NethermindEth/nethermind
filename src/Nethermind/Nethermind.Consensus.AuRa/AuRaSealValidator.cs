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
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaSealValidator : ISealValidator
    {
        private readonly AuRaParameters _parameters;
        private readonly IAuRaStepCalculator _stepCalculator;
        private readonly IValidatorStore _validatorStore;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ILogger _logger;
        private readonly ReceivedSteps _receivedSteps = new ReceivedSteps();
        
        public AuRaSealValidator(AuRaParameters parameters, IAuRaStepCalculator stepCalculator, IValidatorStore validatorStore, IEthereumEcdsa ecdsa, ILogManager logManager)
        {
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _stepCalculator = stepCalculator ?? throw new ArgumentNullException(nameof(stepCalculator));
            _validatorStore = validatorStore?? throw new ArgumentNullException(nameof(validatorStore));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _logger = logManager.GetClassLogger<AuRaSealValidator>() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        public IReportingValidator ReportingValidator { get; set; } = NullReportingValidator.Instance;

        public bool ValidateParams(BlockHeader parent, BlockHeader header)
        {
            const long rejectedStepDrift = 4;
            
            if (header.AuRaSignature == null)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} is missing signature.");
                return false;
            }

            // Ensure header is from the step after parent.
            if (header.AuRaStep == null)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} is missing step value.");
                return false;
            }
            else if (header.AuRaStep == parent.AuRaStep)
            {
                if (_logger.IsWarn) _logger.Warn($"Multiple blocks proposed for step {header.AuRaStep}. Block {header.Number}, hash {header.Hash} is duplicate.");
                ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, Bytes.Empty, IReportingValidator.MaliciousCause.DuplicateStep);
                return false;
            }
            else if (header.AuRaStep < parent.AuRaStep && header.Number >= _parameters.ValidateStepTransition)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} step {header.AuRaStep} is lesser than parents step {parent.AuRaStep}.");
                ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, Bytes.Empty, IReportingValidator.MaliciousCause.DuplicateStep);
                return false;
            }

            var currentStep = _stepCalculator.CurrentStep;

            if (header.AuRaStep > currentStep + rejectedStepDrift)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} step {header.AuRaStep} is from the future. Current step is {currentStep}.");
                ReportingValidator.ReportBenign(header.Beneficiary, header.Number, IReportingValidator.BenignCause.FutureBlock);
                return false;
            }

            if (header.AuRaStep > currentStep)
            {
                TimeSpan timeToStep = _stepCalculator.TimeToStep(header.AuRaStep.Value);
                if (timeToStep.TotalMilliseconds > 500)
                {
                    if (_logger.IsWarn) _logger.Warn($"Block {header.Number}, hash {header.Hash} step {header.AuRaStep} is {timeToStep:g} too early. Current step is {currentStep}.");
                }
            }
            
            // if (!ValidateEmptySteps())
            // ReportBenign
            ReportingValidator.TryReportSkipped(header, parent);
            
            // Report malice if the validator produced other sibling blocks in the same step.
            if (_receivedSteps.ContainsSiblingOrInsert(header, _validatorStore.GetValidators().Length))
            {
                if (_logger.IsDebug) _logger.Debug($"Validator {header.Beneficiary} produced sibling blocks in the same step {header.AuRaStep} in block {header.Number}.");
                ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, Bytes.Empty, IReportingValidator.MaliciousCause.SiblingBlocksInSameStep);
            }
            
            if (header.Number >= _parameters.ValidateScoreTransition)
            {
                if (header.Difficulty >= AuraDifficultyCalculator.MaxDifficulty)
                {
                    if (_logger.IsError) _logger.Error($"Difficulty out of bounds for block {header.Number}, hash {header.Hash}, Max value {AuraDifficultyCalculator.MaxDifficulty}, but found {header.Difficulty}.");
                    return false;
                }

                var expectedDifficulty = AuraDifficultyCalculator.CalculateDifficulty(parent.AuRaStep.Value, header.AuRaStep.Value, 0);
                if (header.Difficulty != expectedDifficulty)
                {
                    if (_logger.IsError) _logger.Error($"Invalid difficulty for block {header.Number}, hash {header.Hash}, expected value {expectedDifficulty}, but found {header.Difficulty}.");
                    return false;                    
                }
            }
            
            return true;
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            if (header.IsGenesis) return true;

            var author = GetSealer(header);

            if (author != header.Beneficiary)
            {
                if (_logger.IsError) _logger.Error($"Author {header.Beneficiary} of the block {header.Number}, hash {header.Hash} doesn't match signer {author}.");
                return false;
            }
            
            // cannot call: _validator.IsValidSealer(header.Author); because we can call it only when previous step was processed.
            // this responsibility delegated to actual validator during processing with AuRaValidator and IValidSealerStrategy
            return true;
        }

        private Address GetSealer(BlockHeader header)
        {
            Signature signature = new Signature(header.AuRaSignature);
            signature.V += Signature.VOffset;
            Keccak message = header.CalculateHash(RlpBehaviors.ForSealing);
            return _ecdsa.RecoverAddress(signature, message);
        }

        private class ReceivedSteps
        {
            private struct AuthorBlock : IEquatable<AuthorBlock>
            {
                public AuthorBlock(Address author, Keccak block)
                {
                    Author = author;
                    Block = block;
                }

                public Address Author { get; }
                public Keccak Block { get; }

                public bool Equals(AuthorBlock other) => Equals(Author, other.Author) && Equals(Block, other.Block);
                public override bool Equals(object obj) => obj is AuthorBlock other && Equals(other);
                public override int GetHashCode() => HashCode.Combine(Author, Block);
                public static bool operator==(AuthorBlock obj1, AuthorBlock obj2) => obj1.Equals(obj2);
                public static bool operator!=(AuthorBlock obj1, AuthorBlock obj2) => !obj1.Equals(obj2);
            }
            
            private class AuthorBlockForStep
            {
                public AuthorBlockForStep(in long step, AuthorBlock? authorBlock)
                {
                    Step = step;
                    AuthorBlock = authorBlock;
                }

                public long Step { get; }
                public AuthorBlock? AuthorBlock { get; set; }
                public ISet<AuthorBlock> AuthorBlocks { get; set; }
            }
            
            private class StepElementComparer : IComparer<AuthorBlockForStep>
            {
                public static readonly StepElementComparer Instance = new StepElementComparer();

                public int Compare(AuthorBlockForStep x, AuthorBlockForStep y)
                {
                    return x.Step.CompareTo(y.Step);
                }
            }
            
            private readonly List<AuthorBlockForStep> _list 
                = new List<AuthorBlockForStep>();
            
            private const int CacheSizeFullRoundsMultiplier = 4;

            public bool ContainsSiblingOrInsert(BlockHeader header, int validatorCount)
            {
                long step = header.AuRaStep.Value;
                Address author = header.Beneficiary;
                var hash = header.Hash;
                int index = BinarySearch(step);
                bool contains = index >= 0;
                var item = new AuthorBlock(author, hash);
                bool containsSibling = false;
                if (contains)
                {
                    var stepElement = _list[index];
                    contains = stepElement.AuthorBlocks?.Contains(item) ?? stepElement.AuthorBlock == item;
                    if (!contains)
                    {
                        if (stepElement.AuthorBlocks == null)
                        {
                            stepElement.AuthorBlocks = new HashSet<AuthorBlock>
                            {
                                stepElement.AuthorBlock.Value
                            };
                            
                            stepElement.AuthorBlock = null;
                        }

                        stepElement.AuthorBlocks.Add(item);
                        containsSibling = true;
                    }
                }
                else
                {
                    _list.Add(new AuthorBlockForStep(step, item));
                }
                
                ClearOldCache(step, validatorCount);

                return containsSibling;
            }

            private int BinarySearch(long step) => _list.BinarySearch(new AuthorBlockForStep(step, null), StepElementComparer.Instance);

            /// <summary>
            /// Remove hash records older than two full N of steps (picked as a reasonable trade-off between memory consumption and fault-tolerance).
            /// </summary>
            /// <param name="step"></param>
            /// <param name="validatorCount"></param>
            private void ClearOldCache(long step, int validatorCount)
            {
                var siblingMaliceDetectionPeriod = CacheSizeFullRoundsMultiplier * validatorCount;
                var oldestStepToKeep = step - siblingMaliceDetectionPeriod;
                var index = BinarySearch(oldestStepToKeep);
                var positiveIndex = index >= 0 ? index : ~index;
                if (positiveIndex > 0)
                {
                    _list.RemoveRange(0, positiveIndex);
                }
            }
        }
    }
}
