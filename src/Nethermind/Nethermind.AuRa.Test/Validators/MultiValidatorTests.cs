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
using System.Linq;
using FluentAssertions;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Logging;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Validators
{
    public class MultiValidatorTests
    {
        private AuRaParameters.Validator _validator;
        private IAuRaAdditionalBlockProcessorFactory _factory;
        private ILogManager _logManager;
        private IDictionary<long, IAuRaValidatorProcessor> _innerValidators;
        private Block _block;
        private IBlockFinalizationManager _finalizationManager;
        private IBlockTree _blockTree;
        private IValidatorStore _validatorStore;

        [SetUp]
        public void SetUp()
        {
            _validator = GetValidator(AuRaParameters.ValidatorType.List);
            _innerValidators = new SortedList<long, IAuRaValidatorProcessor>();
            _factory = Substitute.For<IAuRaAdditionalBlockProcessorFactory>();
            _logManager = Substitute.For<ILogManager>();
            _finalizationManager = Substitute.For<IBlockFinalizationManager>();
            _blockTree = Substitute.For<IBlockTree>();
            _validatorStore = Substitute.For<IValidatorStore>();
            _finalizationManager.LastFinalizedBlockLevel.Returns(0);
            
            _factory.CreateValidatorProcessor(default, default)
                .ReturnsForAnyArgs(x =>
                {
                    var innerValidator = Substitute.For<IAuRaValidatorProcessor>();
                    _innerValidators[x.Arg<long?>() ?? 0] = innerValidator;
                    return innerValidator;
                });

            _block = new Block(Prepare.A.BlockHeader().WithNumber(1).TestObject, new BlockBody());
        }
        
[Test]
        public void throws_ArgumentNullException_on_empty_validator()
        {
            Action act = () => new MultiValidator(null, _factory, _blockTree, _validatorStore, _logManager);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void throws_ArgumentNullException_on_empty_validatorFactory()
        {
            Action act = () => new MultiValidator(_validator, null, _blockTree, _validatorStore, _logManager);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_logManager()
        {
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentException_on_wrong_validator_type()
        {
            _validator.ValidatorType = AuRaParameters.ValidatorType.Contract;
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _logManager);
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void throws_ArgumentException_on_empty_inner_validators()
        {
            _validator.Validators.Clear();
            Action act = () => new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _logManager);            
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void creates_inner_validators()
        {
            _validator = GetValidator(AuRaParameters.ValidatorType.Contract);
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _logManager);
            validator.SetFinalizationManager(_finalizationManager);

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
            IAuRaValidatorProcessor validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _logManager);
            var innerValidatorsFirstBlockCalls = GetInnerValidatorsFirstBlockCalls(_validator);
            var maxCalls = innerValidatorsFirstBlockCalls.Values.Max() + 10;
            validator.SetFinalizationManager(_finalizationManager);

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
            var validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _logManager);
            
            // Act
            ProcessBlocks(_validator.Validators.Keys.Min(), validator, 1);

            // Assert
            EnsureInnerValidatorsCalled(i => (_innerValidators.ElementAt(i).Value, 0));
        }
        
        [TestCase(16L, ExpectedResult = 11)]
        [TestCase(21L, ExpectedResult = 21)]
        public long initializes_validator_when_producing_block(long blockNumber)
        {
            IAuRaValidatorProcessor validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _logManager);
            _block.Number = blockNumber;
            validator.PreProcess(_block, ProcessingOptions.ProducingBlock);
            _innerValidators.Count.Should().Be(1);
            return _innerValidators.Keys.First();
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
            IAuRaValidatorProcessor validator = new MultiValidator(_validator, _factory, _blockTree, _validatorStore, _logManager);
            validator.SetFinalizationManager(_finalizationManager);
            var validatorBlockLevel = (blockNumber - 1)/10*10;
            _finalizationManager.GetFinalizedLevel(validatorBlockLevel).Returns(finalizedLastValidatorBlockLevel ? blockNumber - 2 : (long?) null);
            _block.Number = blockNumber;
            validator.PreProcess(_block);
            return _innerValidators.Keys.Last();
        }
        
        private void ProcessBlocks(long count, IAuRaValidatorProcessor validator, int blocksToFinalization)
        {
            for (int i = 1; i < count; i++)
            {
                _block.Number = i;
                validator.PreProcess(_block);
                validator.PostProcess(_block, Array.Empty<TxReceipt>());

                var finalizedBlock = i - blocksToFinalization;
                if (finalizedBlock >= 1)
                {
                    _finalizationManager.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(
                        Build.A.BlockHeader.WithNumber(i).TestObject,
                        Build.A.BlockHeader.WithNumber(finalizedBlock).TestObject));
                }
            }
        }
        
        private void EnsureInnerValidatorsCalled(Func<int, (IAuRaValidatorProcessor Validator, int calls)> getValidatorWithCallCount)
        {
            for (var i = 0; i < _innerValidators.Count; i++)
            {
                var (innerValidator, calls) = getValidatorWithCallCount(i);
                
                innerValidator.Received(calls).PreProcess(Arg.Any<Block>());
                innerValidator.Received(calls).PostProcess(Arg.Any<Block>(),
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