/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using Nethermind.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Logging;
using Nethermind.Mining;

namespace Nethermind.AuRa
{
    public class AuRaSealValidator : ISealValidator
    {
        private readonly AuRaParameters _parameters;
        private readonly IAuRaStepCalculator _stepCalculator;
        private readonly IAuRaValidator _validator;
        private readonly IEthereumEcdsa _ecdsa;
        private readonly ILogger _logger;
<<<<<<< HEAD
=======
        private readonly AuraDifficultyCalculator _auraDifficultyCalculator;
>>>>>>> test squash
        private readonly ReceivedSteps _receivedSteps = new ReceivedSteps();
        
        public AuRaSealValidator(AuRaParameters parameters, IAuRaStepCalculator stepCalculator, IAuRaValidator validator, IEthereumEcdsa ecdsa, ILogManager logManager)
        {
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _stepCalculator = stepCalculator ?? throw new ArgumentNullException(nameof(stepCalculator));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _ecdsa = ecdsa ?? throw new ArgumentNullException(nameof(ecdsa));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
<<<<<<< HEAD
=======
            _auraDifficultyCalculator = new AuraDifficultyCalculator(stepCalculator);
>>>>>>> test squash
        }

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
                return false;
            }
            else if (header.AuRaStep < parent.AuRaStep)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} step {header.AuRaStep} is lesser than parents step {parent.AuRaStep}.");
                return false;
            }

            var currentStep = _stepCalculator.CurrentStep;

            if (header.AuRaStep > currentStep + rejectedStepDrift)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} step {header.AuRaStep} is from the future. Current step is {currentStep}.");
                return false;
            }

            if (header.AuRaStep > currentStep)
            {
                if (_logger.IsWarn) _logger.Warn($"Block {header.Number}, hash {header.Hash} step {header.AuRaStep} is too early. Current step is {currentStep}.");
            }

            if (header.AuRaStep - parent.AuRaStep != 1)
            {
                // report_skipped
            }

            // Report malice if the validator produced other sibling blocks in the same step.
            if (_receivedSteps.ContainsOrInsert(header.AuRaStep.Value, header.Beneficiary, _validator.CurrentSealersCount))
            {
<<<<<<< HEAD
                if (_logger.IsDebug) _logger.Debug($"Validator {header.Beneficiary} produced sibling blocks in the same step {header.AuRaStep} in block {header.Number}.");
                // report malicious
            }
            
            if (header.Number >= _parameters.ValidateScoreTransition)
            {
                if (header.Difficulty >= AuraDifficultyCalculator.MaxDifficulty)
                {
                    if (_logger.IsError) _logger.Error($"Difficulty out of bounds for block {header.Number}, hash {header.Hash}, Max value {AuraDifficultyCalculator.MaxDifficulty}, but found {header.Difficulty}.");
                    return false;
                }

                var expectedDifficulty = AuraDifficultyCalculator.CalculateDifficulty(parent.AuRaStep.Value, header.AuRaStep.Value, 0);
=======
                if (_logger.IsWarn) _logger.Warn($"Validator {header.Beneficiary} produced sibling blocks in the same step {header.AuRaStep} in block {header.Number}.");
                // report malicious
            }
            
            if (_parameters.ValidateScoreTransition >= header.Number)
            {
                if (header.Difficulty >= _auraDifficultyCalculator.MaxDifficulty)
                {
                    if (_logger.IsError) _logger.Error($"Difficulty out of bounds for block {header.Number}, hash {header.Hash}, Max value {_auraDifficultyCalculator.MaxDifficulty}, but found {header.Difficulty}.");
                    return false;
                }

                var expectedDifficulty = _auraDifficultyCalculator.CalculateDifficulty(parent);
>>>>>>> test squash
                if (header.Difficulty != expectedDifficulty)
                {
                    if (_logger.IsError) _logger.Error($"Invalid difficulty for block {header.Number}, hash {header.Hash}, expected value {expectedDifficulty}, but found {header.Difficulty}.");
                    return false;                    
                }
            }

            return true;
        }

        public bool ValidateSeal(BlockHeader header)
        {
            if (header.IsGenesis) return true;

<<<<<<< HEAD
            header.Author ??= GetSealer(header);
=======
            header.Author ??= GetSealer(header); 
            
>>>>>>> test squash

            if (header.Author != header.Beneficiary)
            {
                if (_logger.IsError) _logger.Error($"Author {header.Beneficiary} of the block {header.Number}, hash {header.Hash} doesn't match signer {header.Author}.");
                return false;
            }
            
            // cannot call: _validator.IsValidSealer(header.Author); because we can call it only when previous step was processed.
            // this responsibility delegated to actual validator during processing 
            return true;
        }

        private Address GetSealer(BlockHeader header)
        {
            Signature signature = new Signature(header.AuRaSignature);
            signature.V += Signature.VOffset;
            Keccak message = BlockHeader.CalculateHash(header, RlpBehaviors.ForSealing);
            return _ecdsa.RecoverAddress(signature, message);
        }

        private class ReceivedSteps
        {
<<<<<<< HEAD
            private readonly List<(long Step, Address Author, ISet<Address> Authors)> _list = new List<(long Step, Address Author, ISet<Address> Authors)>();
=======
            private readonly List<(long Step, Address Author, ISet<Address> Authors)> _list = new List<(long, Address, ISet<Address>)>();
>>>>>>> test squash
            private const int CacheSizeFullRoundsMultiplier = 4;

            public bool ContainsOrInsert(long step, Address author, int validatorCount)
            {
                int index = BinarySearch(step);
                bool contains = index > 0;
                if (contains)
                {
                    var stepElement = _list[index];
                    contains = stepElement.Authors?.Contains(author) ?? stepElement.Author == author;
                    if (!contains)
                    {
                        stepElement.Author = null;
                        stepElement.Authors ??= new HashSet<Address>();
                        stepElement.Authors.Add(author);
                    }
                }
                else
                {
                    _list.Add((step, author, null));
                }
                
                ClearOldCache(step, validatorCount);

                return contains;
            }

            private int BinarySearch(long step) => _list.BinarySearch((step, null, null), StepElementComparer.Instance);

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
        
<<<<<<< HEAD
        private class StepElementComparer : IComparer<(long Step, Address Author, ISet<Address> Authors)>
        {
            public static readonly StepElementComparer Instance = new StepElementComparer();
            public int Compare((long Step, Address Author, ISet<Address> Authors) x, (long Step, Address Author, ISet<Address> Authors) y) => x.Step.CompareTo(y.Step);
=======
        private class StepElementComparer : IComparer<(long, Address, ISet<Address>)>
        {
            public static readonly StepElementComparer Instance = new StepElementComparer();
            public int Compare((long, Address, ISet<Address>) x, (long, Address, ISet<Address>) y) => x.CompareTo(y);
>>>>>>> test squash
        }
    }
}