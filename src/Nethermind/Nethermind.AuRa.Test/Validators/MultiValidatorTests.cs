// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class MultiValidatorTests
    {
        private AuRaParameters.Validator _validator;
        private IAuRaValidatorFactory _factory;
        private ILogManager _logManager;
        private IDictionary<ulong, IAuRaValidator> _innerValidators;
        private Block _block;
        private IAuRaBlockFinalizationManager _finalizationManager;
        private IBlockTree _blockTree;
        private IValidatorStore _validatorStore;

        [SetUp]
        public void SetUp()
        {
            _validator = GetValidator(AuRaParameters.ValidatorType.List);
            _innerValidators = new SortedList<ulong, IAuRaValidator>();
            _factory = Substitute.For<IAuRaValidatorFactory>();
            _logManager = LimboLogs.Instance;
            _finalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
            _blockTree = Substitute.For<IBlockTree>();
            _validatorStore = Substitute.For<IValidatorStore>();
            _finalizationManager.LastFinalizedBlockLevel.Returns(0UL);

            _factory.CreateValidatorProcessor(default, default, default)
                .ReturnsForAnyArgs(x =>
                {
                    IAuRaValidator innerValidator = Substitute.For<IAuRaValidator>();
                    _innerValidators[x.Arg<ulong?>() ?? 0UL] = innerValidator;
                    return innerValidator;
                });

            _block = new Block(Build.A.BlockHeader.WithNumber(1).TestObject, new BlockBody());
        }

        [TearDown]
        public void TearDown() => _finalizationManager?.Dispose();

        [Test]
        public void throws_ArgumentNullException_on_empty_validator()
        {
            Action act = () => new MultiValidator(null, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            Assert.That(act, Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_validatorFactory()
        {
            Action act = () => new MultiValidator(_validator, null, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            Assert.That(act, Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_logManager()
        {
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, null);
            Assert.That(act, Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void throws_ArgumentException_on_wrong_validator_type()
        {
            _validator.ValidatorType = AuRaParameters.ValidatorType.Contract;
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            Assert.That(act, Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void throws_ArgumentException_on_empty_inner_validators()
        {
            _validator.Validators.Clear();
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            Assert.That(act, Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void creates_inner_validators()
        {
            _validator = GetValidator(AuRaParameters.ValidatorType.Contract);
            MultiValidator validator = new(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            validator.SetFinalizationManager(_finalizationManager, null);

            foreach (ulong blockNumber in _validator.Validators.Keys.Skip(1))
            {
                _finalizationManager.BlocksFinalized += Raise.EventWith(new AuRaFinalizeEventArgs(
                    Build.A.BlockHeader.WithNumber(blockNumber + 1).TestObject, Build.A.BlockHeader.WithNumber(blockNumber).TestObject));
            }

            Assert.That(_innerValidators.Keys, Is.EquivalentTo(_validator.Validators.Keys.Select(static x => x == 0 ? 1 : x + 2)));
        }

        [TestCase(AuRaParameters.ValidatorType.Contract, 1)]
        [TestCase(AuRaParameters.ValidatorType.List, 0)]
        [TestCase(AuRaParameters.ValidatorType.ReportingContract, 2)]
        public void correctly_consecutively_calls_inner_validators(AuRaParameters.ValidatorType validatorType, int blocksToFinalization)
        {
            // Arrange
            _validator = GetValidator(validatorType);
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            Dictionary<AuRaParameters.Validator, ulong> innerValidatorsFirstBlockCalls = GetInnerValidatorsFirstBlockCalls(_validator);
            ulong maxCalls = innerValidatorsFirstBlockCalls.Values.Max() + 10;

            // Act
            ProcessBlocks(maxCalls, validator, blocksToFinalization);

            // Assert
            int[] callCountPerValidator = innerValidatorsFirstBlockCalls.Zip(
                innerValidatorsFirstBlockCalls.Values.Skip(1).Union(new[] { maxCalls }), (b0, b1) => (int)(b1 - b0.Value))
                .ToArray();

            callCountPerValidator[0] += blocksToFinalization;
            callCountPerValidator[^1] -= blocksToFinalization;

            ulong GetFinalizedIndex(int j)
            {
                // Safe: finalization indices are derived from block numbers, always non-negative.
                ulong finalizedIndex = innerValidatorsFirstBlockCalls.Values.ElementAt(j);
                return finalizedIndex == 1 ? finalizedIndex : finalizedIndex + (ulong)blocksToFinalization;
            }

            EnsureInnerValidatorsCalled(i => (_innerValidators[GetFinalizedIndex(i)], callCountPerValidator[i]));
        }

        [Test]
        public void does_not_call_inner_validators_before_start_block()
        {
            // Arrange
            _validator.Validators.Remove(0);
            MultiValidator validator = new(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);

            // Act
            ProcessBlocks(_validator.Validators.Keys.Min(), validator, 1);

            // Assert
            EnsureInnerValidatorsCalled(i => (_innerValidators.ElementAt(i).Value, 0));
        }

        [TestCase(16UL, ExpectedResult = 11UL)]
        [TestCase(21UL, ExpectedResult = 21UL)]
        public ulong initializes_validator_when_producing_block(ulong blockNumber)
        {
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            _block.Header.Number = blockNumber;
            validator.OnBlockProcessingStart(_block, ProcessingOptions.ProducingBlock);
            Assert.That(_innerValidators.Count, Is.EqualTo(2));
            return _innerValidators.Keys.Last();
        }

        [TestCase(16UL, AuRaParameters.ValidatorType.List, true, ExpectedResult = 11UL)]
        [TestCase(21UL, AuRaParameters.ValidatorType.List, false, ExpectedResult = 21UL)]
        [TestCase(16UL, AuRaParameters.ValidatorType.Contract, true, ExpectedResult = 15UL)]
        [TestCase(23UL, AuRaParameters.ValidatorType.Contract, true, ExpectedResult = 22UL)]
        [TestCase(16UL, AuRaParameters.ValidatorType.Contract, false, ExpectedResult = 1UL)]
        [TestCase(21UL, AuRaParameters.ValidatorType.Contract, false, ExpectedResult = 11UL)]
        public ulong initializes_validator_when_on_nonconsecutive_block(ulong blockNumber, AuRaParameters.ValidatorType validatorType, bool finalizedLastValidatorBlockLevel)
        {
            _validator = GetValidator(validatorType);
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            _validator.Validators.ToList().TryGetSearchedItem<KeyValuePair<ulong, AuRaParameters.Validator>, ulong>(
                in blockNumber,
                static (l, pair) => l.CompareTo(pair.Key),
                out KeyValuePair<ulong, AuRaParameters.Validator> validatorInfo);

            ulong? finalizationLevel = finalizedLastValidatorBlockLevel
                ? blockNumber - 2UL
                : null;
            _finalizationManager.GetFinalizationLevel(validatorInfo.Key)
                .Returns(finalizationLevel.HasValue ? finalizationLevel.Value : null);

            _block.Header.Number = blockNumber;
            validator.OnBlockProcessingStart(_block);
            return _innerValidators.Keys.Last();
        }

        private void ProcessBlocks(ulong count, IAuRaValidator validator, int blocksToFinalization)
        {
            for (ulong i = 1; i < count; i++)
            {
                _block.Header.Number = i;
                validator.OnBlockProcessingStart(_block);
                validator.OnBlockProcessingEnd(_block, []);

                // Guard against underflow: only raise finalization event once i has advanced
                // far enough that (i - blocksToFinalization) is a valid (>= 1) block number.
                // Safe: blocksToFinalization is small (0, 1, or 2) and i starts at 1.
                if (i >= (ulong)blocksToFinalization)
                {
                    ulong finalizedBlock = i - (ulong)blocksToFinalization;
                    if (finalizedBlock >= 1)
                    {
                        _finalizationManager.BlocksFinalized += Raise.EventWith(new AuRaFinalizeEventArgs(
                            Build.A.BlockHeader.WithNumber(i).TestObject,
                            Build.A.BlockHeader.WithNumber(finalizedBlock).TestObject));
                    }
                }
            }
        }

        private void EnsureInnerValidatorsCalled(Func<int, (IAuRaValidator Validator, int calls)> getValidatorWithCallCount)
        {
            for (int i = 0; i < _innerValidators.Count; i++)
            {
                (IAuRaValidator innerValidator, int calls) = getValidatorWithCallCount(i);

                innerValidator.Received(calls).OnBlockProcessingStart(Arg.Any<Block>());
                innerValidator.Received(calls).OnBlockProcessingEnd(Arg.Any<Block>(),
                    []);
            }
        }

        private Dictionary<AuRaParameters.Validator, ulong> GetInnerValidatorsFirstBlockCalls(
            AuRaParameters.Validator validator) =>
            validator.Validators.ToDictionary(static x => x.Value, static x => Math.Max(x.Key + 1, 1));

        private static AuRaParameters.Validator GetValidator(AuRaParameters.ValidatorType validatorType) =>
            new()
            {
                ValidatorType = AuRaParameters.ValidatorType.Multi,
                Validators = new SortedList<ulong, AuRaParameters.Validator>()
                {
                    {
                        0,
                        new AuRaParameters.Validator()
                        {
                            ValidatorType = validatorType,
                            Addresses = new[] {Address.FromNumber(0)}
                        }
                    },
                    {
                        10,
                        new AuRaParameters.Validator()
                        {
                            ValidatorType = validatorType,
                            Addresses = new[] {Address.FromNumber(10)}
                        }
                    },
                    {
                        20,
                        new AuRaParameters.Validator()
                        {
                            ValidatorType = validatorType,
                            Addresses = new[] {Address.FromNumber(20)}
                        }
                    },
                }
            };
    }
}
