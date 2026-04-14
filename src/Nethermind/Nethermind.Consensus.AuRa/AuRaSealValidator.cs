// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa
{
    public class AuRaSealValidator(
        AuRaChainSpecEngineParameters parameters,
        IAuRaStepCalculator stepCalculator,
        IBlockTree blockTree,
        IValidatorStore validatorStore,
        IValidSealerStrategy validSealerStrategy,
        IEthereumEcdsa ecdsa,
        Lazy<IReportingValidator> reportingValidator,
        ILogManager logManager) : ISealValidator
    {
        private readonly AuRaChainSpecEngineParameters _parameters = parameters;
        private readonly IAuRaStepCalculator _stepCalculator = stepCalculator;
        private readonly IBlockTree _blockTree = blockTree;
        private readonly IValidatorStore _validatorStore = validatorStore;
        private readonly IValidSealerStrategy _validSealerStrategy = validSealerStrategy;
        private readonly IEthereumEcdsa _ecdsa = ecdsa;
        private readonly ILogger _logger = logManager?.GetClassLogger<AuRaSealValidator>() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly ReceivedSteps _receivedSteps = new();
        private readonly Lazy<IReportingValidator> _reportingValidator = reportingValidator;

        private IReportingValidator ReportingValidator => _reportingValidator.Value;

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
        {
            const long rejectedStepDrift = 4;

            if (header.AuRaSignature is null)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} is missing signature.");
                return false;
            }

            // Ensure header is from the step after parent.
            if (header.AuRaStep is null)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} is missing step value.");
                return false;
            }
            else
            {
                long step = header.AuRaStep.Value;

                if (step == parent.AuRaStep)
                {
                    if (_logger.IsWarn) _logger.Warn($"Multiple blocks proposed for step {step}. Block {header.Number}, hash {header.Hash} is duplicate.");
                    ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, [], IReportingValidator.MaliciousCause.DuplicateStep);
                    return false;
                }
                else if (step < parent.AuRaStep && header.Number >= _parameters.ValidateStepTransition)
                {
                    if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} step {step} is lesser than parents step {parent.AuRaStep}.");
                    ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, [], IReportingValidator.MaliciousCause.DuplicateStep);
                    return false;
                }

                // we can only validate if its correct proposer for step if parent was already processed as it can change validators
                // no worries we do this validation later before processing the block
                if (parent.Hash == _blockTree.Head?.Hash)
                {
                    if (!_validSealerStrategy.IsValidSealer(_validatorStore.GetValidators(), header.Beneficiary, step, out Address expectedAddress))
                    {
                        if (_logger.IsError) _logger.Error($"Proposed block is not valid {header.ToString(BlockHeader.Format.FullHashAndNumber)}. Incorrect proposer at step {step}, expected {expectedAddress}, but found {header.Beneficiary}.");
                        return false;
                    }
                }

                long currentStep = _stepCalculator.CurrentStep;

                if (step > currentStep + rejectedStepDrift)
                {
                    if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} step {step} is from the future. Current step is {currentStep}.");
                    ReportingValidator.ReportBenign(header.Beneficiary, header.Number, IReportingValidator.BenignCause.FutureBlock);
                    return false;
                }

                if (step > currentStep)
                {
                    const int blockTooEarlyWarningMillisecondThreshold = 1000;

                    TimeSpan timeToStep = _stepCalculator.TimeToStep(step);
                    if (timeToStep.TotalMilliseconds > blockTooEarlyWarningMillisecondThreshold)
                    {
                        if (_logger.IsWarn) _logger.Warn($"Block {header.Number}, hash {header.Hash} step {step} is {timeToStep:g} too early. Current step is {currentStep}.");
                    }
                }

                // if (!ValidateEmptySteps())
                // ReportBenign
                ReportingValidator.TryReportSkipped(header, parent);

                // Report malice if the validator produced other sibling blocks in the same step.
                if (_receivedSteps.ContainsSiblingOrInsert(header, _validatorStore.GetValidators().Length))
                {
                    if (_logger.IsDebug) _logger.Debug($"Validator {header.Beneficiary} produced sibling blocks in the same step {step} in block {header.Number}.");
                    ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, [], IReportingValidator.MaliciousCause.SiblingBlocksInSameStep);
                }

                if (header.Number >= _parameters.ValidateScoreTransition)
                {
                    if (header.Difficulty >= AuraDifficultyCalculator.MaxDifficulty)
                    {
                        if (_logger.IsError) _logger.Error($"Difficulty out of bounds for block {header.Number}, hash {header.Hash}, Max value {AuraDifficultyCalculator.MaxDifficulty}, but found {header.Difficulty}.");
                        return false;
                    }

                    UInt256 expectedDifficulty = AuraDifficultyCalculator.CalculateDifficulty(parent.AuRaStep.Value, step, 0);
                    if (header.Difficulty != expectedDifficulty)
                    {
                        if (_logger.IsError) _logger.Error($"Invalid difficulty for block {header.Number}, hash {header.Hash}, expected value {expectedDifficulty}, but found {header.Difficulty}.");
                        return false;
                    }
                }

                return true;
            }
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            if (header.IsGenesis) return true;

            Address author = GetSealer(header);

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
            Signature signature = new(header.AuRaSignature);
            signature.V += Signature.VOffset;
            ValueHash256 message = header.CalculateValueHash(RlpBehaviors.ForSealing);
            return _ecdsa.RecoverAddress(signature, in message);
        }

        private class ReceivedSteps
        {
            private McsLock _lock = new();

            private readonly struct AuthorBlock(Address author, Hash256 block) : IEquatable<AuthorBlock>
            {
                public Address Author { get; } = author;
                public Hash256 Block { get; } = block;

                public bool Equals(AuthorBlock other) => Equals(Author, other.Author) && Equals(Block, other.Block);
                public override bool Equals(object obj) => obj is AuthorBlock other && Equals(other);
                public override int GetHashCode() => HashCode.Combine(Author, Block);
                public static bool operator ==(AuthorBlock obj1, AuthorBlock obj2) => obj1.Equals(obj2);
                public static bool operator !=(AuthorBlock obj1, AuthorBlock obj2) => !obj1.Equals(obj2);
            }

            private class AuthorBlockForStep(in long step, ReceivedSteps.AuthorBlock? authorBlock)
            {
                public long Step { get; } = step;
                public AuthorBlock? AuthorBlock { get; set; } = authorBlock;
                public ISet<AuthorBlock> AuthorBlocks { get; set; }
            }

            private class StepElementComparer : IComparer<AuthorBlockForStep>
            {
                public static readonly StepElementComparer Instance = new();

                public int Compare(AuthorBlockForStep x, AuthorBlockForStep y) => x.Step.CompareTo(y.Step);
            }

            private readonly List<AuthorBlockForStep> _list
                = new();

            private const int CacheSizeFullRoundsMultiplier = 4;

            public bool ContainsSiblingOrInsert(BlockHeader header, int validatorCount)
            {
                using McsLock.Disposable _ = _lock.Acquire();

                long step = header.AuRaStep.Value;
                Address author = header.Beneficiary;
                Hash256 hash = header.Hash;
                int index = BinarySearch(step);
                bool contains = index >= 0;
                AuthorBlock item = new(author, hash);
                bool containsSibling = false;
                if (contains)
                {
                    AuthorBlockForStep stepElement = _list[index];
                    contains = stepElement.AuthorBlocks?.Contains(item) ?? stepElement.AuthorBlock == item;
                    if (!contains)
                    {
                        if (stepElement.AuthorBlocks is null)
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
                int siblingMaliceDetectionPeriod = CacheSizeFullRoundsMultiplier * validatorCount;
                long oldestStepToKeep = step - siblingMaliceDetectionPeriod;
                int index = BinarySearch(oldestStepToKeep);
                int positiveIndex = index >= 0 ? index : ~index;
                if (positiveIndex > 0)
                {
                    _list.RemoveRange(0, positiveIndex);
                }
            }
        }
    }
}
