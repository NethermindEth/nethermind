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
using System.Linq;
using FluentAssertions;
using Nethermind.AuRa.Validators;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpecStyle;
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

        [SetUp]
        public void SetUp()
        {
            _validator = GetValidator();
            _innerValidators = new SortedList<long, IAuRaValidatorProcessor>();
            _factory = Substitute.For<IAuRaAdditionalBlockProcessorFactory>();
            _logManager = Substitute.For<ILogManager>();
            _finalizationManager = Substitute.For<IBlockFinalizationManager>();
            _finalizationManager.LastFinalizedBlockLevel.Returns(-1);
            
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
            Action act = () => new MultiValidator(null, _factory, _logManager);
            act.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void throws_ArgumentNullException_on_empty_validatorFactory()
        {
            Action act = () => new MultiValidator(_validator, null, _logManager);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentNullException_on_empty_logManager()
        {
            Action act = () => new MultiValidator(_validator,_factory, null);
            act.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void throws_ArgumentException_on_wrong_validator_type()
        {
            _validator.ValidatorType = AuRaParameters.ValidatorType.Contract;
            Action act = () => new MultiValidator(_validator, _factory, _logManager);
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void throws_ArgumentException_on_empty_inner_validators()
        {
            _validator.Validators.Clear();
            Action act = () => new MultiValidator(_validator, _factory, _logManager);            
            act.Should().Throw<ArgumentException>();
        }
        
        [Test]
        public void creates_inner_validators()
        {
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _logManager);
            validator.SetFinalizationManager(_finalizationManager);

            foreach (var blockNumber in _validator.Validators.Keys)
            {
                _finalizationManager.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(
                    Build.A.BlockHeader.WithNumber(blockNumber + 1).TestObject, Build.A.BlockHeader.WithNumber(blockNumber).TestObject));
            }

            _innerValidators.Keys.Should().BeEquivalentTo(_validator.Validators.Keys.Select(x => x + 2));
        }
        
        [Test]
        public void correctly_consecutively_calls_inner_validators()
        {
            // Arrange
            var validator = new MultiValidator(_validator, _factory, _logManager);
            var innerValidatorsFirstBlockCalls = GetInnerValidatorsFirstBlockCalls(_validator);
            var maxCalls = innerValidatorsFirstBlockCalls.Max() + 10;
            
            // Act
            ProcessBlocks(maxCalls, validator);

            // Assert
            var callCountPerValidator = innerValidatorsFirstBlockCalls.Zip(
                innerValidatorsFirstBlockCalls.Skip(1).Union(new[] {maxCalls}), (b0, b1) => (int)(b1 - b0))
                .ToArray();
            
            EnsureInnerValidatorsCalled(i => (_innerValidators[innerValidatorsFirstBlockCalls[i]], callCountPerValidator[i]));
        }

        [Test]
        public void doesnt_call_inner_validators_before_start_block()
        {
            // Arrange
            _validator.Validators.Remove(0);
            var validator = new MultiValidator(_validator, _factory, _logManager);
            
            // Act
            ProcessBlocks(_validator.Validators.Keys.Min(), validator);

            // Assert
            EnsureInnerValidatorsCalled(i => (_innerValidators.ElementAt(i).Value, 0));
        }

        [Test]
        public void initializes_with_lastFinalizedBlockLevel()
        {
            _finalizationManager.LastFinalizedBlockLevel.Returns(_validator.Validators.Keys.Skip(1).First());
            IAuRaValidator validator = new MultiValidator(_validator, _factory, _logManager);
            validator.SetFinalizationManager(_finalizationManager);
            _innerValidators.Count.Should().Be(1);
        }
        
        private void ProcessBlocks(long count, MultiValidator validator)
        {
            for (int i = 1; i < count; i++)
            {
                _block.Number = i;
                validator.PreProcess(_block);
                validator.PostProcess(_block, Array.Empty<TxReceipt>());
                validator.IsValidSealer(Address.Zero);
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
                innerValidator.Received(calls).IsValidSealer(Address.Zero);
            }
        }
        
        private long[] GetInnerValidatorsFirstBlockCalls(AuRaParameters.Validator validator)
        {
            return validator.Validators.Keys.Select(x => Math.Max(x, 1)).OrderBy(k => k).ToArray();
        }
        
        private static AuRaParameters.Validator GetValidator()
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
                            ValidatorType = AuRaParameters.ValidatorType.List, 
                            Addresses = new[] {Address.FromNumber(0)}
                        }
                    },
                    {
                        10,
                        new AuRaParameters.Validator()
                        {
                            ValidatorType = AuRaParameters.ValidatorType.List, 
                            Addresses = new[] {Address.FromNumber(10)}
                        }
                    },
                    {
                        20,
                        new AuRaParameters.Validator()
                        {
                            ValidatorType = AuRaParameters.ValidatorType.List, 
                            Addresses = new[] {Address.FromNumber(20)}
                        }
                    },
                }
            };
        }
    }
}