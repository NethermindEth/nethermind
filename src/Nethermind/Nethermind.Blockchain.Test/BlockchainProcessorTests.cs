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

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockchainProcessorTests
    {
        private class ProcessingTestContext
        {
            private BlockTree _blockTree;
            private AutoResetEvent _resetEvent;

            public ProcessingTestContext()
            {
                MemDb blockDb = new MemDb();
                MemDb blockInfoDb = new MemDb();
                _blockTree = new BlockTree(blockDb, blockInfoDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, NullLogManager.Instance);
                IBlockProcessor blockProcessor = Substitute.For<IBlockProcessor>();
                BlockchainProcessor processor = new BlockchainProcessor(_blockTree, blockProcessor, NullRecoveryStep.Instance, NullLogManager.Instance, false, false);
                _resetEvent = new AutoResetEvent(false);
                bool ignoreNextSignal = true;
                processor.ProcessingQueueEmpty += (sender, args) =>
                {
                    if (ignoreNextSignal)
                    {
                        ignoreNextSignal = false;
                        return;
                    }

                    _resetEvent.Set();
                };

                blockProcessor.Process(Arg.Any<Keccak>(), Arg.Any<Block[]>(), ProcessingOptions.None, NullBlockTracer.Instance).Returns(ci => ci.ArgAt<Block[]>(1));
                processor.Start();
            }

            public AfterBlock Then(Block block)
            {
                return new AfterBlock(this, block);
            }

            public class AfterBlock
            {
                private const int ProcessingWait = 1000;
                private const int IgnoreWait = 200;
                private readonly Block _block;
                private readonly BlockHeader _headBefore;
                private readonly ProcessingTestContext _processingTestContext;

                public AfterBlock(ProcessingTestContext processingTestContext, Block block)
                {
                    _processingTestContext = processingTestContext;
                    _block = block;

                    _headBefore = _processingTestContext._blockTree.Head;
                    _processingTestContext._blockTree.SuggestBlock(_block);
                }

                public ProcessingTestContext BecomesGenesis()
                {
                    _processingTestContext._resetEvent.WaitOne(ProcessingWait);
                    Assert.AreEqual(_block.Header, _processingTestContext._blockTree.Genesis, "genesis");
                    return _processingTestContext;
                }

                public ProcessingTestContext BecomesNewHead()
                {
                    _processingTestContext._resetEvent.WaitOne(ProcessingWait);
                    Assert.AreEqual(_block.Header, _processingTestContext._blockTree.Head, "head");
                    return _processingTestContext;
                }

                public ProcessingTestContext IsKeptOnBranch()
                {
                    _processingTestContext._resetEvent.WaitOne(IgnoreWait);
                    Assert.AreEqual(_headBefore, _processingTestContext._blockTree.Head, "head");
                    return _processingTestContext;
                }
            }
        }

        private static class When
        {
            public static ProcessingTestContext ProcessingBlocks => new ProcessingTestContext();
        }

        private static Block _block0 = Build.A.Block.WithNumber(0).WithNonce(0).WithDifficulty(0).TestObject;
        private static Block _block1D2 = Build.A.Block.WithNumber(1).WithNonce(1).WithParent(_block0).WithDifficulty(2).TestObject;
        private static Block _block2D4 = Build.A.Block.WithNumber(2).WithNonce(2).WithParent(_block1D2).WithDifficulty(2).TestObject;
        private static Block _block3D6 = Build.A.Block.WithNumber(3).WithNonce(3).WithParent(_block2D4).WithDifficulty(2).TestObject;
        private static Block _block4D8 = Build.A.Block.WithNumber(4).WithNonce(4).WithParent(_block3D6).WithDifficulty(2).TestObject;
        private static Block _block5D10 = Build.A.Block.WithNumber(5).WithNonce(5).WithParent(_block4D8).WithDifficulty(2).TestObject;
        private static Block _blockB2D4 = Build.A.Block.WithNumber(2).WithNonce(6).WithParent(_block1D2).WithDifficulty(2).TestObject;
        private static Block _blockB3D8 = Build.A.Block.WithNumber(3).WithNonce(7).WithParent(_blockB2D4).WithDifficulty(4).TestObject;
        private static Block _blockC2D100 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(98).TestObject;
        private static Block _blockD2D200 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(198).TestObject;
        private static Block _blockE2D300 = Build.A.Block.WithNumber(3).WithNonce(8).WithParent(_block1D2).WithDifficulty(298).TestObject;

        [Test]
        public void Can_ignore_lower_difficulty()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_blockB2D4).BecomesNewHead()
                .Then(_blockB3D8).BecomesNewHead()
                .Then(_block2D4).IsKeptOnBranch()
                .Then(_block3D6).IsKeptOnBranch();
        }

        [Test]
        public void Can_ignore_same_difficulty()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_blockB2D4).IsKeptOnBranch();
        }

        [Test]
        public void Can_process_sequence()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_block3D6).BecomesNewHead()
                .Then(_block4D8).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_just_head_block_twice()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_blockC2D100).BecomesNewHead()
                .Then(_blockD2D200).BecomesNewHead()
                .Then(_blockE2D300).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_there_and_back()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_block3D6).BecomesNewHead()
                .Then(_blockB2D4).IsKeptOnBranch()
                .Then(_blockB3D8).BecomesNewHead()
                .Then(_block4D8).IsKeptOnBranch()
                .Then(_block5D10).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_to_longer_path()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_blockB2D4).BecomesNewHead()
                .Then(_blockB3D8).BecomesNewHead()
                .Then(_block2D4).IsKeptOnBranch()
                .Then(_block3D6).IsKeptOnBranch()
                .Then(_block4D8).IsKeptOnBranch()
                .Then(_block5D10).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_to_same_length()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_block3D6).BecomesNewHead()
                .Then(_blockB2D4).IsKeptOnBranch()
                .Then(_blockB3D8).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_to_shorter_path()
        {
            When.ProcessingBlocks
                .Then(_block0).BecomesGenesis()
                .Then(_block1D2).BecomesNewHead()
                .Then(_block2D4).BecomesNewHead()
                .Then(_block3D6).BecomesNewHead()
                .Then(_blockC2D100).BecomesNewHead();
        }
    }
}