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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Proof
{
    [TestFixture]
    public class ProofModuleTests
    {
        private IProofModule _proofModule;
        private IBlockTree _blockTree;
        private IDbProvider _dbProvider;

        [SetUp]
        public void Setup()
        {
            InMemoryReceiptStorage receiptStorage = new InMemoryReceiptStorage();
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            _blockTree = Build.A.BlockTree().WithTransactions(receiptStorage, specProvider).OfChainLength(10).TestObject;
            _dbProvider = new MemDbProvider();
            ProofModuleFactory moduleFactory = new ProofModuleFactory(
                _dbProvider,
                _blockTree,
                new CompositeDataRecoveryStep(),
                receiptStorage,
                specProvider,
                LimboLogs.Instance);

            _proofModule = moduleFactory.Create();
        }

        [Test]
        public void Can_get_transaction()
        {
            _proofModule.proof_getTransactionByHash(_blockTree.FindBlock(1).Transactions[0].Hash);
        }

        [Test]
        public void Can_get_receipt()
        {
            _proofModule.proof_getTransactionReceipt(_blockTree.FindBlock(1).Transactions[0].Hash);
        }

        [Test]
        public void Can_call()
        {
            StateProvider stateProvider = new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            AddAccount(stateProvider, TestItem.AddressA, 1.Ether());
            AddAccount(stateProvider, TestItem.AddressB, 1.Ether());

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            _proofModule.proof_call(new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            }, new BlockParameter(block.Number));
        }

        [Test]
        public void Can_call_with_block_hashes()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.BLOCKHASH)
                .Done;
            TestCallWithCode(code);
        }
        
        [Test]
        public void Can_call_with_storage_load()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .Op(Instruction.SLOAD)
                .Done;
            TestCallWithCode(code);
        }
        
        [Test]
        public void Can_call_with_storage_write()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x01")
                .Op(Instruction.SSTORE)
                .Done;
            TestCallWithCode(code);
        }

        private void TestCallWithCode(byte[] code)
        {
            StateProvider stateProvider = new StateProvider(_dbProvider.StateDb, _dbProvider.CodeDb, LimboLogs.Instance);
            AddAccount(stateProvider, TestItem.AddressA, 1.Ether());
            AddAccount(stateProvider, TestItem.AddressB, 1.Ether());
            AddCode(stateProvider, TestItem.AddressB, code);

            Keccak root = stateProvider.StateRoot;
            Block block = Build.A.Block.WithParent(_blockTree.Head).WithStateRoot(root).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, block);

            // would need to setup state root somehow...

            _proofModule.proof_call(new TransactionForRpc
            {
                From = TestItem.AddressA,
                To = TestItem.AddressB
            }, new BlockParameter(block.Number));
        }

        private void AddAccount(StateProvider stateProvider, Address account, UInt256 initialBalance)
        {
            stateProvider.CreateAccount(account, initialBalance);
            stateProvider.Commit(MuirGlacier.Instance, null);
            stateProvider.CommitTree();
            _dbProvider.StateDb.Commit();
        }

        private void AddCode(StateProvider stateProvider, Address account, byte[] code)
        {
            Keccak codeHash = stateProvider.UpdateCode(code);
            stateProvider.UpdateCodeHash(account, codeHash, MuirGlacier.Instance);

            stateProvider.Commit(MainNetSpecProvider.Instance.GenesisSpec, null);
            stateProvider.CommitTree();
            _dbProvider.CodeDb.Commit();
            _dbProvider.StateDb.Commit();
        }
    }
}