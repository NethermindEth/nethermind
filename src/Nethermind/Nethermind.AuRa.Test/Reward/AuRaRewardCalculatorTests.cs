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
using System.Linq;
using System.Numerics;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.AuRa.Rewards;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs.ChainSpecStyle;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Reward
{
    public class AuRaRewardCalculatorTests
    {
        private AuRaParameters _auraParameters;
        private IAbiEncoder _abiEncoder;
        private ITransactionProcessor _transactionProcessor;
        private Block _block;
        private byte[] _rewardData = new byte[] {3, 4, 5};

        [SetUp]
        public void SetUp()
        {
            _auraParameters = new AuRaParameters
            {
                BlockRewardContractAddress = Address.FromNumber(100),
                BlockRewardContractTransition = 10,
                BlockReward = 200
            };

            _abiEncoder = Substitute.For<IAbiEncoder>();
            _transactionProcessor = Substitute.For<ITransactionProcessor>();
            
            _block = new Block(Prepare.A.BlockHeader().TestObject, new BlockBody());
            
            _abiEncoder
                .Encode(AbiEncodingStyle.IncludeSignature, Arg.Is<AbiSignature>(s => s.Name == "reward"), Arg.Any<object[]>())
                .Returns(_rewardData);
        }
        
        [Test]
        public void constructor_throws_ArgumentNullException_on_null_auraParameters()
        {
            Action action = () => new AuRaRewardCalculator(null, _abiEncoder, _transactionProcessor);
            action.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void constructor_throws_ArgumentNullException_on_null_encoder()
        {
            Action action = () => new AuRaRewardCalculator(_auraParameters, null, _transactionProcessor);
            action.Should().Throw<ArgumentNullException>();
        }
        
        [Test]
        public void constructor_throws_ArgumentNullException_on_null_transactionProcessor()
        {
            Action action = () => new AuRaRewardCalculator(_auraParameters, _abiEncoder, null);
            action.Should().Throw<ArgumentNullException>();
        }
        
        [TestCase(0, 200)]
        [TestCase(5, 200)]
        [TestCase(9, 200)]
        public void calculates_rewards_correctly_before_contract_transition(long blockNumber, long expectedReward)
        {
            _block.Number = blockNumber;
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward, BlockRewardType.Block));
        }
        
        [TestCase(10, 100)]
        [TestCase(15, 150)]
        public void calculates_rewards_correctly_after_contract_transition(long blockNumber, long expectedReward)
        {
            _block.Number = blockNumber;
            var expected = new BlockReward(_block.Beneficiary, expectedReward, BlockRewardType.Block);
            SetupBlockRewards(expected);
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);            
            result.Should().BeEquivalentTo(expected);
        }
        
        [TestCase(10, 100)]
        [TestCase(15, 150)]
        public void calculates_rewards_correctly_for_ommers(long blockNumber, long expectedReward)
        {
            _block.Number = blockNumber;
            _block.Body.Ommers = new[]
            {
                Prepare.A.BlockHeader().WithBeneficiary(Address.FromNumber(777)).WithNumber(blockNumber - 1).TestObject,
                Prepare.A.BlockHeader().WithBeneficiary(Address.FromNumber(888)).WithNumber(blockNumber - 2).TestObject
            };
            
            var expected = new BlockReward[]
            {
                new BlockReward(_block.Beneficiary, expectedReward, BlockRewardType.Block),
                new BlockReward(_block.Body.Ommers[0].Beneficiary, expectedReward, BlockRewardType.Uncle),
                new BlockReward(_block.Body.Ommers[1].Beneficiary, expectedReward, BlockRewardType.Uncle),
            };
            
            SetupBlockRewards(expected);
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);            
            result.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void calculates_rewards_correctly_for_external_addresses()
        {
            _block.Number = 10;
            _block.Body.Ommers = new[]
            {
                Prepare.A.BlockHeader().WithBeneficiary(Address.FromNumber(777)).WithNumber(9).TestObject,
                Prepare.A.BlockHeader().WithBeneficiary(Address.FromNumber(888)).WithNumber(8).TestObject
            };
            
            var expected = new BlockReward[]
            {
                new BlockReward(TestItem.AddressA, 1, BlockRewardType.External),
                new BlockReward(TestItem.AddressB, 3, BlockRewardType.External),
                new BlockReward(TestItem.AddressC, 5, BlockRewardType.External),
                new BlockReward(TestItem.AddressD, 8, BlockRewardType.External),
            };
            
            SetupBlockRewards(expected);
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);            
            result.Should().BeEquivalentTo(expected);
        }
        
        private void SetupBlockRewards(params BlockReward[] rewards)
        {
            _transactionProcessor.When(x => x.Execute(
                    Arg.Is<Transaction>(t => CheckTransaction(t, _rewardData)),
                    _block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer)))
                .Do(args =>
                    args.Arg<ITxTracer>().MarkAsSuccess(
                        args.Arg<Transaction>().To,
                        0,
                        SetupAbiAddresses(rewards),
                        Array.Empty<LogEntry>()));
        }
        
        private bool CheckTransaction(Transaction t, byte[] transactionData)
        {
            return t.SenderAddress == Address.SystemUser && t.To == _auraParameters.BlockRewardContractAddress && t.Data == transactionData;
        }

        private byte[] SetupAbiAddresses(BlockReward[] rewards)
        {
            byte[] data = rewards.Select(r => r.Address).SelectMany(a => a.Bytes).ToArray();

            _abiEncoder.Decode(
                AbiEncodingStyle.None,
                Arg.Is<AbiSignature>(s => s.Types.Length == 2 && s.Types[0].CSharpType == typeof(Address[]) && s.Types[1].CSharpType == typeof(BigInteger[])),
                data).Returns(new object[] {rewards.Select(r => r.Address).ToArray(), rewards.Select(r => r.Value).ToArray()});

            return data;
        }
    }
}