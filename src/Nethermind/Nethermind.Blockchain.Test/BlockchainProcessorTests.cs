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

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockchainProcessorTests
    {
        private class ProcessingTestContext
        {
            private class BlockProcessorMock : IBlockProcessor
            {
                private HashSet<Keccak> _allowed = new HashSet<Keccak>();

                private HashSet<Keccak> _allowedToFail = new HashSet<Keccak>();

                public void Allow(Keccak hash)
                {
                    _allowed.Add(hash);
                }

                public void AllowToFail(Keccak hash)
                {
                    _allowedToFail.Add(hash);
                }

                public Block[] Process(Keccak branchStateRoot, Block[] suggestedBlocks, ProcessingOptions processingOptions, IBlockTracer blockTracer)
                {
                    while (true)
                    {
                        bool notYet = false;
                        for (int i = 0; i < suggestedBlocks.Length; i++)
                        {
                            Keccak hash = suggestedBlocks[i].Hash;
                            if (!_allowed.Contains(hash))
                            {
                                if (_allowedToFail.Contains(hash))
                                {
                                    _allowedToFail.Remove(hash);
                                    throw new InvalidBlockException(hash);
                                }

                                notYet = true;
                                break;
                            }

                            _allowed.Remove(hash);
                        }

                        if (notYet)
                        {
                            Thread.Sleep(20);
                        }
                        else
                        {
                            return suggestedBlocks;
                        }
                    }
                }

                public event EventHandler<BlockProcessedEventArgs> BlockProcessed;
                public event EventHandler<TransactionProcessedEventArgs> TransactionProcessed;
            }

            private class RecoveryStepMock : IBlockDataRecoveryStep
            {
                private HashSet<Keccak> _allowed = new HashSet<Keccak>();

                private HashSet<Keccak> _allowedToFail = new HashSet<Keccak>();

                public void Allow(Keccak hash)
                {
                    _allowed.Add(hash);
                }

                public void AllowToFail(Keccak hash)
                {
                    _allowedToFail.Add(hash);
                }

                public void RecoverData(Block block)
                {
                    if (block.Author != null)
                    {
                        return;
                    }
                    
                    while (true)
                    {
                        if (!_allowed.Contains(block.Hash))
                        {
                            if (_allowedToFail.Contains(block.Hash))
                            {
                                _allowedToFail.Remove(block.Hash);
                                throw new Exception();
                            }

                            Thread.Sleep(20);
                            continue;
                        }

                        block.Author = Address.Zero;
                        _allowed.Remove(block.Hash);
                        return;
                    }
                }
            }

            private BlockTree _blockTree;
            private AutoResetEvent _resetEvent;
            private BlockProcessorMock _blockProcessor;
            private RecoveryStepMock _recoveryStep;

            public ProcessingTestContext()
            {
                MemDb blockDb = new MemDb();
                MemDb blockInfoDb = new MemDb();
                _blockTree = new BlockTree(blockDb, blockInfoDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, NullLogManager.Instance);
                _blockProcessor = new BlockProcessorMock();
                _recoveryStep = new RecoveryStepMock();
                BlockchainProcessor processor = new BlockchainProcessor(_blockTree, _blockProcessor, _recoveryStep, NullLogManager.Instance, false, false);
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

                processor.Start();
            }

            public AfterBlock Processed(Block block)
            {
                _headBefore = _blockTree.Head;
                _blockProcessor.Allow(block.Hash);
                return new AfterBlock(this, block);
            }

            public AfterBlock ProcessedFail(Block block)
            {
                _headBefore = _blockTree.Head;
                _blockProcessor.AllowToFail(block.Hash);
                return new AfterBlock(this, block);
            }

            public ProcessingTestContext Suggested(Block block)
            {
                _blockTree.SuggestBlock(block);
                return this;
            }

            public ProcessingTestContext Recovered(Block block)
            {
                _recoveryStep.Allow(block.Hash);
                return this;
            }

            public ProcessingTestContext ThenRecoveredFail(Block block)
            {
                _recoveryStep.AllowToFail(block.Hash);
                return this;
            }

            public AfterBlock FullyProcessed(Block block)
            {
                return Suggested(block)
                    .Recovered(block)
                    .Processed(block);
            }

            public AfterBlock FullyProcessedFail(Block block)
            {
                return Suggested(block)
                    .Recovered(block)
                    .ProcessedFail(block);
            }

            private BlockHeader _headBefore;

            public class AfterBlock
            {
                private const int ProcessingWait = 1000 * 1000;
                private const int IgnoreWait = 200;
                private readonly Block _block;

                private readonly ProcessingTestContext _processingTestContext;

                public AfterBlock(ProcessingTestContext processingTestContext, Block block)
                {
                    _processingTestContext = processingTestContext;
                    _block = block;
                }

                public ProcessingTestContext AndThen => _processingTestContext;

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
                    Assert.AreEqual(_processingTestContext._headBefore, _processingTestContext._blockTree.Head, "head");
                    return _processingTestContext;
                }

                public ProcessingTestContext IsDeletedAsInvalid()
                {
                    _processingTestContext._resetEvent.WaitOne(IgnoreWait);
                    Assert.AreEqual(_processingTestContext._headBefore, _processingTestContext._blockTree.Head, "head");
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
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_blockB2D4).BecomesNewHead()
                .FullyProcessed(_blockB3D8).BecomesNewHead()
                .FullyProcessed(_block2D4).IsKeptOnBranch()
                .FullyProcessed(_block3D6).IsKeptOnBranch();
        }

        [Test]
        public void Can_ignore_same_difficulty()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessed(_blockB2D4).IsKeptOnBranch();
        }

        [Test]
        public void Can_process_sequence()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessed(_block3D6).BecomesNewHead()
                .FullyProcessed(_block4D8).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_just_head_block_twice()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessed(_blockC2D100).BecomesNewHead()
                .FullyProcessed(_blockD2D200).BecomesNewHead()
                .FullyProcessed(_blockE2D300).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_there_and_back()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessed(_block3D6).BecomesNewHead()
                .FullyProcessed(_blockB2D4).IsKeptOnBranch()
                .FullyProcessed(_blockB3D8).BecomesNewHead()
                .FullyProcessed(_block4D8).IsKeptOnBranch()
                .FullyProcessed(_block5D10).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_to_longer_path()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_blockB2D4).BecomesNewHead()
                .FullyProcessed(_blockB3D8).BecomesNewHead()
                .FullyProcessed(_block2D4).IsKeptOnBranch()
                .FullyProcessed(_block3D6).IsKeptOnBranch()
                .FullyProcessed(_block4D8).IsKeptOnBranch()
                .FullyProcessed(_block5D10).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_to_same_length()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessed(_block3D6).BecomesNewHead()
                .FullyProcessed(_blockB2D4).IsKeptOnBranch()
                .FullyProcessed(_blockB3D8).BecomesNewHead();
        }

        [Test]
        public void Can_reorganize_to_shorter_path()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessed(_block2D4).BecomesNewHead()
                .FullyProcessed(_block3D6).BecomesNewHead()
                .FullyProcessed(_blockC2D100).BecomesNewHead();
        }

        [Test]
        public void Can_change_branch_on_invalid_block()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .FullyProcessed(_block1D2).BecomesNewHead()
                .FullyProcessedFail(_block2D4).IsDeletedAsInvalid()
                .FullyProcessed(_blockB2D4).BecomesNewHead();
        }
        
        [Test]
        public void Can_change_branch_on_invalid_block_when_invalid_branch_is_in_the_queue()
        {
            When.ProcessingBlocks
                .FullyProcessed(_block0).BecomesGenesis()
                .Suggested(_block1D2)
                .Suggested(_block2D4)
                .Suggested(_block3D6)
                .Suggested(_block4D8)
                .Recovered(_block1D2)
                .Recovered(_block2D4)
                .Recovered(_block3D6)
                .Processed(_block1D2).AndThen
                .ProcessedFail(_block2D4).AndThen
                .FullyProcessed(_blockB2D4).BecomesNewHead();
        }
    }
}