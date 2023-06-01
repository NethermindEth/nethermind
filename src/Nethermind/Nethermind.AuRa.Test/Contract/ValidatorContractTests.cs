// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Contract
{
    [TestFixture]
    public class ValidatorContractTests
    {
        private Block _block;
        private readonly Address _contractAddress = Address.FromNumber(long.MaxValue);
        private IReadOnlyTransactionProcessor _transactionProcessor;
        private IReadOnlyTxProcessorSource _readOnlyTxProcessorSource;
        private IWorldState _stateProvider;

        [SetUp]
        public void SetUp()
        {
            _block = new Block(Build.A.BlockHeader.TestObject, new BlockBody());
            _transactionProcessor = Substitute.For<IReadOnlyTransactionProcessor>();
            _readOnlyTxProcessorSource = Substitute.For<IReadOnlyTxProcessorSource>();
            _readOnlyTxProcessorSource.Build(TestItem.KeccakA).Returns(_transactionProcessor);
            _stateProvider = Substitute.For<IWorldState>();
            _stateProvider.StateRoot.Returns(TestItem.KeccakA);
        }

        [Test]
        public void constructor_throws_ArgumentNullException_on_null_contractAddress()
        {
            Action action =
                () => new ValidatorContract(
                    _transactionProcessor,
                    AbiEncoder.Instance,
                    null,
                    _stateProvider,
                    _readOnlyTxProcessorSource,
                    new Signer(0, TestItem.PrivateKeyD, LimboLogs.Instance));
            action.Should().Throw<ArgumentNullException>();
        }

        [Test]
        public void finalize_change_should_call_correct_transaction()
        {
            SystemTransaction expectation = new()
            {
                Value = 0,
                Data = new byte[] { 0x75, 0x28, 0x62, 0x11 },
                Hash = new Keccak("0x0652461cead47b6e1436fc631debe06bde8bcdd2dad3b9d21df5cf092078c6d3"),
                To = _contractAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = Blockchain.Contracts.CallableContract.UnlimitedGas,
                GasPrice = 0,
                Nonce = 0
            };
            expectation.Hash = expectation.CalculateHash();

            ValidatorContract contract = new(
                _transactionProcessor,
                AbiEncoder.Instance,
                _contractAddress,
                _stateProvider,
                _readOnlyTxProcessorSource,
                new Signer(0, TestItem.PrivateKeyD, LimboLogs.Instance));

            contract.FinalizeChange(_block.Header);

            _transactionProcessor.Received().Execute(
                Arg.Is<Transaction>(t => IsEquivalentTo(expectation, t)), _block.Header, Arg.Any<ITxTracer>());
        }

        private static bool IsEquivalentTo(Transaction expected, Transaction item)
        {
            try
            {
                item.EqualToTransaction(expected);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
