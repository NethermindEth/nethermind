// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
        /// <remarks>
        /// Forward sync recovers the sealers of a batch in parallel in <see cref="ValidateSeal"/> and checks the parameters
        /// of each header later, so the last recoveries are kept for a whole batch rather than for the last header only.
        /// A lookup compares the sealing hash with each kept entry, which costs far less than one recovery.
        /// </remarks>
        internal const int RecoveredSealersCapacity = 256;

        private readonly RecoveredSealer?[] _recoveredSealers = new RecoveredSealer?[RecoveredSealersCapacity];
        private int _nextRecoveredSealer;

        private IReportingValidator ReportingValidator => _reportingValidator.Value;

        public bool ValidateParams(BlockHeader parent, BlockHeader header, bool isUncle = false)
        {
            const long rejectedStepDrift = 4;

            if (header is not AuRaBlockHeader auraHeader)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} is not an AuRa header.");
                return false;
            }

            if (parent is not AuRaBlockHeader auraParent)
            {
                if (_logger.IsError) _logger.Error($"Parent block {parent.Number}, hash {parent.Hash} of {header.Number} is not an AuRa header.");
                return false;
            }

            if (auraHeader.AuRaSignature?.Length != Signature.Size)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} is missing signature or its length is invalid.");
                return false;
            }

            if (!IsSealedByBeneficiary(header))
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} is not signed by its beneficiary {header.Beneficiary}.");
                return false;
            }

            ulong step = auraHeader.AuRaStep;

            if (header.Number >= _parameters.ValidateScoreTransition)
            {
                UInt256 expectedDifficulty = AuraDifficultyCalculator.CalculateDifficulty(auraParent.AuRaStep, step, 0);
                if (header.Difficulty != expectedDifficulty)
                {
                    if (_logger.IsError) _logger.Error($"Invalid difficulty for block {header.Number}, hash {header.Hash}, expected value {expectedDifficulty}, but found {header.Difficulty}.");
                    return false;
                }
            }

            if (step == auraParent.AuRaStep)
            {
                if (_logger.IsWarn) _logger.Warn($"Multiple blocks proposed for step {step}. Block {header.Number}, hash {header.Hash} is duplicate.");
                ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, [], IReportingValidator.MaliciousCause.DuplicateStep);
                return false;
            }
            else if (step < auraParent.AuRaStep && header.Number >= _parameters.ValidateStepTransition)
            {
                if (_logger.IsError) _logger.Error($"Block {header.Number}, hash {header.Hash} step {step} is lesser than parents step {auraParent.AuRaStep}.");
                ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, [], IReportingValidator.MaliciousCause.DuplicateStep);
                return false;
            }

            bool validatorsKnown = TryGetValidators(parent, out Address[] validators);
            if (validatorsKnown && !_validSealerStrategy.IsValidSealer(validators, header.Beneficiary, step, out Address expectedAddress))
            {
                if (_logger.IsError) _logger.Error($"Proposed block is not valid {header.ToString(BlockHeader.Format.FullHashAndNumber)}. Incorrect proposer at step {step}, expected {expectedAddress}, but found {header.Beneficiary}.");
                return false;
            }

            ulong currentStep = _stepCalculator.CurrentStep;

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

            if (header.Number >= _parameters.ValidateScoreTransition && header.Difficulty >= AuraDifficultyCalculator.MaxDifficulty)
            {
                if (_logger.IsError) _logger.Error($"Difficulty out of bounds for block {header.Number}, hash {header.Hash}, Max value {AuraDifficultyCalculator.MaxDifficulty}, but found {header.Difficulty}.");
                return false;
            }

            if (validatorsKnown)
            {
                ReportingValidator.TryReportSkipped(header, parent);

                if (_receivedSteps.ContainsSiblingOrInsert(header, validators.Length))
                {
                    if (_logger.IsDebug) _logger.Debug($"Validator {header.Beneficiary} produced sibling blocks in the same step {step} in block {header.Number}.");
                    ReportingValidator.ReportMalicious(header.Beneficiary, header.Number, [], IReportingValidator.MaliciousCause.SiblingBlocksInSameStep);
                }
            }

            return true;
        }

        public bool ValidateSeal(BlockHeader header, bool force)
        {
            if (header.IsGenesis) return true;

            if (!IsSealedByBeneficiary(header))
            {
                if (_logger.IsError) _logger.Error($"Author {header.Beneficiary} of the block {header.Number}, hash {header.Hash} doesn't match its signer.");
                return false;
            }

            // cannot call: _validator.IsValidSealer(header.Author); because we can call it only when previous step was processed.
            // this responsibility delegated to actual validator during processing with AuRaValidator and IValidSealerStrategy
            return true;
        }

        private bool TryGetValidators(BlockHeader parent, out Address[] validators)
        {
            if (_parameters.ValidatorsJson?.List is { Length: > 0 } fixedValidators)
            {
                validators = fixedValidators;
                return true;
            }

            validators = [];
            BlockHeader? head = _blockTree.Head?.Header;
            if (head is null) return false;

            if (parent.Hash == head.Hash)
            {
                validators = _validatorStore.GetValidators();
                return true;
            }

            ValidatorInfo current = _validatorStore.GetValidatorsInfo();
            ulong quorum = current.Validators.MinSealersForFinalization();

            if (parent.Number > head.Number)
            {
                if (_validatorStore.PendingValidators is not null || parent.Number - head.Number >= quorum || HasValidatorTransition(head.Number, parent.Number)) return false;

                BlockHeader? ancestor = parent;
                while (ancestor is not null && ancestor.Number > head.Number)
                {
                    ancestor = _blockTree.FindParentHeader(ancestor, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
                }

                if (ancestor?.Hash != head.Hash) return false;

                validators = current.Validators;
                return validators.Length != 0;
            }

            BlockHeader? forkPoint = parent;
            ulong depth = 0;
            while (forkPoint is not null && !_blockTree.IsMainChain(forkPoint))
            {
                if (++depth >= quorum) return false;
                forkPoint = _blockTree.FindParentHeader(forkPoint, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            }

            if (forkPoint is null) return false;

            ValidatorInfo validatorInfo = _validatorStore.GetValidatorsInfo(Math.Max(forkPoint.Number, AuRaValidatorBase.DefaultStartBlockNumber) + 1);

            if (forkPoint.Hash != parent.Hash)
            {
                bool changesOnMainChain = validatorInfo.FinalizingBlockNumber != current.FinalizingBlockNumber || _validatorStore.PendingValidators is not null;
                bool canChangeOnSideBranch = parent.Number - forkPoint.Number >= validatorInfo.Validators.MinSealersForFinalization();
                if (changesOnMainChain || canChangeOnSideBranch || HasValidatorTransition(forkPoint.Number, parent.Number)) return false;
            }

            validators = validatorInfo.Validators;
            return validators.Length != 0;
        }

        private bool HasValidatorTransition(ulong after, ulong upTo)
        {
            if (_parameters.ValidatorsJson?.Multi is not { Count: > 0 } multi) return false;

            foreach (ulong transition in multi.Keys)
            {
                if (transition > after && transition <= upTo + 1) return true;
            }

            return false;
        }

        private bool IsSealedByBeneficiary(BlockHeader header) => GetSealer(header) == header.Beneficiary;

        private Address GetSealer(BlockHeader header)
        {
            AuRaBlockHeader auraHeader = header.RequireAuRa();
            ValueHash256 message = header.CalculateValueHash(RlpBehaviors.ForSealing);
            RecoveredSealer?[] recovered = _recoveredSealers;
            for (int i = 0; i < recovered.Length; i++)
            {
                RecoveredSealer? cached = Volatile.Read(ref recovered[i]);
                if (cached is not null && cached.Message == message && cached.Signature.AsSpan().SequenceEqual(auraHeader.AuRaSignature))
                {
                    return cached.Sealer;
                }
            }

            Signature signature = new(auraHeader.AuRaSignature);
            signature.V += Signature.VOffset;
            Address sealer = _ecdsa.RecoverAddress(signature, in message);
            int next = (int)((uint)Interlocked.Increment(ref _nextRecoveredSealer) % RecoveredSealersCapacity);
            Volatile.Write(ref recovered[next], new RecoveredSealer(message, auraHeader.AuRaSignature.AsSpan().ToArray(), sealer));
            return sealer;
        }

        private sealed record RecoveredSealer(ValueHash256 Message, byte[] Signature, Address Sealer);

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

            private class AuthorBlockForStep(in ulong step, ReceivedSteps.AuthorBlock? authorBlock)
            {
                public ulong Step { get; } = step;
                public AuthorBlock? AuthorBlock { get; set; } = authorBlock;
                public ISet<AuthorBlock> AuthorBlocks { get; set; }
            }

            private class StepElementComparer : IComparer<AuthorBlockForStep>
            {
                public static readonly StepElementComparer Instance = new();

                public int Compare(AuthorBlockForStep x, AuthorBlockForStep y) => x.Step.CompareTo(y.Step);
            }

            private readonly List<AuthorBlockForStep> _list
                = [];

            private const ulong CacheSizeFullRoundsMultiplier = 4;

            public bool ContainsSiblingOrInsert(BlockHeader header, int validatorCount)
            {
                using McsLock.Disposable _ = _lock.Acquire();

                ulong step = header.GetAuRaStepOrZero();
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
                    _list.Insert(~index, new AuthorBlockForStep(step, item));
                }

                ClearOldCache(step, validatorCount);

                return containsSibling;
            }

            private int BinarySearch(ulong step) => _list.BinarySearch(new AuthorBlockForStep(step, null), StepElementComparer.Instance);

            /// <summary>
            /// Remove hash records older than two full N of steps (picked as a reasonable trade-off between memory consumption and fault-tolerance).
            /// </summary>
            /// <param name="step"></param>
            /// <param name="validatorCount"></param>
            private void ClearOldCache(ulong step, int validatorCount)
            {
                ulong siblingMaliceDetectionPeriod = CacheSizeFullRoundsMultiplier * (ulong)validatorCount;
                ulong oldestStepToKeep = step.SaturatingSub(siblingMaliceDetectionPeriod);
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
