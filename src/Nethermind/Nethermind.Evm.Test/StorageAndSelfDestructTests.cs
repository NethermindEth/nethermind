// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Blockchain.Tracing.ParityStyle;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
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
            TestState.CommitTree(0);

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

            EthereumEcdsa ecdsa = new(1);
            Transaction initTx = Build.A.Transaction.WithCode(initByteCode).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Transaction tx1 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Transaction tx2 = Build.A.Transaction.WithCode(byteCode2).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(initTx, tx1, tx2).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer initTracer = new(block, initTx, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            var blCtx = new BlockExecutionContext(block.Header, Spec);
            _processor.Execute(initTx, blCtx, initTracer);
            AssertStorage(new StorageCell(contractAddress, 1), 0);

            ParityLikeTxTracer tracer1 = new(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, blCtx, tracer1);
            AssertStorage(new StorageCell(contractAddress, 1), 1);

            ParityLikeTxTracer tracer2 = new(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, blCtx, tracer2);
            AssertStorage(new StorageCell(contractAddress, 1), 0);
        }

        [Test]
        public void Destroy_restore_store()
        {
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);

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

            EthereumEcdsa ecdsa = new(1);
            // deploy create 2
            Transaction tx0 = Build.A.Transaction.WithCode(initOfCreate2Code).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // invoke create 2 to deploy contract
            Transaction tx1 = Build.A.Transaction.WithCode(deploy).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call contract once
            Transaction tx2 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // self destruct contract
            Transaction tx3 = Build.A.Transaction.WithCode(byteCode2).WithGasLimit(gasLimit).WithNonce(3).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // deploy again using create2
            Transaction tx4 = Build.A.Transaction.WithCode(deploy).WithGasLimit(gasLimit).WithNonce(4).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call newly deployed once
            Transaction tx5 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(5).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx0, tx1, tx2, tx3, tx4, tx5).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer tracer0 = new(block, tx0, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            var blCtx = new BlockExecutionContext(block.Header, Spec);
            _processor.Execute(tx0, blCtx, tracer0);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);

            ParityLikeTxTracer tracer = new(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);

            tracer = new ParityLikeTxTracer(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 1);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);

            tracer = new ParityLikeTxTracer(block, tx3, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx3, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 0);

            tracer = new ParityLikeTxTracer(block, tx4, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx4, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);

            tracer = new ParityLikeTxTracer(block, tx5, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx5, blCtx, tracer);
            AssertStorage(new StorageCell(deploymentAddress, 1), 1);
            AssertStorage(new StorageCell(deploymentAddress, 2), 2);
        }

        [Test]
        public void Destroy_restore_store_different_cells()
        {
            TestState.CreateAccount(TestItem.PrivateKeyA.Address, 100.Ether());
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);

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

            EthereumEcdsa ecdsa = new(1);
            // deploy create 2
            Transaction tx0 = Build.A.Transaction.WithCode(initOfCreate2Code).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // invoke create 2 to deploy contract
            Transaction tx1 = Build.A.Transaction.WithValue(2).WithCode(deploy).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call contract once
            Transaction tx2 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // self destruct contract
            Transaction tx3 = Build.A.Transaction.WithCode(byteCode2).WithGasLimit(gasLimit).WithNonce(3).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // deploy again using create2
            Transaction tx4 = Build.A.Transaction.WithValue(3).WithCode(deploy).WithGasLimit(gasLimit).WithNonce(4).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call newly deployed once
            Transaction tx5 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(5).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx0, tx1, tx2, tx3, tx4, tx5).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer tracer0 = new(block, tx0, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            var blCtx = new BlockExecutionContext(block.Header, Spec);
            _processor.Execute(tx0, blCtx, tracer0);
            AssertStorage(new StorageCell(deploymentAddress, 1), 0);

            ParityLikeTxTracer tracer = new(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);

            tracer = new ParityLikeTxTracer(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 1);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 2);
            // AssertStorage(new StorageCell(deploymentAddress, 3), 0);

            tracer = new ParityLikeTxTracer(block, tx3, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx3, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 3), 0);

            tracer = new ParityLikeTxTracer(block, tx4, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx4, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 1), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 2), 0);
            // AssertStorage(new StorageCell(deploymentAddress, 3), 2);

            tracer = new ParityLikeTxTracer(block, tx5, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx5, blCtx, tracer);
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
            //TestState.Commit(SpecProvider.GenesisSpec);
            //TestState.CommitTree(0);

            TestState.CreateAccount(deploymentAddress, UInt256.One);
            TestState.InsertCode(deploymentAddress, contractCode, MuirGlacier.Instance);

            TestState.Set(new StorageCell(deploymentAddress, 7), new byte[] { 7 });
            TestState.Commit(MuirGlacier.Instance);
            TestState.CommitTree(0);

            long gasLimit = 1000000;

            EthereumEcdsa ecdsa = new(1);
            // deploy create 2
            Transaction tx0 = Build.A.Transaction.WithCode(initOfCreate2Code).WithGasLimit(gasLimit).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call contract once
            Transaction tx1 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(1).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // self destruct contract
            Transaction tx2 = Build.A.Transaction.WithCode(byteCode2).WithGasLimit(gasLimit).WithNonce(2).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // deploy again using create2
            Transaction tx3 = Build.A.Transaction.WithValue(3).WithCode(deploy).WithGasLimit(gasLimit).WithNonce(3).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            // call newly deployed once
            Transaction tx4 = Build.A.Transaction.WithCode(byteCode1).WithGasLimit(gasLimit).WithNonce(4).SignedAndResolved(ecdsa, TestItem.PrivateKeyA).TestObject;
            Block block = Build.A.Block.WithNumber(MainnetSpecProvider.MuirGlacierBlockNumber).WithTransactions(tx0, tx1, tx2, tx3, tx4).WithGasLimit(2 * gasLimit).TestObject;

            ParityLikeTxTracer tracer = new(block, tx0, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            var blCtx = new BlockExecutionContext(block.Header, Spec);
            _processor.Execute(tx0, blCtx, tracer);

            tracer = new ParityLikeTxTracer(block, tx1, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx1, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 7), 7);

            tracer = new ParityLikeTxTracer(block, tx2, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx2, blCtx, tracer);
            // AssertStorage(new StorageCell(deploymentAddress, 7), 0);

            tracer = new ParityLikeTxTracer(block, tx3, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx3, blCtx, tracer);
            AssertStorage(new StorageCell(deploymentAddress, 7), 0);

            tracer = new ParityLikeTxTracer(block, tx4, ParityTraceTypes.Trace | ParityTraceTypes.StateDiff);
            _processor.Execute(tx4, blCtx, tracer);
            AssertStorage(new StorageCell(deploymentAddress, 7), 0);
        }
    }
}
