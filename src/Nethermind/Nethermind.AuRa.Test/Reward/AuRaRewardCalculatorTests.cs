// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.AuRa.Config;
using Nethermind.Consensus.AuRa.Rewards;
using Nethermind.Consensus.Rewards;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Reward
{
    public class AuRaRewardCalculatorTests
    {
        private AuRaChainSpecEngineParameters _auraParameters;
        private IAbiEncoder _abiEncoder;
        private ITransactionProcessor _transactionProcessor;
        private Block _block;
        private readonly byte[] _rewardData = { 3, 4, 5 };
        private Address _address10;
        private Address _address50;
        private Address _address150;

        [SetUp]
        public void SetUp()
        {
            _address10 = TestItem.AddressA;
            _address50 = TestItem.AddressB;
            _address150 = TestItem.AddressC;
            _auraParameters = new AuRaChainSpecEngineParameters()
            {
                BlockRewardContractAddress = _address10,
                BlockRewardContractTransition = 10,
                BlockReward = new SortedDictionary<ulong, UInt256>() { { 0, 200 } },
            };

            _abiEncoder = Substitute.For<IAbiEncoder>();
            _transactionProcessor = Substitute.For<ITransactionProcessor>();

            _block = new Block(Build.A.BlockHeader.TestObject, new BlockBody());

            _abiEncoder
                .Encode(AbiEncodingStyle.IncludeSignature, Arg.Is<AbiSignature>(static s => s.Name == "reward"), Arg.Any<object[]>())
                .Returns(_rewardData);
        }

        [Test]
        public void constructor_throws_ArgumentNullException_on_null_auraParameters()
        {
            Action action = () => new AuRaRewardCalculator(null, _abiEncoder, _transactionProcessor);
            Assert.That(action, Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void constructor_throws_ArgumentNullException_on_null_encoder()
        {
            Action action = () => new AuRaRewardCalculator(_auraParameters, null, _transactionProcessor);
            Assert.That(action, Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void constructor_throws_ArgumentNullException_on_null_transactionProcessor()
        {
            Action action = () => new AuRaRewardCalculator(_auraParameters, _abiEncoder, null);
            Assert.That(action, Throws.TypeOf<ArgumentNullException>());
        }

        [Test]
        public void constructor_throws_ArgumentException_on_BlockRewardContractTransition_higher_than_BlockRewardContractTransitions()
        {
            _auraParameters.BlockRewardContractTransitions = new Dictionary<ulong, Address>()
            {
                {2, Address.FromNumber(2)}
            };

            Action action = () => new AuRaRewardCalculator(_auraParameters, _abiEncoder, _transactionProcessor);
            Assert.That(action, Throws.TypeOf<ArgumentException>());
        }

        [TestCase(1ul, 200ul)]
        [TestCase(5ul, 200ul)]
        [TestCase(9ul, 200ul)]
        public void calculates_rewards_correctly_before_contract_transition(ulong blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            AuRaRewardCalculator calculator = new(_auraParameters, _abiEncoder, _transactionProcessor);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(new[] { new BlockReward(_block.Beneficiary, expectedReward) }).UsingPropertiesComparer());
        }

        [Test]
        public void calculates_rewards_correctly_for_genesis()
        {
            _block.Header.Number = 0;
            AuRaRewardCalculator calculator = new(_auraParameters, _abiEncoder, _transactionProcessor);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.Empty);
        }

        [TestCase(10ul, 100ul)]
        [TestCase(15ul, 150ul)]
        public void calculates_rewards_correctly_after_contract_transition(ulong blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            BlockReward expected = new(_block.Beneficiary, expectedReward, BlockRewardType.External);
            SetupBlockRewards(new Dictionary<Address, BlockReward[]>() { { _address10, new[] { expected } } });
            AuRaRewardCalculator calculator = new(_auraParameters, _abiEncoder, _transactionProcessor);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(new[] { expected }).UsingPropertiesComparer());
        }

        public static IEnumerable SubsequentTransitionsTestCases
        {
            get
            {
                yield return new TestCaseData(10UL, 100ul, TestItem.AddressA);
                yield return new TestCaseData(50UL, 150ul, TestItem.AddressB);
                yield return new TestCaseData(150UL, 200ul, TestItem.AddressC);
            }
        }

        [TestCaseSource(nameof(SubsequentTransitionsTestCases))]
        public void calculates_rewards_correctly_after_subsequent_contract_transitions(ulong blockNumber, ulong expectedReward, Address address)
        {
            _auraParameters.BlockRewardContractTransitions = new Dictionary<ulong, Address>()
            {
                {50, _address50},
                {150, _address150}
            };
            _block.Header.Number = blockNumber;
            BlockReward expected = new(_block.Beneficiary, expectedReward, BlockRewardType.External);
            SetupBlockRewards(new Dictionary<Address, BlockReward[]>() { { address, new[] { expected } } });
            AuRaRewardCalculator calculator = new(_auraParameters, _abiEncoder, _transactionProcessor);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(new[] { expected }).UsingPropertiesComparer());
        }

        [TestCase(10ul, 100ul)]
        [TestCase(15ul, 150ul)]
        public void calculates_rewards_correctly_for_uncles(ulong blockNumber, ulong expectedReward)
        {
            _block.Header.Number = blockNumber;
            _block = _block.WithReplacedBody(new BlockBody(_block.Body.Transactions, new[]
            {
                 Build.A.BlockHeader.WithBeneficiary(TestItem.AddressB).WithNumber(blockNumber - 1).TestObject,
                 Build.A.BlockHeader.WithBeneficiary(TestItem.AddressD).WithNumber(blockNumber - 2).TestObject
            }));

            BlockReward[] expected = {
                new(_block.Beneficiary, expectedReward, BlockRewardType.External),
                new(_block.Body.Uncles[0].Beneficiary, expectedReward, BlockRewardType.External),
                new(_block.Body.Uncles[1].Beneficiary, expectedReward, BlockRewardType.External),
            };

            SetupBlockRewards(new Dictionary<Address, BlockReward[]>() { { _address10, expected } });
            AuRaRewardCalculator calculator = new(_auraParameters, _abiEncoder, _transactionProcessor);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(expected).UsingPropertiesComparer());
        }

        [Test]
        public void calculates_rewards_correctly_for_external_addresses()
        {
            _block.Header.Number = 10;
            _block = _block.WithReplacedBody(new BlockBody(_block.Body.Transactions, new[]
            {
                 Build.A.BlockHeader.WithBeneficiary(TestItem.Addresses[0]).WithNumber(9).TestObject,
                 Build.A.BlockHeader.WithBeneficiary(TestItem.Addresses[1]).WithNumber(8).TestObject
            }));

            BlockReward[] expected = {
                new(TestItem.AddressA, 1, BlockRewardType.External),
                new(TestItem.AddressB, 3, BlockRewardType.External),
                new(TestItem.AddressC, 5, BlockRewardType.External),
                new(TestItem.AddressD, 8, BlockRewardType.External),
            };

            SetupBlockRewards(new Dictionary<Address, BlockReward[]>() { { _address10, expected } });
            AuRaRewardCalculator calculator = new(_auraParameters, _abiEncoder, _transactionProcessor);
            BlockReward[] result = calculator.CalculateRewards(_block);
            Assert.That(result, Is.EquivalentTo(expected).UsingPropertiesComparer());
        }

        private void SetupBlockRewards(IDictionary<Address, BlockReward[]> rewards) =>
            _transactionProcessor.When(x => x.Execute(
                    Arg.Is<Transaction>(t => CheckTransaction(t, rewards.Keys, _rewardData)),
                    Arg.Is<ITxTracer>(t => t is CallOutputTracer)))
                .Do(args =>
                {
                    Address? recipient = args.Arg<Transaction>().To;
                    args.Arg<ITxTracer>().MarkAsSuccess(
                        recipient,
                        0,
                        SetupAbiAddresses(rewards[recipient]),
                        []);
                });

        private bool CheckTransaction(Transaction t, ICollection<Address> addresses, byte[] transactionData) =>
            t.SenderAddress == Address.SystemUser
            && (t.To == _auraParameters.BlockRewardContractAddress || addresses.Contains(t.To))
            && t.Data.AsArray() == transactionData;

        private byte[] SetupAbiAddresses(params BlockReward[] rewards)
        {
            byte[] data = rewards.Select(static r => r.Address).SelectMany(static a => a.Bytes.ToArray()).ToArray();

            _abiEncoder.Decode(
                AbiEncodingStyle.None,
                Arg.Is<AbiSignature>(static s =>
                    s.Types.Length == 2
                    && s.Types[0] is AbiArray && ((AbiArray)s.Types[0]).ElementType is AbiAddress
                    && s.Types[1] is AbiArray && ((AbiArray)s.Types[1]).ElementType is AbiUInt),
                data).Returns(new object[] { rewards.Select(static r => r.Address).ToArray(), rewards.Select(static r => r.Value).ToArray() });

            return data;
        }
    }
}
