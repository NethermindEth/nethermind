/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Rewards;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockProcessorTests
    {
        [Test]
        public void Prepared_block_contains_author_field()
        {
            ISnapshotableDb stateDb = new StateDb();
            ISnapshotableDb codeDb = new StateDb();
            IDb traceDb = new MemDb();
            IStateProvider stateProvider = new StateProvider(stateDb, codeDb, LimboLogs.Instance);
            ITransactionProcessor transactionProcessor = Substitute.For<ITransactionProcessor>();
            BlockProcessor processor = new BlockProcessor(
                RinkebySpecProvider.Instance,
                TestBlockValidator.AlwaysValid,
                NoBlockRewards.Instance,
                transactionProcessor,
                stateDb,
                codeDb,
                traceDb,
                stateProvider,
                new StorageProvider(stateDb, stateProvider, LimboLogs.Instance),
                NullTxPool.Instance,
                NullReceiptStorage.Instance, 
                LimboLogs.Instance);

            BlockHeader header = Build.A.BlockHeader.WithAuthor(TestItem.AddressD).TestObject;
            Block block = Build.A.Block.WithHeader(header).TestObject;
            Block[] processedBlocks = processor.Process(Keccak.EmptyTreeHash, new [] {block}, ProcessingOptions.None, NullBlockTracer.Instance);
            Assert.AreEqual(1, processedBlocks.Length, "length");
            Assert.AreEqual(block.Author, processedBlocks[0].Author, "author");
        }
    }
}