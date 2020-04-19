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

            _validatorStore.GetValidators().Returns(new Address[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC});
            
            Rlp.Decoders[typeof(BlockInfo)] = new BlockInfoDecoder(true);
        }

        private void BuildChainLevelTree(params ChainLevelInfo[] levels)
        {
            for (var index = 0; index < levels.Length; index++)
            {
                var element = levels[index];
                _chainLevelInfoRepository.LoadLevel(index).Returns(element);
            }
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

        [Test]
        public void correctly_finalizes_blocks_in_chain()
        {
            var count = 10;
            var blockTreeBuilder = Build.A.BlockTree();
            HashSet<BlockHeader> finalizedBlocks = new HashSet<BlockHeader>();
            
            var finalizationManager = new AuRaBlockFinalizationManager(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager);
            finalizationManager.BlocksFinalized += (sender, args) =>
            {
                foreach (var block in args.FinalizedBlocks)
                {
                    finalizedBlocks.Add(block);
                }
            };
            
            blockTreeBuilder.OfChainLength(count, 0, 0, TestItem.AddressA, TestItem.AddressB);

            var start = 0;
            for (int i = start; i < count; i++)
            {
                var blockHash = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i).MainChainBlock.BlockHash;
                var block = blockTreeBuilder.TestObject.FindBlock(blockHash, BlockTreeLookupOptions.None);
                _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(block));
            }

            var result = Enumerable.Range(start, count).Select(i => blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i).MainChainBlock.IsFinalized);
            var expected = Enumerable.Range(start, count).Select(i => i != count - 1);
            finalizedBlocks.Count.Should().Be(count - 1);
            result.Should().BeEquivalentTo(expected);
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
            _validatorStore.GetValidators().Returns(TestItem.Addresses.Take(validators).ToArray());
            
            void ProcessBlock(BlockTreeBuilder blockTreeBuilder1, int level, int index)
            {
                var blockHash = blockTreeBuilder1.ChainLevelInfoRepository.LoadLevel(level).BlockInfos[index].BlockHash;
                var block = blockTreeBuilder1.TestObject.FindBlock(blockHash, BlockTreeLookupOptions.None);
                _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(block));
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
            SetupValidators(beneficiaries, minForFinalization);
            var blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength, 0, 0, beneficiaries);
            var blockTree = blockTreeBuilder.TestObject;
            var finalizationManager = new AuRaBlockFinalizationManager(blockTree, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager);

            var result = finalizationManager.GetLastLevelFinalizedBy(blockTree.Head.Hash);
            return result;
        }
        
        public static IEnumerable GetFinalizedLevelTests
        {
            get
            {
                yield return new TestCaseData(2, 1, new[] {TestItem.AddressA, TestItem.AddressB}, 2) {ExpectedResult = null};
                yield return new TestCaseData(10, 9, new[] {TestItem.AddressA, TestItem.AddressB}, 2) {ExpectedResult = null};
                yield return new TestCaseData(10, 8, new[] {TestItem.AddressA, TestItem.AddressB}, 2) {ExpectedResult = 9};
                yield return new TestCaseData(10, 3, new[] {TestItem.AddressA, TestItem.AddressB}, 2) {ExpectedResult = 4};
                yield return new TestCaseData(10, 3, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 2) {ExpectedResult = 4};
                yield return new TestCaseData(10, 3, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 3) {ExpectedResult = 5};
                yield return new TestCaseData(10, 3, new[] {TestItem.AddressA, TestItem.AddressB, TestItem.AddressC}, 4) {ExpectedResult = null};
            }
        }
        
        [TestCaseSource(nameof(GetFinalizedLevelTests))]
        public long? GetFinalizedLevel_test(int chainLength, int levelToCheck, Address[] beneficiaries, int minForFinalization)
        {
            SetupValidators(beneficiaries, minForFinalization);
            _validSealerStrategy.IsValidSealer(Arg.Any<IList<Address>>(), Arg.Any<Address>(), Arg.Any<long>()).Returns(c => beneficiaries.GetItemRoundRobin(c.Arg<long>()) == c.Arg<Address>());
            var blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength, 0, 0, beneficiaries);
            var blockTree = blockTreeBuilder.TestObject;
            var finalizationManager = new AuRaBlockFinalizationManager(blockTree, blockTreeBuilder.ChainLevelInfoRepository, _blockProcessor, _validatorStore, _validSealerStrategy, _logManager);

            var result = finalizationManager.GetFinalizedLevel(levelToCheck);
            return result;
        }

        private void SetupValidators(Address[] beneficiaries, int minForFinalization)
        {
            var validators = beneficiaries.Union(TestItem.Addresses.TakeLast(Math.Max(0, minForFinalization - 1) * 2 - beneficiaries.Length)).ToArray();
            _validatorStore.GetValidators().Returns(validators);
        }
    }
}