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
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.ChainSpecStyle;
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
        private IDictionary<long, IAuRaValidator> _innerValidators;
        private Block _block;
        private IAuRaBlockFinalizationManager _finalizationManager;
        private IBlockTree _blockTree;
        private IValidatorStore _validatorStore;

        [SetUp]
        public void SetUp()
        {
            _validator = GetValidator(AuRaParameters.ValidatorType.List);
            _innerValidators = new SortedList<long, IAuRaValidator>();
            _factory = Substitute.For<IAuRaValidatorFactory>();
            _logManager = LimboLogs.Instance;
            _finalizationManager = Substitute.For<IAuRaBlockFinalizationManager>();
            _blockTree = Substitute.For<IBlockTree>();
            _validatorStore = Substitute.For<IValidatorStore>();
            _finalizationManager.LastFinalizedBlockLevel.Returns(0);
            
            _factory.CreateValidatorProcessor(default, default, default)
                .ReturnsForAnyArgs(x =>
                {
                    var innerValidator = Substitute.For<IAuRaValidator>();
                    _innerValidators[x.Arg<long?>() ?? 0] = innerValidator;
                    return innerValidator;
                });

            _block = new Block( Build.A.BlockHeader.WithNumber(1).TestObject, new BlockBody());
        }
        
[Test]
        public void throws_ArgumentNullException_on_empty_validator()
        {
            Action act = () => new MultiValidator(null, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void throws_ArgumentNullException_on_empty_validatorFactory()
        {
            Action act = () => new MultiValidator(_validator, null, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_logManager()
        {
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentException_on_wrong_validator_type()
        {
            _validator.ValidatorType = AuRaParameters.ValidatorType.Contract;
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void throws_ArgumentException_on_empty_inner_validators()
        {
            _validator.Validators.Clear();
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);            
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void creates_inner_validators()
        {
            _validator = GetValidator(AuRaParameters.ValidatorType.Contract);
            var validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            validator.SetFinalizationManager(_finalizationManager, null);

            foreach (var blockNumber in _validator.Validators.Keys.Skip(1))
            {
                _finalizationManager.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(
                    Build.A.BlockHeader.WithNumber(blockNumber + 1).TestObject, Build.A.BlockHeader.WithNumber(blockNumber).TestObject));
            }

            _innerValidators.Keys.Should().BeEquivalentTo(_validator.Validators.Keys.Select(x => x == 0 ? 1 : x + 2));
        }
        
        [TestCase(AuRaParameters.ValidatorType.Contract, 1)]
        [TestCase(AuRaParameters.ValidatorType.List, 0)]
        [TestCase(AuRaParameters.ValidatorType.ReportingContract, 2)]
        public void correctly_consecutively_calls_inner_validators(AuRaParameters.ValidatorType validatorType, int blocksToFinalization)
        {
            // Arrange
            _validator = GetValidator(validatorType);
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            var innerValidatorsFirstBlockCalls = GetInnerValidatorsFirstBlockCalls(_validator);
            var maxCalls = innerValidatorsFirstBlockCalls.Values.Max() + 10;
            
            // Act
            ProcessBlocks(maxCalls, validator, blocksToFinalization);

            // Assert
            var callCountPerValidator = innerValidatorsFirstBlockCalls.Zip(
                innerValidatorsFirstBlockCalls.Values.Skip(1).Union(new[] {maxCalls}), (b0, b1) => (int)(b1 - b0.Value))
                .ToArray();

            callCountPerValidator[0] += blocksToFinalization;
            callCountPerValidator[^1] -= blocksToFinalization;

            long GetFinalizedIndex(int j)
            {
                var finalizedIndex = innerValidatorsFirstBlockCalls.Values.ElementAt(j);
                return finalizedIndex == 1 ? finalizedIndex : finalizedIndex + blocksToFinalization;
            }

            EnsureInnerValidatorsCalled(i => (_innerValidators[GetFinalizedIndex(i)], callCountPerValidator[i]));
        }

        [Test]
        public void doesnt_call_inner_validators_before_start_block()
        {
            // Arrange
            _validator.Validators.Remove(0);
            var validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            
            // Act
            ProcessBlocks(_validator.Validators.Keys.Min(), validator, 1);

            // Assert
            EnsureInnerValidatorsCalled(i => (_innerValidators.ElementAt(i).Value, 0));
        }
        
        [TestCase(16L, ExpectedResult = 11)]
        [TestCase(21L, ExpectedResult = 21)]
        public long initializes_validator_when_producing_block(long blockNumber)
        {
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            _block.Header.Number = blockNumber;
            validator.OnBlockProcessingStart(_block, ProcessingOptions.ProducingBlock);
            _innerValidators.Count.Should().Be(2);
            return _innerValidators.Keys.Last();
        }
        
        [TestCase(16L, AuRaParameters.ValidatorType.List, true, ExpectedResult = 11)]
        [TestCase(21L, AuRaParameters.ValidatorType.List, false, ExpectedResult = 21)]
        [TestCase(16L, AuRaParameters.ValidatorType.Contract, true, ExpectedResult = 15)]
        [TestCase(23L, AuRaParameters.ValidatorType.Contract, true, ExpectedResult = 22)]
        [TestCase(16L, AuRaParameters.ValidatorType.Contract, false, ExpectedResult = 1)]
        [TestCase(21L, AuRaParameters.ValidatorType.Contract, false, ExpectedResult = 11)]
        public long initializes_validator_when_on_nonconsecutive_block(long blockNumber, AuRaParameters.ValidatorType validatorType, bool finalizedLastValidatorBlockLevel)
        {
            _validator = GetValidator(validatorType);
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _finalizationManager, default, _logManager);
            _validator.Validators.ToList().TryGetSearchedItem(in blockNumber, (l, pair) => l.CompareTo(pair.Key), out var validatorInfo);
            _finalizationManager.GetFinalizationLevel(validatorInfo.Key).Returns(finalizedLastValidatorBlockLevel ? blockNumber - 2 : (long?) null);
            _block.Header.Number = blockNumber;
            validator.OnBlockProcessingStart(_block);
            return _innerValidators.Keys.Last();
        }
        
        private void ProcessBlocks(long count, IAuRaValidator validator, int blocksToFinalization)
        {
            for (int i = 1; i < count; i++)
            {
                _block.Header.Number = i;
                validator.OnBlockProcessingStart(_block);
                validator.OnBlockProcessingEnd(_block, Array.Empty<TxReceipt>());

                var finalizedBlock = i - blocksToFinalization;
                if (finalizedBlock >= 1)
                {
                    _finalizationManager.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(
                        Build.A.BlockHeader.WithNumber(i).TestObject,
                        Build.A.BlockHeader.WithNumber(finalizedBlock).TestObject));
                }
            }
        }
        
        private void EnsureInnerValidatorsCalled(Func<int, (IAuRaValidator Validator, int calls)> getValidatorWithCallCount)
        {
            for (var i = 0; i < _innerValidators.Count; i++)
            {
                var (innerValidator, calls) = getValidatorWithCallCount(i);
                
                innerValidator.Received(calls).OnBlockProcessingStart(Arg.Any<Block>());
                innerValidator.Received(calls).OnBlockProcessingEnd(Arg.Any<Block>(),
                    Array.Empty<TxReceipt>());
            }
        }
        
        private Dictionary<AuRaParameters.Validator, long> GetInnerValidatorsFirstBlockCalls(AuRaParameters.Validator validator)
        {
            return validator.Validators.ToDictionary(x => x.Value, x => Math.Max(x.Key + 1, 1));
        }
        
        private static AuRaParameters.Validator GetValidator(AuRaParameters.ValidatorType validatorType)
        {
            return new AuRaParameters.Validator()
            {
                ValidatorType = AuRaParameters.ValidatorType.Multi,
                Validators = new SortedList<long, AuRaParameters.Validator>()
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
}
