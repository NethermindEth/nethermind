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

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;
using FluentAssertions;
using Nethermind.Evm.Tracing;
using Nethermind.Core.Crypto;
using System;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip6780Tests : VirtualMachineTestsBase
    {

        protected override long BlockNumber => MainnetSpecProvider.GrayGlacierBlockNumber;
        protected override ulong Timestamp => MainnetSpecProvider.CancunBlockTimestamp;

        private byte[] _selfDestructCode;
        private Address _contractAddress;
        private byte[] _initCode;
        private long _gasLimit = 1000000;
        private EthereumEcdsa _ecdsa = new(1, LimboLogs.Instance);

        [SetUp]
        public override void Setup()
        {
            base.Setup();
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 1000.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);
            _selfDestructCode = Prepare.EvmCode
                .SELFDESTRUCT(TestItem.PrivateKeyB.Address)
                .Done;
            _contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            _initCode = Prepare.EvmCode
                .ForInitOf(_selfDestructCode)
                .Done;
        }

        [TestCase(0ul, false)]
        [TestCase(MainnetSpecProvider.CancunBlockTimestamp, true)]
        public void self_destruct_not_in_same_transaction(ulong timestamp, bool onlyOnSameTransaction)
        {
            byte[] contractCall = Prepare.EvmCode
                .Call(_contractAddress, 100000)
                .Op(Instruction.STOP).Done;
            AssertDestroyed();
            Transaction initTx = Build.A.Transaction.WithCode(_initCode).WithValue(99.Ether()).WithGasLimit(_gasLimit).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
            Transaction tx1 = Build.A.Transaction.WithCode(contractCall).WithGasLimit(_gasLimit).WithNonce(1).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(BlockNumber)
                .WithTimestamp(timestamp)
                .WithTransactions(initTx, tx1).WithGasLimit(2 * _gasLimit).TestObject;

            _processor.Execute(initTx, block.Header, NullTxTracer.Instance);
            UInt256 contractBalanceAfterInit = TestState.GetBalance(_contractAddress);
            _processor.Execute(tx1, block.Header, NullTxTracer.Instance);

            contractBalanceAfterInit.Should().Be(99.Ether());
            AssertSendAll();
            if (onlyOnSameTransaction)
                AssertNotDestroyed();
            else
                AssertDestroyed();
        }

        [Test]
        public void self_destruct_in_same_transaction()
        {
            byte[] salt = new UInt256(123).ToBigEndian();
            Address createTxAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            _contractAddress = ContractAddress.From(createTxAddress, salt, _initCode);
            byte[] tx1 = Prepare.EvmCode
                .Create2(_initCode, salt, 99.Ether())
                .Call(_contractAddress, 100000)
                .STOP()
                .Done;

            Transaction createTx = Build.A.Transaction.WithCode(tx1).WithValue(100.Ether()).WithGasLimit(_gasLimit).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(BlockNumber)
                .WithTimestamp(Timestamp)
                .WithTransactions(createTx).WithGasLimit(2 * _gasLimit).TestObject;

            _processor.Execute(createTx, block.Header, NullTxTracer.Instance);

            AssertDestroyed();
            AssertSendAll();
        }

        [Test]
        public void self_destruct_in_initcode_of_create_opcodes()
        {
            byte[] salt = new UInt256(123).ToBigEndian();
            Address createTxAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            _contractAddress = ContractAddress.From(createTxAddress, salt, _selfDestructCode);
            byte[] tx1 = Prepare.EvmCode
                .Create2(_selfDestructCode, salt, 99.Ether())
                .STOP()
                .Done;

            Transaction createTx = Build.A.Transaction.WithCode(tx1).WithValue(100.Ether()).WithGasLimit(_gasLimit).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(BlockNumber)
                .WithTimestamp(Timestamp)
                .WithTransactions(createTx).WithGasLimit(2 * _gasLimit).TestObject;

            _processor.Execute(createTx, block.Header, NullTxTracer.Instance);

            AssertDestroyed();
            AssertSendAll();
        }

        [Test]
        public void self_destruct_in_initcode_of_create_tx()
        {
            _initCode = Prepare.EvmCode
                .StoreDataInMemory(0, _selfDestructCode)
                .PushData(_selfDestructCode.Length)
                .PushData(0)
                .SELFDESTRUCT(TestItem.PrivateKeyB.Address)
                .Done;
            Transaction createTx = Build.A.Transaction.WithCode(_selfDestructCode).WithValue(99.Ether()).WithGasLimit(_gasLimit).SignedAndResolved(_ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(BlockNumber)
                .WithTimestamp(Timestamp)
                .WithTransactions(createTx).WithGasLimit(2 * _gasLimit).TestObject;

            _processor.Execute(createTx, block.Header, NullTxTracer.Instance);

            AssertDestroyed();
            AssertSendAll();
        }

        private void AssertNotDestroyed()
        {
            AssertCodeHash(_contractAddress, Keccak.Compute(_selfDestructCode.AsSpan()));
        }

        private void AssertDestroyed(Address address = null)
        {
            TestState.AccountExists(address ?? _contractAddress).Should().BeFalse();
        }

        private void AssertSendAll()
        {
            TestState.GetBalance(_contractAddress).Should().Be(0);
            TestState.GetBalance(TestItem.PrivateKeyB.Address).Should().Be(99.Ether());
        }

    }
}
