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
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
        private IStateProvider _stateProvider;

        [SetUp]
        public void SetUp()
        {
            _block = new Block(Build.A.BlockHeader.TestObject, new BlockBody());
            _transactionProcessor = Substitute.For<IReadOnlyTransactionProcessor>();
            _readOnlyTxProcessorSource = Substitute.For<IReadOnlyTxProcessorSource>();
            _readOnlyTxProcessorSource.Build(TestItem.KeccakA).Returns(_transactionProcessor);
            _stateProvider = Substitute.For<IStateProvider>();
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
            var expectation = new SystemTransaction()
            {
                Value = 0, 
                Data = new byte[] {0x75, 0x28, 0x62, 0x11},
                Hash = new Keccak("0x0652461cead47b6e1436fc631debe06bde8bcdd2dad3b9d21df5cf092078c6d3"), 
                To = _contractAddress,
                SenderAddress = Address.SystemUser,
                GasLimit = Blockchain.Contracts.CallableContract.UnlimitedGas,
                GasPrice = 0,
                Nonce = 0
            };
            expectation.Hash = expectation.CalculateHash();
            
            var contract = new ValidatorContract(
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
        
        private static bool IsEquivalentTo(object expected, object item)
        {
            try
            {
                item.Should().BeEquivalentTo(expected);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
