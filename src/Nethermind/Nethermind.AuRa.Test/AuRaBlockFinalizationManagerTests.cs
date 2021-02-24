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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaBlockFinalizationManagerTests
    {
        private IChainLevelInfoRepository _chainLevelInfoRepository;
        private IBlockProcessor _blockProcessor;
        private IValidatorStore _validatorStore;
        private ILogManager _logManager;
        private IValidSealerStrategy _validSealerStrategy;

        [SetUp]
        public void Initialize()
        {
            _chainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();
            _blockProcessor = Substitute.For<IBlockProcessor>();
            _validatorStore = Substitute.For<IValidatorStore>();
            _logManager = LimboLogs.Instance;
            _validSealerStrategy = Substitute.For<IValidSealerStrategy>();

            _validatorStore.GetValidators(Arg.Any<long?>()).Returns(new Address[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC});
            
            Rlp.Decoders[typeof(BlockInfo)] = new BlockInfoDecoder(true);
        }

        [Test]
        public void correctly_initializes_lastFinalizedBlock()
        {
            var blockTreeBuilder = Build.A.BlockTree().OfChainLength(3, 1, 1);
            FinalizeToLevel(1, blockTreeBuilder.ChainLevelInfoRepository);
            
            var finalizationManager = new AuRaBlockFinalizationManager(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager);
            finalizationManager.LastFinalizedBlockLevel.Should().Be(1);
        }

        private void FinalizeToLevel(long upperLevel, ChainLevelInfoRepository chainLevelInfoRepository)
        {
            for (long i = 0; i <= upperLevel; i++)
            {
                var level = chainLevelInfoRepository.LoadLevel(i);
                if (level?.MainChainBlock != null)
                {
                    level.MainChainBlock.IsFinalized = true;
                    chainLevelInfoRepository.PersistLevel(i, level);
                }
            }
        }

        public static IEnumerable FinalizingTests
        {
            get
            {
                yield return new TestCaseData(10, long.MaxValue, new[] {TestItem.AddressA, TestItem.AddressB}, 1);
                yield return new TestCaseData(10, long.MaxValue, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 1);
                yield return new TestCaseData(10, long.MaxValue, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD}, 2);
                
                yield return new TestCaseData(10, 0, new[] {TestItem.AddressA}, 0);
                yield return new TestCaseData(10, 0, new[] {TestItem.AddressA, TestItem.AddressB}, 1);
                yield return new TestCaseData(10, 0, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 2);
                yield return new TestCaseData(10, 0, TestItem.Addresses.Take(10).ToArray(), 6);
            }
        }

        [TestCaseSource(nameof(FinalizingTests))]
        public void correctly_finalizes_blocks_in_chain(int chainLength, long twoThirdsMajorityTransition, Address[] blockCreators, int notFinalizedExpectedCount)
        {
            _validatorStore.GetValidators(Arg.Any<long?>()).Returns(blockCreators);
            
            var blockTreeBuilder = Build.A.BlockTree();
            HashSet<BlockHeader> finalizedBlocks = new HashSet<BlockHeader>();
            
            var finalizationManager = new AuRaBlockFinalizationManager(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager, twoThirdsMajorityTransition);
            finalizationManager.BlocksFinalized += (sender, args) =>
            {
                foreach (var block in args.FinalizedBlocks)
                {
                    finalizedBlocks.Add(block);
                }
            };
            
            blockTreeBuilder.OfChainLength(chainLength, 0, 0, blockCreators);

            var start = 0;
            for (int i = start; i < chainLength; i++)
            {
                var blockHash = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i).MainChainBlock.BlockHash;
                var block = blockTreeBuilder.TestObject.FindBlock(blockHash, BlockTreeLookupOptions.None);
                _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(block, Array.Empty<TxReceipt>()));
            }

            var isBlockFinalized = Enumerable.Range(start, chainLength).Select(i => blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i).MainChainBlock.IsFinalized);
            var expected = Enumerable.Range(start, chainLength).Select(i => i < chainLength - notFinalizedExpectedCount);
            finalizedBlocks.Count.Should().Be(chainLength - notFinalizedExpectedCount);
            isBlockFinalized.Should().BeEquivalentTo(expected);
        }
        
        [Test]
        public void correctly_finalizes_blocks_in_already_in_chain_on_initialize()
        {
            var count = 2;
            var blockTreeBuilder = Build.A.BlockTree().OfChainLength(count, 0, 0, TestItem.AddressA, TestItem.AddressB);
            var finalizationManager = new AuRaBlockFinalizationManager(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager);

            IEnumerable<bool> result = Enumerable.Range(0, count).Select(i => blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i).MainChainBlock.IsFinalized);
            result.Should().BeEquivalentTo(new[] {true, false});
        }
        
        [TestCase(2, 4, ExpectedResult = new[] {1, 3, 1, 0})]
        [TestCase(1, 4, ExpectedResult = new[] {1, 3, 3, 1})]
        [TestCase(4, 5, ExpectedResult = new[] {1, 3, 1, 0, 0})]
        public int[] correctly_finalizes_blocks_on_reorganisations(int validators, int chainLength)
        {
            _validatorStore.GetValidators(Arg.Any<long?>()).Returns(TestItem.Addresses.Take(validators).ToArray());
            
            void ProcessBlock(BlockTreeBuilder blockTreeBuilder1, int level, int index)
            {
                var blockHash = blockTreeBuilder1.ChainLevelInfoRepository.LoadLevel(level).BlockInfos[index].BlockHash;
                var block = blockTreeBuilder1.TestObject.FindBlock(blockHash, BlockTreeLookupOptions.None);
                _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(block, Array.Empty<TxReceipt>()));
            }

            Block genesis = Build.A.Block.Genesis.TestObject;
            var blockTreeBuilder = Build.A.BlockTree(genesis);

            var finalizationManager = new AuRaBlockFinalizationManager(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager);
            
            blockTreeBuilder
                .OfChainLength(out Block headBlock, chainLength, 1, 0, TestItem.Addresses.Take(validators).ToArray())
                .OfChainLength(out Block alternativeHeadBlock, chainLength, 0, splitFrom: 2, TestItem.Addresses.Skip(validators).Take(validators).ToArray());
            
            for (int i = 0; i < chainLength - 1; i++)
            {
                ProcessBlock(blockTreeBuilder, i, 0);
            }

            for (int i = 1; i < chainLength - 1; i++)
            {
                ProcessBlock(blockTreeBuilder, i, 1);
            }

            ProcessBlock(blockTreeBuilder, chainLength - 1, 0);
            
            var finalizedBLocks = Enumerable.Range(0, chainLength)
                .Select(i => blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i).BlockInfos.Select((b, j) => b.IsFinalized ? j + 1 : 0).Sum())
                .ToArray();
            return finalizedBLocks;
        }

        public static IEnumerable GetLastFinalizedByTests
        {
            get
            {
                yield return new TestCaseData(2, new[] {TestItem.AddressA, TestItem.AddressB}, 2) {ExpectedResult = 0};
                yield return new TestCaseData(10, new[] {TestItem.AddressA, TestItem.AddressB}, 2) {ExpectedResult = 8};
                yield return new TestCaseData(10, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 3) {ExpectedResult = 7};
                yield return new TestCaseData(10, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 4) {ExpectedResult = 0};
                yield return new TestCaseData(10, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 2) {ExpectedResult = 8};
                yield return new TestCaseData(100, TestItem.Addresses.Take(30).ToArray(), 30) {ExpectedResult = 70};
            }
        }
        
        [TestCaseSource(nameof(GetLastFinalizedByTests))]
        public long GetLastFinalizedBy_test(int chainLength, Address[] beneficiaries, int minForFinalization)
        {
            SetupValidators(minForFinalization, beneficiaries);
            var blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength, 0, 0, beneficiaries);
            var blockTree = blockTreeBuilder.TestObject;
            var finalizationManager = new AuRaBlockFinalizationManager(blockTree, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager);

            var result = finalizationManager.GetLastLevelFinalizedBy(blockTree.Head.Hash);
            return result;
        }

        private void SetupValidators(int minForFinalization, params Address[] beneficiaries)
        {
            var validators = beneficiaries.Union(TestItem.Addresses.TakeLast(Math.Max(0, minForFinalization - 1) * 2 - beneficiaries.Length)).ToArray();
            _validatorStore.GetValidators(Arg.Any<long?>()).Returns(validators);
        }
        
        public static IEnumerable GetFinalizationLevelTests
        {
            get
            {
                yield return new TestCaseData(2, new[] {TestItem.AddressA, TestItem.AddressB}, 2, 2) {ExpectedResult = null};
                yield return new TestCaseData(10, new[] {TestItem.AddressA, TestItem.AddressB}, 2, 5) {ExpectedResult = 6};
                yield return new TestCaseData(10, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 3, 5) {ExpectedResult = 7};
                yield return new TestCaseData(10, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD}, 4, 5) {ExpectedResult = 8};
                yield return new TestCaseData(100, TestItem.Addresses.Take(30).ToArray(), 30, 60) {ExpectedResult = 89};
            }
        }
        
        [TestCaseSource(nameof(GetFinalizationLevelTests))]
        public long? GetFinalizationLevel_tests(int chainLength, Address[] beneficiaries, int minForFinalization, long level)
        {
            SetupValidators(minForFinalization, beneficiaries);
            var blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength, 0, 0, beneficiaries);
            var blockTree = blockTreeBuilder.TestObject;
            var finalizationManager = new AuRaBlockFinalizationManager(blockTree, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager);

            var result = finalizationManager.GetFinalizationLevel(level);
            return result;
        }

        [TestCase(2, 11, 10, ExpectedResult = 11)]
        [TestCase(3, 11, 10, ExpectedResult = null)]
        [TestCase(3, 20, 10, ExpectedResult = 12)]
        public long? GetFinalizationLevel_when_before_pivot_and_not_synced(int minForFinalization, long bestKnownBlock, long level)
        {
            SetupValidators(minForFinalization);
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.BestKnownNumber.Returns(bestKnownBlock);
            var finalizationManager = new AuRaBlockFinalizationManager(
                blockTree, 
                Substitute.For<IChainLevelInfoRepository>(), 
                _blockProcessor, 
                _validatorStore, 
                _validSealerStrategy, 
                _logManager);

            return finalizationManager.GetFinalizationLevel(level);
        }
        
        [TestCase(10, 4, 5, false)]
        [TestCase(10, 4, 5, true)]
        public void correctly_de_finalizes_blocks_on_block_reprocessing(int chainLength, int rerun, int validatorCount, bool twoThirdsMajorityTransition)
        {
            Address[] blockCreators = TestItem.Addresses.Take(validatorCount).ToArray();
            _validatorStore.GetValidators(Arg.Any<long?>()).Returns(blockCreators);
            
            var blockTreeBuilder = Build.A.BlockTree();
            var finalizationManager = new AuRaBlockFinalizationManager(
                blockTreeBuilder.TestObject, 
                blockTreeBuilder.ChainLevelInfoRepository, 
                _blockProcessor, 
                _validatorStore, 
                _validSealerStrategy,
                _logManager,
                twoThirdsMajorityTransition ? 0 : long.MaxValue);

            blockTreeBuilder.OfChainLength(chainLength, 0, 0, blockCreators);
            FinalizeToLevel(chainLength, blockTreeBuilder.ChainLevelInfoRepository);

            List<Block> blocks = Enumerable.Range(1, rerun)
                .Select(i => blockTreeBuilder.TestObject.FindBlock(chainLength - i, BlockTreeLookupOptions.None))
                .Reverse()
                .ToList();
                
            _blockProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(blocks));

            int majority = (twoThirdsMajorityTransition ? (validatorCount - 1) * 2 / 3 : (validatorCount - 1) / 2) + 1;
            for (int i = 1; i < rerun + majority; i++)
            {
                blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(chainLength - i).MainChainBlock.IsFinalized.Should().BeFalse();
            }

            blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(chainLength - rerun - majority - 1).MainChainBlock.IsFinalized.Should().BeTrue();
        }
    }
}
