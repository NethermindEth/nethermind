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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Specs.ChainSpecStyle;
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
        private readonly byte[] _rewardData = new byte[] {3, 4, 5};
        private Address _address10;
        private Address _address50;
        private Address _address150;

        [SetUp]
        public void SetUp()
        {
            _address10 = TestItem.AddressA;
            _address50 = TestItem.AddressB;
            _address150 = TestItem.AddressC;
            _auraParameters = new AuRaParameters
            {
                BlockRewardContractAddress = _address10,
                BlockRewardContractTransition = 10,
                BlockReward = 200,
            };

            _abiEncoder = Substitute.For<IAbiEncoder>();
            _transactionProcessor = Substitute.For<ITransactionProcessor>();
            
            _block = new Block( Build.A.BlockHeader.TestObject, new BlockBody());
            
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

        [Test]
        public void constructor_throws_ArgumentException_on_BlockRewardContractTransition_higher_than_BlockRewardContractTransitions()
        {
            _auraParameters.BlockRewardContractTransitions = new Dictionary<long, Address>()
            {
                {2, Address.FromNumber(2)}
            };
            
            Action action = () => new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            action.Should().Throw<ArgumentException>();
        }
        
        [TestCase(0, 200ul)]
        [TestCase(5, 200ul)]
        [TestCase(9, 200ul)]
        public void calculates_rewards_correctly_before_contract_transition(long blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);
            result.Should().BeEquivalentTo(new BlockReward(_block.Beneficiary, expectedReward, BlockRewardType.Block));
        }
        
        [TestCase(10, 100ul)]
        [TestCase(15, 150ul)]
        public void calculates_rewards_correctly_after_contract_transition(long blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            var expected = new BlockReward(_block.Beneficiary, expectedReward, BlockRewardType.Block);
            SetupBlockRewards(new Dictionary<Address, BlockReward[]>() {{_address10, new[] {expected}}});
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);            
            result.Should().BeEquivalentTo(expected);
        }

        public static IEnumerable SubsequentTransitionsTestCases
        {
            get
            {
                yield return new TestCaseData(10, 100ul, TestItem.AddressA);
                yield return new TestCaseData(50, 150ul, TestItem.AddressB);
                yield return new TestCaseData(150, 200ul, TestItem.AddressC);
            }
        }

        [TestCaseSource(nameof(SubsequentTransitionsTestCases))]
        public void calculates_rewards_correctly_after_subsequent_contract_transitions(long blockNumber, ulong expectedReward, Address address)
        {
            _auraParameters.BlockRewardContractTransitions = new Dictionary<long, Address>()
            {
                {50, _address50},
                {150, _address150}
            };
            _block.Header.Number = blockNumber;
            var expected = new BlockReward(_block.Beneficiary, expectedReward, BlockRewardType.Block);
            SetupBlockRewards(new Dictionary<Address, BlockReward[]>() {{address, new[] {expected}}});
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);            
            result.Should().BeEquivalentTo(expected);
        }
        
        [TestCase(10, 100ul)]
        [TestCase(15, 150ul)]
        public void calculates_rewards_correctly_for_ommers(long blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            _block.Body = new BlockBody(_block.Body.Transactions, new[]
            {
                 Build.A.BlockHeader.WithBeneficiary(TestItem.AddressB).WithNumber(blockNumber - 1).TestObject,
                 Build.A.BlockHeader.WithBeneficiary(TestItem.AddressD).WithNumber(blockNumber - 2).TestObject
            });
            
            var expected = new BlockReward[]
            {
                new BlockReward(_block.Beneficiary, expectedReward, BlockRewardType.Block),
                new BlockReward(_block.Body.Ommers[0].Beneficiary, expectedReward, BlockRewardType.Uncle),
                new BlockReward(_block.Body.Ommers[1].Beneficiary, expectedReward, BlockRewardType.Uncle),
            };
            
            SetupBlockRewards(new Dictionary<Address, BlockReward[]>() {{_address10, expected}});
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);            
            result.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void calculates_rewards_correctly_for_external_addresses()
        {
            _block.Header.Number = 10;
            _block.Body = new BlockBody(_block.Body.Transactions, new[]
            {
                 Build.A.BlockHeader.WithBeneficiary(TestItem.Addresses[0]).WithNumber(9).TestObject,
                 Build.A.BlockHeader.WithBeneficiary(TestItem.Addresses[1]).WithNumber(8).TestObject
            });
            
            var expected = new BlockReward[]
            {
                new BlockReward(TestItem.AddressA, 1, BlockRewardType.External),
                new BlockReward(TestItem.AddressB, 3, BlockRewardType.External),
                new BlockReward(TestItem.AddressC, 5, BlockRewardType.External),
                new BlockReward(TestItem.AddressD, 8, BlockRewardType.External),
            };
            
            SetupBlockRewards(new Dictionary<Address, BlockReward[]>() {{_address10, expected}});
            var calculator = new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            var result =  calculator.CalculateRewards(_block);            
            result.Should().BeEquivalentTo(expected);
        }
        
        private void SetupBlockRewards(IDictionary<Address, BlockReward[]> rewards)
        {
            _transactionProcessor.When(x => x.Execute(
                    Arg.Is<Transaction>(t => CheckTransaction(t, rewards.Keys, _rewardData)),
                    _block.Header,
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer)))
                .Do(args =>
                {
                    var recipient = args.Arg<Transaction>().To;
                    args.Arg<ITxTracer>().MarkAsSuccess(
                        recipient,
                        0,
                        SetupAbiAddresses(rewards[recipient]),
                        Array.Empty<LogEntry>());
                });
        }
        
        private bool CheckTransaction(Transaction t, ICollection<Address> addresses, byte[] transactionData) => 
            t.SenderAddress == Address.SystemUser 
            && (t.To == _auraParameters.BlockRewardContractAddress || addresses.Contains(t.To)) 
            && t.Data == transactionData;

        private byte[] SetupAbiAddresses(params BlockReward[] rewards)
        {
            byte[] data = rewards.Select(r => r.Address).SelectMany(a => a.Bytes).ToArray();

            _abiEncoder.Decode(
                AbiEncodingStyle.None,
                Arg.Is<AbiSignature>(s =>
                    s.Types.Length == 2
                    && s.Types[0] is AbiArray && ((AbiArray) s.Types[0]).ElementType is AbiAddress
                    && s.Types[1] is AbiArray && ((AbiArray) s.Types[1]).ElementType is AbiUInt),
                data).Returns(new object[] {rewards.Select(r => r.Address).ToArray(), rewards.Select(r => r.Value).ToArray()});

            return data;
        }
    }
}