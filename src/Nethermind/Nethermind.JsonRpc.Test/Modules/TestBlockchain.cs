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
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Validators;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.State.Repositories;
using Nethermind.Store.Bloom;
using Nethermind.TxPool;
using Nethermind.TxPool.Storages;

namespace Nethermind.JsonRpc.Test.Modules
{
    public class TestBlockchain
    {
        private readonly SealEngineType _sealEngineType;
        public IStateReader StateReader { get; private set; }
        public IBlockProducer BlockProducer { get; private set; }
        public IEthereumEcdsa EthereumEcdsa { get; private set; }
        public TransactionProcessor TxProcessor { get; set; }
        public IStorageProvider Storage { get; set; }
        public IReceiptStorage ReceiptStorage { get; set; }
        public ITxPool TxPool { get; set; }
        public ISnapshotableDb CodeDb { get; set; }
        public IBlockProcessor BlockProcessor { get; set; }
        public IBlockTree BlockTree { get; set; }
        public IJsonSerializer JsonSerializer { get; set; }
        public IStateProvider State { get; set; }
        public ISnapshotableDb StateDb { get; set; }

        protected TestBlockchain(SealEngineType sealEngineType)
        {
            _sealEngineType = sealEngineType;
        }

        public static Address AccountA = TestItem.AddressA;
        public static Address AccountB = TestItem.AddressB;
        public static Address AccountC = TestItem.AddressC;

        public static TransactionBuilder BuildSimpleTransaction = Core.Test.Builders.Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).To(AccountB);
        
        protected virtual async Task<TestBlockchain> Build()
        {
            JsonSerializer = new EthereumJsonSerializer();
            ISpecProvider specProvider = MainNetSpecProvider.Instance;
            EthereumEcdsa = new EthereumEcdsa(specProvider, LimboLogs.Instance);
            ITxStorage txStorage = new InMemoryTxStorage();
            StateDb = new StateDb();
            CodeDb = new StateDb();
            State = new StateProvider(StateDb, CodeDb, LimboLogs.Instance);
            State.CreateAccount(TestItem.AddressA, 1000.Ether());
            State.CreateAccount(TestItem.AddressB, 1000.Ether());
            State.CreateAccount(TestItem.AddressC, 1000.Ether());
            byte[] code = Bytes.FromHexString("0xabcd");
            Keccak codeHash = Keccak.Compute(code);
            State.UpdateCode(code);
            State.UpdateCodeHash(TestItem.AddressA, codeHash, specProvider.GenesisSpec);

            Storage = new StorageProvider(StateDb, State, LimboLogs.Instance);
            Storage.Set(new StorageCell(TestItem.AddressA, UInt256.One), Bytes.FromHexString("0xabcdef"));
            Storage.Commit();

            State.Commit(specProvider.GenesisSpec);
            State.CommitTree();

            TxPool = new TxPool.TxPool(txStorage, Timestamper.Default, EthereumEcdsa, specProvider, new TxPoolConfig(), State, LimboLogs.Instance);

            IDb blockDb = new MemDb();
            IDb headerDb = new MemDb();
            IDb blockInfoDb = new MemDb();
            BlockTree = new BlockTree(blockDb, headerDb, blockInfoDb, new ChainLevelInfoRepository(blockDb), specProvider, TxPool, NullBloomStorage.Instance, LimboLogs.Instance);

            ReceiptStorage = new InMemoryReceiptStorage();
            VirtualMachine virtualMachine = new VirtualMachine(State, Storage, new BlockhashProvider(BlockTree, LimboLogs.Instance), specProvider, LimboLogs.Instance);
            TxProcessor = new TransactionProcessor(specProvider, State, Storage, virtualMachine, LimboLogs.Instance);
            BlockProcessor = new BlockProcessor(specProvider, AlwaysValidBlockValidator.Instance, new RewardCalculator(specProvider), TxProcessor, StateDb, CodeDb, State, Storage, TxPool, ReceiptStorage, LimboLogs.Instance);

            BlockchainProcessor chainProcessor = new BlockchainProcessor(BlockTree, BlockProcessor, new TxSignaturesRecoveryStep(EthereumEcdsa, TxPool, LimboLogs.Instance), LimboLogs.Instance, true);
            chainProcessor.Start();

            StateReader = new StateReader(StateDb, CodeDb, LimboLogs.Instance);
            PendingTxSelector txSelector = new PendingTxSelector(TxPool, StateReader, LimboLogs.Instance);
            ISealer sealer = new FakeSealer(TimeSpan.Zero);
            TestBlockProducer producer = new TestBlockProducer(txSelector, chainProcessor, State, sealer, BlockTree, chainProcessor, Timestamper.Default, LimboLogs.Instance);
            producer.Start();

            AutoResetEvent resetEvent = new AutoResetEvent(false);
            BlockTree.NewHeadBlock += (s, e) =>
            {
                Console.WriteLine(e.Block.Header.Hash);
                resetEvent.Set();
            };

            var genesisBlockBuilder = Core.Test.Builders.Build.A.Block.Genesis.WithStateRoot(new Keccak("0x1ef7300d8961797263939a3d29bbba4ccf1702fabf02d8ad7a20b454edb6fd2f"));
            if (_sealEngineType == SealEngineType.AuRa)
            {
                genesisBlockBuilder.WithAura(0, new byte[65]);
            }

            Block genesis = genesisBlockBuilder.TestObject;
            BlockTree.SuggestBlock(genesis);

            Block previousBlock = genesis;

            // BlockBuilder[] blockBuilders = new BlockBuilder[]
            // {
            //     BuildBlockWithoutTransactions,
            //     BuildBlockWithOneSimpleTransaction,
            //     BuildBlockWithTwoSimpleTransaction,
            // };

            // if (_sealEngineType == SealEngineType.AuRa)
            // {
            //     for (int i = 0; i < blockBuilders.Length; i++)
            //     {
            //         blockBuilders[i].WithAura(i + 1, i.ToByteArray());
            //     }
            // }

            await resetEvent.WaitOneAsync(CancellationToken.None);
            producer.BuildNewBlock();
            await resetEvent.WaitOneAsync(CancellationToken.None);
            TxPool.AddTransaction(BuildSimpleTransaction.TestObject, 1, TxHandlingOptions.ManagedNonce);
            producer.BuildNewBlock();
            await resetEvent.WaitOneAsync(CancellationToken.None);
            TxPool.AddTransaction(BuildSimpleTransaction.TestObject, 2, TxHandlingOptions.ManagedNonce);
            TxPool.AddTransaction(BuildSimpleTransaction.TestObject, 2, TxHandlingOptions.ManagedNonce);
            producer.BuildNewBlock();
            await resetEvent.WaitOneAsync(CancellationToken.None);
            return this;
        }
    }
}