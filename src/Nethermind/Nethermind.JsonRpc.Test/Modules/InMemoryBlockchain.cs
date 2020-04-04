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
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Facade;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Store.Bloom;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Test.Modules
{
    // public class Build<T> where T : InMemoryBlockchain 
    // {
    //     private SealEngineType _sealEngineType;
    //
    //     public Build(SealEngineType sealEngineType)
    //     {
    //         _sealEngineType = sealEngineType;
    //     }
    //     
    //     public static Build<T> Blockchain(SealEngineType sealEngineType = SealEngineType.NethDev)
    //     {
    //         Build<T> build = new Build<T>(sealEngineType);
    //         return build;
    //     }
    //
    //     public InMemoryBlockchain Test
    //     {
    //         get
    //         {
    //             return new InMemoryBlockchain(_sealEngineType);    
    //         }
    //     }
    // }
    //
    public class TestBlockchain
    {
        private readonly SealEngineType _sealEngineType;
        public IEthereumEcdsa EthereumEcdsa { get; private set; }
        public TransactionProcessor TxProcessor { get; set; }
        public IStorageProvider StorageProvider { get; set; }
        public IReceiptStorage ReceiptStorage { get; set; }
        public ITxPool TxPool { get; set; }
        public ISnapshotableDb CodeDb { get; set; }
        public IBlockProcessor BlockProcessor { get; set; }
        public IBlockTree BlockTree { get; set; }
        public IJsonSerializer JsonSerializer { get; set; }
        public IStateProvider StateProvider { get; set; }
        public ISnapshotableDb StateDb { get; set; }

        protected TestBlockchain(SealEngineType sealEngineType)
        {
            _sealEngineType = sealEngineType;
        }

        protected virtual TestBlockchain Build()
        {
            JsonSerializer = new EthereumJsonSerializer();
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            EthereumEcdsa = new EthereumEcdsa(specProvider, LimboLogs.Instance);
            ITxStorage txStorage = new InMemoryTxStorage();
            StateDb = new StateDb();
            CodeDb = new StateDb();
            StateProvider = new StateProvider(StateDb, CodeDb, LimboLogs.Instance);
            StateProvider.CreateAccount(TestItem.AddressA, 1000.Ether());
            StateProvider.CreateAccount(TestItem.AddressB, 1000.Ether());
            StateProvider.CreateAccount(TestItem.AddressC, 1000.Ether());
            byte[] code = Bytes.FromHexString("0xabcd");
            Keccak codeHash = Keccak.Compute(code);
            StateProvider.UpdateCode(code);
            StateProvider.UpdateCodeHash(TestItem.AddressA, codeHash, specProvider.GenesisSpec);

            StorageProvider = new StorageProvider(StateDb, StateProvider, LimboLogs.Instance);
            StorageProvider.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));
            StorageProvider.Commit();

            StateProvider.Commit(specProvider.GenesisSpec);
            StateProvider.CommitTree();

            TxPool = new TxPool.TxPool(txStorage, Timestamper.Default, EthereumEcdsa, specProvider, new TxPoolConfig(), StateProvider, LimboLogs.Instance);

            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            BlockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockDb), specProvider, TxPool, NullBloomStorage.Instance, LimboLogs.Instance);

            ReceiptStorage = new InMemoryReceiptStorage();
            VirtualMachine virtualMachine = new VirtualMachine(StateProvider, StorageProvider, new BlockhashProvider(BlockTree, LimboLogs.Instance), specProvider, LimboLogs.Instance);
            TxProcessor = new TransactionProcessor(specProvider, StateProvider, StorageProvider, virtualMachine, LimboLogs.Instance);
            BlockProcessor = new BlockProcessor(specProvider, AlwaysValidBlockValidator.Instance, new RewardCalculator(specProvider), TxProcessor, StateDb, CodeDb, StateProvider, StorageProvider, TxPool, ReceiptStorage, LimboLogs.Instance);

            BlockchainProcessor blockchainProcessor = new BlockchainProcessor(BlockTree, BlockProcessor, new TxSignaturesRecoveryStep(EthereumEcdsa, TxPool, LimboLogs.Instance), LimboLogs.Instance, true);
            blockchainProcessor.Start();

            ManualResetEventSlim resetEvent = new ManualResetEventSlim(false);
            BlockTree.NewHeadBlock += (s, e) =>
            {
                Console.WriteLine(e.Block.Header.Hash);
                if (e.Block.Number == 9)
                {
                    resetEvent.Set();
                }
            };

            var genesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"));
            if (_sealEngineType == SealEngineType.AuRa)
            {
                genesisBlockBuilder.WithAura(0, new byte[65]);
            }

            Block genesis = genesisBlockBuilder.TestObject;
            BlockTree.SuggestBlock(genesis);

            Block previousBlock = genesis;
            for (int i = 1; i < 10; i++)
            {
                BlockBuilder builder = Core.Test.Builders.Build.A.Block.WithNumber(i).WithParent(previousBlock).WithTransactions(i == 2 ? new Transaction[] {Core.Test.Builders.Build.A.Transaction.SignedAndResolved().TestObject} : Array.Empty<Transaction>()).WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"));
                if (_sealEngineType == SealEngineType.AuRa)
                {
                    builder.WithAura(i, i.ToByteArray());
                }

                Block block = builder.TestObject;
                BlockTree.SuggestBlock(block);
                previousBlock = block;
            }

            resetEvent.Wait(2000);
            return this;
        }
    }
}