﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
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
            AssertStorage(new StorageCell(contractAddress, 1), 0);
        }

        [Test]
        public void Destroy_restore_store()
        {
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree();

            byte[] baseInitCodeStore = Prepare.EvmCode
                .PushData(2)
                .PushData(2)
                .Op(Instruction.SSTORE).Done;

            byte[] baseInitCodeAfterStore = Prepare.EvmCode
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

            byte[] baseInitCode = Bytes.Concat(baseInitCodeStore, baseInitCodeAfterStore);

            byte[] create2Code = Prepare.EvmCode
                .ForCreate2Of(baseInitCode)
                .Done;

            byte[] initOfCreate2Code = Prepare.EvmCode
                .ForInitOf(create2Code)
                .Done;

            Address deployingContractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            Address deploymentAddress = ContractAddress.From(deployingContractAddress, new byte[32], baseInitCode);

            byte[] deploy = Prepare.EvmCode
                .Call(deployingContractAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode1 = Prepare.EvmCode
                .Call(deploymentAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode2 = Prepare.EvmCode
                .Call(deploymentAddress, 100000)
                .Op(Instruction.STOP).Done;

            long gasLimit = 1000000;

            EthereumEcdsa ecdsa = new EthereumEcdsa(1, LimboLogs.Instance);
            // deploy create 2
            Transaction tx0 = Build.A.Transaction.WithInit(initOfCreate2Code).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // invoke create 2 to deploy contract
            Transaction tx1 = Build.A.Transaction.WithInit(deploy).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call contract once
            Transaction tx2 = Build.A.Transaction.WithInit(byteCode1).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // self destruct contract
            Transaction tx3 = Build.A.Transaction.WithInit(byteCode2).WithGasLimit(gasLimit).WithNonce(3).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // deploy again using create2
            Transaction tx4 = Build.A.Transaction.WithInit(deploy).WithGasLimit(gasLimit).WithNonce(4).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call newly deployed once
            Transaction tx5 = Build.A.Transaction.WithInit(byteCode1).WithGasLimit(gasLimit).WithNonce(5).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx0, tx1, tx2, tx3, tx4, tx5).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer tracer0 = new ParityLikeTxTracer(block, tx0, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx0, block.Header, tracer0);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);

            ParityLikeTxTracer tracer = new ParityLikeTxTracer(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);

            tracer = new ParityLikeTxTracer(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 1);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);

            tracer = new ParityLikeTxTracer(block, tx3, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx3, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 0);

            tracer = new ParityLikeTxTracer(block, tx4, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx4, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);

            tracer = new ParityLikeTxTracer(block, tx5, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx5, block.Header, tracer);
            AssertStorage(new StorageCell(deploymentAddress, 1), 1);
            AssertStorage(new StorageCell(deploymentAddress, 2), 2);
        }

        [Test]
        public void Destroy_restore_store_different_cells()
        {
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree();

            byte[] baseInitCodeStore = Prepare.EvmCode
                .PushData(2)
                .Op(Instruction.CALLVALUE)
                .Op(Instruction.SSTORE).Done;

            byte[] baseInitCodeAfterStore = Prepare.EvmCode
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

            byte[] baseInitCode = Bytes.Concat(baseInitCodeStore, baseInitCodeAfterStore);

            byte[] create2Code = Prepare.EvmCode
                .ForCreate2Of(baseInitCode)
                .Done;

            byte[] initOfCreate2Code = Prepare.EvmCode
                .ForInitOf(create2Code)
                .Done;

            Address deployingContractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            Address deploymentAddress = ContractAddress.From(deployingContractAddress, new byte[32], baseInitCode);
            
            byte[] deploy = Prepare.EvmCode
                .CallWithValue(deployingContractAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode1 = Prepare.EvmCode
                .CallWithValue(deploymentAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode2 = Prepare.EvmCode
                .CallWithValue(deploymentAddress, 100000)
                .Op(Instruction.STOP).Done;

            long gasLimit = 1000000;

            EthereumEcdsa ecdsa = new EthereumEcdsa(1, LimboLogs.Instance);
            // deploy create 2
            Transaction tx0 = Build.A.Transaction.WithInit(initOfCreate2Code).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // invoke create 2 to deploy contract
            Transaction tx1 = Build.A.Transaction.WithValue(2).WithInit(deploy).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call contract once
            Transaction tx2 = Build.A.Transaction.WithInit(byteCode1).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // self destruct contract
            Transaction tx3 = Build.A.Transaction.WithInit(byteCode2).WithGasLimit(gasLimit).WithNonce(3).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // deploy again using create2
            Transaction tx4 = Build.A.Transaction.WithValue(3).WithInit(deploy).WithGasLimit(gasLimit).WithNonce(4).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call newly deployed once
            Transaction tx5 = Build.A.Transaction.WithInit(byteCode1).WithGasLimit(gasLimit).WithNonce(5).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx0, tx1, tx2, tx3, tx4, tx5).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer tracer0 = new ParityLikeTxTracer(block, tx0, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx0, block.Header, tracer0);
            AssertStorage(new StorageCell(deploymentAddress, 1), 0);

            ParityLikeTxTracer tracer = new ParityLikeTxTracer(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);

            tracer = new ParityLikeTxTracer(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 1);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);
            // AssertStorage(new StorageCell(deploymentAddress, 3), 0);

            tracer = new ParityLikeTxTracer(block, tx3, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx3, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 3), 0);

            tracer = new ParityLikeTxTracer(block, tx4, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx4, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 3), 2);

            tracer = new ParityLikeTxTracer(block, tx5, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx5, block.Header, tracer);
            AssertStorage(new StorageCell(deploymentAddress, 1), 1);
            AssertStorage(new StorageCell(deploymentAddress, 2), 0);
            AssertStorage(new StorageCell(deploymentAddress, 3), 2);
        }
        
         [Test]
        public void Destroy_restore_store_different_cells_previously_existing()
        {
            byte[] baseInitCodeStore = Prepare.EvmCode
                .PushData(2)
                .Op(Instruction.CALLVALUE)
                .Op(Instruction.SSTORE).Done;

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
                .PushData(21)
                .Op(Instruction.JUMP)
                .Op(Instruction.JUMPDEST)
                .PushData(0)
                .Op(Instruction.SELFDESTRUCT)
                .Op(Instruction.JUMPDEST)
                .Done;
            
            byte[] baseInitCodeAfterStore = Prepare.EvmCode
                .ForInitOf(contractCode)
                .Done;

            byte[] baseInitCode = Bytes.Concat(baseInitCodeStore, baseInitCodeAfterStore);

            byte[] create2Code = Prepare.EvmCode
                .ForCreate2Of(baseInitCode)
                .Done;

            byte[] initOfCreate2Code = Prepare.EvmCode
                .ForInitOf(create2Code)
                .Done;

            Address deployingContractAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
            Address deploymentAddress = ContractAddress.From(deployingContractAddress, new byte[32], baseInitCode);
            
            byte[] deploy = Prepare.EvmCode
                .CallWithValue(deployingContractAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode1 = Prepare.EvmCode
                .CallWithValue(deploymentAddress, 100000)
                .Op(Instruction.STOP).Done;

            byte[] byteCode2 = Prepare.EvmCode
                .CallWithValue(deploymentAddress, 100000)
                .Op(Instruction.STOP).Done;

            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree();
            
            TestState.CreateAccount(deploymentAddress, UInt256.One);
            Keccak codeHash = TestState.UpdateCode(contractCode);
            TestState.UpdateCodeHash(deploymentAddress, codeHash, MuirGlacier.Instance);
            
            Storage.Set(new StorageCell(deploymentAddress, 7), new byte[] {7});
            Storage.Commit();
            Storage.CommitTrees();
            TestState.Commit(MuirGlacier.Instance);
            TestState.CommitTree();
            
            long gasLimit = 1000000;

            EthereumEcdsa ecdsa = new EthereumEcdsa(1, LimboLogs.Instance);
            // deploy create 2
            Transaction tx0 = Build.A.Transaction.WithInit(initOfCreate2Code).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call contract once
            Transaction tx1 = Build.A.Transaction.WithInit(byteCode1).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // self destruct contract
            Transaction tx2 = Build.A.Transaction.WithInit(byteCode2).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // deploy again using create2
            Transaction tx3 = Build.A.Transaction.WithValue(3).WithInit(deploy).WithGasLimit(gasLimit).WithNonce(3).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call newly deployed once
            Transaction tx4 = Build.A.Transaction.WithInit(byteCode1).WithGasLimit(gasLimit).WithNonce(4).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx0, tx1, tx2, tx3, tx4).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer tracer = new ParityLikeTxTracer(block, tx0, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx0, block.Header, tracer);

            tracer = new ParityLikeTxTracer(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 7), 7);

            tracer = new ParityLikeTxTracer(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, block.Header, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 7), 0);

            tracer = new ParityLikeTxTracer(block, tx3, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx3, block.Header, tracer);
            AssertStorage(new StorageCell(deploymentAddress, 7), 0);

            tracer = new ParityLikeTxTracer(block, tx4, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx4, block.Header, tracer);
            AssertStorage(new StorageCell(deploymentAddress, 7), 0);
        }
    }
}