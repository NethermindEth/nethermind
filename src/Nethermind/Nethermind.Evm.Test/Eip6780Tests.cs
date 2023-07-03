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
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Specs;
using NUnit.Framework;
using FluentAssertions;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip6780Tests : VirtualMachineTestsBase
    {
        [TestCase(MainnetSpecProvider.GrayGlacierBlockNumber, 0ul, false)]
        [TestCase(MainnetSpecProvider.GrayGlacierBlockNumber, MainnetSpecProvider.CancunBlockTimestamp, true)]
        public void self_destruct_not_in_same_transaction(long blockNumber, ulong timestamp, bool onlyOnSameTransaction)
        {
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);

            Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            byte[] initByteCode = Prepare.EvmCode
                .ForInitOf(
                    Prepare.EvmCode
                        .PushData(1)
                        .Op(Instruction.SLOAD)
                        .PushData(1)
                        .Op(Instruction.EQ)
                        .PushData(17)
                        .Op(Instruction.JUMPI)
                        .PushData(1)
                        .PushData(1)
                        .Op(Instruction.SSTORE)
                        .PushData(40)
                        .Op(Instruction.JUMP)
                        .Op(Instruction.JUMPDEST)
                        .PushData(TestItem.PrivateKeyB.Address)
                        .Op(Instruction.SELFDESTRUCT)
                        .Op(Instruction.JUMPDEST)
                        .Done)
                .Done;

            byte[] byteCode1 = Prepare.EvmCode
                .Call(contractAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode2 = Prepare.EvmCode
                .Call(contractAddress, 100000)
                .Op(Instruction.STOP).Done;

            long gasLimit = 1000000;

            EthereumEcdsa ecdsa = new(1, LimboLogs.Instance);
            Transaction initTx = Build.A.Transaction.WithCode(initByteCode).WithValue(99.Ether()).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Transaction tx1 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithCode(byteCode2).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber)
                .WithTimestamp(timestamp)
                .WithTransactions(initTx, tx1, tx2).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer initTracer = new(block, initTx, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(initTx, block.Header, initTracer);
            AssertStorage(new StorageCell(contractAddress, 1), 0);
            TestState.GetBalance(contractAddress).Should().Be(99.Ether());

            ParityLikeTxTracer tracer1 = new(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, block.Header, tracer1);
            AssertStorage(new StorageCell(contractAddress, 1), 1);

            ParityLikeTxTracer tracer2 = new(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, block.Header, tracer2);

            uint expected = onlyOnSameTransaction ? 1u : 0u;
            AssertStorage(new StorageCell(contractAddress, 1), expected);

            TestState.GetBalance(contractAddress).Should().Be(0);
            TestState.GetBalance(TestItem.PrivateKeyB.Address).Should().Be(99.Ether());
        }

        [TestCase(MainnetSpecProvider.GrayGlacierBlockNumber, MainnetSpecProvider.CancunBlockTimestamp)]
        public void self_destruct_in_same_transaction(long blockNumber, ulong timestamp)
        {
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 1000.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);
            byte[] contractCode = Prepare.EvmCode
                        .PushData(1)
                        .Op(Instruction.SLOAD)
                        .PushData(1)
                        .Op(Instruction.EQ)
                        .PushData(17)
                        .Op(Instruction.JUMPI)
                        .PushData(1)
                        .PushData(1)
                        .Op(Instruction.SSTORE)
                        .PushData(40)
                        .Op(Instruction.JUMP)
                        .Op(Instruction.JUMPDEST)
                        .PushData(TestItem.PrivateKeyB.Address)
                        .Op(Instruction.SELFDESTRUCT)
                        .Op(Instruction.JUMPDEST)
                        .Done;
            byte[] initCode = Prepare.EvmCode.ForInitOf(contractCode).Done;
            byte[] salt = new UInt256(123).ToBigEndian();
            Address createTxAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            Address contractAddress = ContractAddress.From(createTxAddress, salt, initCode);
            byte[] tx1 = Prepare.EvmCode
                .Create2(initCode, salt, 99.Ether())
                .Call(contractAddress, 100000)
                .Call(contractAddress, 100000)
                .STOP()
                .Done;

            long gasLimit = 1000000;

            EthereumEcdsa ecdsa = new(1, LimboLogs.Instance);
            Transaction createTx = Build.A.Transaction.WithCode(tx1).WithValue(100.Ether()).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(blockNumber)
                .WithTimestamp(timestamp)
                .WithTransactions(createTx).WithGasLimit(2 * gasLimit).TestObject;

            _processor.Execute(createTx, block.Header, NullTxTracer.Instance);

            AssertStorage(new StorageCell(contractAddress, 1), 0);

            TestState.GetBalance(contractAddress).Should().Be(0);
            TestState.GetBalance(TestItem.PrivateKeyB.Address).Should().Be(99.Ether());
        }
    }
}
