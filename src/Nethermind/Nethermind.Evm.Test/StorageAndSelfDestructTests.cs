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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class StorageAndSelfDestructTests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.MuirGlacierBlockNumber;

        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [Test]
        public void Load_self_destruct()
        {
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree();
            
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
                        .PushData(21)
                        .Op(Instruction.JUMP)
                        .Op(Instruction.JUMPDEST)
                        .PushData(0)
                        .Op(Instruction.SELFDESTRUCT)
                        .Op(Instruction.JUMPDEST)
                        .Done)
                .Done;
            
            Address contractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

            byte[] byteCode1 = Prepare.EvmCode
                .Call(contractAddress, 100000)
                .Op(Instruction.STOP).Done;
            
            byte[] byteCode2 = Prepare.EvmCode
                .Call(contractAddress, 100000)
                .Op(Instruction.STOP).Done;

            long gasLimit = 1000000;

            EthereumEcdsa ecdsa = new EthereumEcdsa(1, LimboLogs.Instance);
            Transaction initTx = Build.A.Transaction.WithInit(initByteCode).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Transaction tx1 = Build.A.Transaction.WithInit(byteCode1).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithInit(byteCode2).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(initTx, tx1, tx2).WithGasLimit(2 * gasLimit).TestObject;
            
            ParityLikeTxTracer initTracer = new ParityLikeTxTracer(block, initTx, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(initTx, block.Header, initTracer);
            AssertStorage(new StorageCell(contractAddress, 1), 0);
            
            ParityLikeTxTracer tracer1 = new ParityLikeTxTracer(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, block.Header, tracer1);
            AssertStorage(new StorageCell(contractAddress, 1), 1);
            
            ParityLikeTxTracer tracer2 = new ParityLikeTxTracer(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, block.Header, tracer2);
            // AssertStorage(new StorageCell(contractAddress, 1), 1);
        }
    }
}