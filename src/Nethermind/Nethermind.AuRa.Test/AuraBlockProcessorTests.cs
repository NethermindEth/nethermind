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
// 

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuraBlockProcessorTests
    {
        [Test]
        public void Prepared_block_contains_author_field()
        {
            AuRaBlockProcessor processor = CreateProcessor();

            BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
            Block block = Build.A.Block.WithHeader(header).TestObject;
            Block[] processedBlocks = processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> {block},
                ProcessingOptions.None,
                NullBlockTracer.Instance);
            Assert.AreEqual(1, processedBlocks.Length, "length");
            Assert.AreEqual(block.Author, processedBlocks[0].Author, "author");
        }
        
        [Test]
        public void For_not_empty_block_tx_filter_should_be_called()
        {
            ITxFilter txFilter = Substitute.For<ITxFilter>();
            txFilter
                .IsAllowed(Arg.Any<Transaction>(), Arg.Any<BlockHeader>())
                .Returns((true, string.Empty));
            AuRaBlockProcessor processor = CreateProcessor(txFilter);

            BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).WithNumber(3).TestObject;
            Transaction tx = Nethermind.Core.Test.Builders.Build.A.Transaction.WithData(new byte[] {0, 1})
                .SignedAndResolved().WithChainId(105).WithGasPrice(0).WithValue(0).TestObject;
            Block block = Build.A.Block.WithHeader(header).WithTransactions(new Transaction[] { tx }).TestObject;
            Block[] processedBlocks = processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> {block},
                ProcessingOptions.None,
                NullBlockTracer.Instance);
            txFilter.Received().IsAllowed(Arg.Any<Transaction>(), Arg.Any<BlockHeader>());
        }
        
        [Test]
        public void For_normal_processing_it_should_not_fail_with_gas_remaining_rules()
        {
            AuRaBlockProcessor processor = CreateProcessor();
            int gasLimit = 10000000;
            BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).WithNumber(3).TestObject;
            Transaction tx = Nethermind.Core.Test.Builders.Build.A.Transaction.WithData(new byte[] {0, 1})
                .SignedAndResolved().WithChainId(105).WithGasPrice(0).WithValue(0).WithGasLimit(gasLimit + 1).TestObject;
            Block block = Build.A.Block.WithHeader(header).WithTransactions(new Transaction[] { tx })
                .WithGasLimit(gasLimit).TestObject;
            Assert.DoesNotThrow(() => processor.Process(
                Keccak.EmptyTreeHash,
                new List<Block> {block},
                ProcessingOptions.None,
                NullBlockTracer.Instance));
            
        }

        private AuRaBlockProcessor CreateProcessor(ITxFilter txFilter = null)
        {
            IDb stateDb = new MemDb();
            IDb codeDb = new MemDb();
            TrieStore trieStore = new TrieStore(stateDb, LimboLogs.Instance);
            IStateProvider stateProvider = new StateProvider(trieStore, codeDb, LimboLogs.Instance);
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            return new AuRaBlockProcessor(
                RinkebySpecProvider.Instance,
                TestBlockValidator.AlwaysValid,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(transactionProcessor, stateProvider),
                stateProvider,
                new StorageProvider(trieStore, stateProvider, LimboLogs.Instance),
                NullReceiptStorage.Instance,
                LimboLogs.Instance,
                Substitute.For<IBlockTree>(),
                txFilter);
        }
    }
}
