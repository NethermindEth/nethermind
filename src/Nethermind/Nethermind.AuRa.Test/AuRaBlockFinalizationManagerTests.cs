// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.State.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaBlockFinalizationManagerTests
    {
        private IBranchProcessor _blockProcessor;
        private IValidatorStore _validatorStore;
        private ILogManager _logManager;

        [SetUp]
        public void Initialize()
        {
            Substitute.For<IChainLevelInfoRepository>();
            _blockProcessor = Substitute.For<IBranchProcessor>();
            _validatorStore = Substitute.For<IValidatorStore>();
            _logManager = LimboLogs.Instance;

            _validatorStore.GetValidators(Arg.Any<ulong?>()).Returns([TestItem.AddressA, TestItem.AddressB, TestItem.AddressC]);
        }

        [Test]
        public void correctly_initializes_lastFinalizedBlock()
        {
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(3, 1, 1);
            FinalizeToLevel(1, blockTreeBuilder.ChainLevelInfoRepository);

            AuRaBlockFinalizationManager finalizationManager = new(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _validatorStore, _logManager);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);
            Assert.That(finalizationManager.LastFinalizedBlockLevel, Is.EqualTo(1));
        }

        [Test]
        public void repeated_SetMainBlockBranchProcessor_is_idempotent()
        {
            // The merge plugin can end up calling SetMainBlockBranchProcessor via the wrapper
            // as well. Initialization must be safe to invoke more than once.
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(3, 1, 1);
            FinalizeToLevel(1, blockTreeBuilder.ChainLevelInfoRepository);

            AuRaBlockFinalizationManager finalizationManager = new(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _validatorStore, _logManager);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);

            Assert.That(finalizationManager.LastFinalizedBlockLevel, Is.EqualTo(1));
        }

        private void FinalizeToLevel(ulong upperLevel, IChainLevelInfoRepository chainLevelInfoRepository)
        {
            for (ulong i = 0; i <= upperLevel; i++)
            {
                ChainLevelInfo? level = chainLevelInfoRepository.LoadLevel(i);
                if (level?.MainChainBlock is not null)
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
                yield return new TestCaseData(10, ulong.MaxValue, new[] { TestItem.AddressA, TestItem.AddressB }, 1);
                yield return new TestCaseData(10, ulong.MaxValue, new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC }, 1);
                yield return new TestCaseData(10, ulong.MaxValue, new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD }, 2);

                yield return new TestCaseData(10, 0UL, new[] { TestItem.AddressA }, 0);
                yield return new TestCaseData(10, 0UL, new[] { TestItem.AddressA, TestItem.AddressB }, 1);
                yield return new TestCaseData(10, 0UL, new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC }, 2);
                yield return new TestCaseData(10, 0UL, TestItem.Addresses.Take(10).ToArray(), 6);
            }
        }

        [TestCaseSource(nameof(FinalizingTests))]
        public void correctly_finalizes_blocks_in_chain(int chainLength, ulong twoThirdsMajorityTransition, Address[] blockCreators, int notFinalizedExpectedCount)
        {
            _validatorStore.GetValidators(Arg.Any<ulong?>()).Returns(blockCreators);

            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree();
            HashSet<BlockHeader> finalizedBlocks = [];

            AuRaBlockFinalizationManager finalizationManager = new(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _validatorStore, _logManager, twoThirdsMajorityTransition);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);
            finalizationManager.BlocksFinalized += (sender, args) =>
            {
                foreach (BlockHeader block in args.FinalizedBlocks)
                {
                    finalizedBlocks.Add(block);
                }
            };

            blockTreeBuilder.OfChainLength(chainLength, blockBeneficiaries: blockCreators);

            int start = 0;
            for (ulong i = 0; i < (ulong)chainLength; i++)
            {
                Hash256 blockHash = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i).MainChainBlock.BlockHash;
                Block? block = blockTreeBuilder.TestObject.FindBlock(blockHash, BlockTreeLookupOptions.None);
                _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(block, []));
            }

            IEnumerable<bool> isBlockFinalized = Enumerable.Range(start, chainLength).Select(i => blockTreeBuilder.ChainLevelInfoRepository.LoadLevel((ulong)i).MainChainBlock.IsFinalized);
            IEnumerable<bool> expected = Enumerable.Range(start, chainLength).Select(i => i < chainLength - notFinalizedExpectedCount);
            Assert.That(finalizedBlocks.Count, Is.EqualTo(chainLength - notFinalizedExpectedCount));
            Assert.That(isBlockFinalized, Is.EqualTo(expected));
        }

        [Test]
        public void correctly_finalizes_blocks_in_already_in_chain_on_initialize()
        {
            int count = 2;
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(count, blockBeneficiaries: [TestItem.AddressA, TestItem.AddressB]);
            AuRaBlockFinalizationManager finalizationManager = new(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _validatorStore, _logManager);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);

            IEnumerable<bool> result = Enumerable.Range(0, count).Select(i => blockTreeBuilder.ChainLevelInfoRepository.LoadLevel((ulong)i).MainChainBlock.IsFinalized);
            Assert.That(result, Is.EqualTo(new[] { true, false }));
        }

        [TestCase(2, 4, ExpectedResult = new[] { 1, 3, 1, 0 })]
        [TestCase(1, 4, ExpectedResult = new[] { 1, 3, 3, 1 })]
        [TestCase(4, 5, ExpectedResult = new[] { 1, 3, 1, 0, 0 })]
        public int[] correctly_finalizes_blocks_on_reorganisations(int validators, int chainLength)
        {
            _validatorStore.GetValidators(Arg.Any<ulong?>()).Returns(TestItem.Addresses.Take(validators).ToArray());

            void ProcessBlock(BlockTreeBuilder blockTreeBuilder1, int level, int index)
            {
                Hash256 blockHash = blockTreeBuilder1.ChainLevelInfoRepository.LoadLevel((ulong)level).BlockInfos[index].BlockHash;
                Block? block = blockTreeBuilder1.TestObject.FindBlock(blockHash, BlockTreeLookupOptions.None);
                _blockProcessor.BlockProcessed += Raise.EventWith(new BlockProcessedEventArgs(block, []));
            }

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis);

            AuRaBlockFinalizationManager finalizationManager = new(blockTreeBuilder.TestObject, blockTreeBuilder.ChainLevelInfoRepository, _validatorStore, _logManager);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);

            blockTreeBuilder
                .OfChainLength(out Block headBlock, chainLength, 1, blockBeneficiaries: TestItem.Addresses.Take(validators).ToArray())
                .OfChainLength(out Block alternativeHeadBlock, chainLength, 0, splitFrom: 2, blockBeneficiaries: TestItem.Addresses.Skip(validators).Take(validators).ToArray());

            for (int i = 0; i < chainLength - 1; i++)
            {
                ProcessBlock(blockTreeBuilder, i, 0);
            }

            for (int i = 1; i < chainLength - 1; i++)
            {
                ProcessBlock(blockTreeBuilder, i, 1);
            }

            ProcessBlock(blockTreeBuilder, chainLength - 1, 0);

            int[] finalizedBLocks = Enumerable.Range(0, chainLength)
                .Select(i => blockTreeBuilder.ChainLevelInfoRepository.LoadLevel((ulong)i).BlockInfos.Select((b, j) => b.IsFinalized ? j + 1 : 0).Sum())
                .ToArray();
            return finalizedBLocks;
        }

        public static IEnumerable GetLastFinalizedByTests
        {
            get
            {
                yield return new TestCaseData(2, new[] { TestItem.AddressA, TestItem.AddressB }, 2) { ExpectedResult = 0UL };
                yield return new TestCaseData(10, new[] { TestItem.AddressA, TestItem.AddressB }, 2) { ExpectedResult = 8UL };
                yield return new TestCaseData(10, new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC }, 3) { ExpectedResult = 7UL };
                yield return new TestCaseData(10, new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC }, 4) { ExpectedResult = 0UL };
                yield return new TestCaseData(10, new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC }, 2) { ExpectedResult = 8UL };
                yield return new TestCaseData(100, TestItem.Addresses.Take(30).ToArray(), 30) { ExpectedResult = 70UL };
            }
        }

        [TestCaseSource(nameof(GetLastFinalizedByTests))]
        public ulong GetLastFinalizedBy_test(int chainLength, Address[] beneficiaries, int minForFinalization)
        {
            SetupValidators(minForFinalization, beneficiaries);
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength, blockBeneficiaries: beneficiaries);
            BlockTree blockTree = blockTreeBuilder.TestObject;
            AuRaBlockFinalizationManager finalizationManager = new(blockTree, blockTreeBuilder.ChainLevelInfoRepository, _validatorStore, _logManager);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);

            ulong result = finalizationManager.GetLastLevelFinalizedBy(blockTree.Head.Hash);
            return result;
        }

        private void SetupValidators(int minForFinalization, params Address[] beneficiaries)
        {
            Address[] validators = beneficiaries.Union(TestItem.Addresses.TakeLast(Math.Max(0, minForFinalization - 1) * 2 - beneficiaries.Length)).ToArray();
            _validatorStore.GetValidators(Arg.Any<ulong?>()).Returns(validators);
        }

        public static IEnumerable GetFinalizationLevelTests
        {
            get
            {
                yield return new TestCaseData(2, new[] { TestItem.AddressA, TestItem.AddressB }, 2, 2UL) { ExpectedResult = null };
                yield return new TestCaseData(10, new[] { TestItem.AddressA, TestItem.AddressB }, 2, 5UL) { ExpectedResult = 6UL };
                yield return new TestCaseData(10, new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC }, 3, 5UL) { ExpectedResult = 7UL };
                yield return new TestCaseData(10, new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC, TestItem.AddressD }, 4, 5UL) { ExpectedResult = 8UL };
                yield return new TestCaseData(100, TestItem.Addresses.Take(30).ToArray(), 30, 60UL) { ExpectedResult = 89UL };
            }
        }

        [TestCaseSource(nameof(GetFinalizationLevelTests))]
        public ulong? GetFinalizationLevel_tests(int chainLength, Address[] beneficiaries, int minForFinalization, ulong level)
        {
            SetupValidators(minForFinalization, beneficiaries);
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength, blockBeneficiaries: beneficiaries);
            BlockTree blockTree = blockTreeBuilder.TestObject;
            AuRaBlockFinalizationManager finalizationManager = new(blockTree, blockTreeBuilder.ChainLevelInfoRepository, _validatorStore, _logManager);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);

            ulong? result = finalizationManager.GetFinalizationLevel(level);
            return result;
        }

        [TestCase(2, 11UL, 10UL, ExpectedResult = 11UL)]
        [TestCase(3, 11UL, 10UL, ExpectedResult = null)]
        [TestCase(3, 20UL, 10UL, ExpectedResult = 12UL)]
        public ulong? GetFinalizationLevel_when_before_pivot_and_not_synced(int minForFinalization, ulong bestKnownBlock, ulong level)
        {
            SetupValidators(minForFinalization);
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.BestKnownNumber.Returns(bestKnownBlock);
            AuRaBlockFinalizationManager finalizationManager = new(
                blockTree,
                Substitute.For<IChainLevelInfoRepository>(),
                _validatorStore,
                _logManager);

            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);

            return finalizationManager.GetFinalizationLevel(level);
        }

        [TestCase(10, 4, 5, false)]
        [TestCase(10, 4, 5, true)]
        public void correctly_de_finalizes_blocks_on_block_reprocessing(int chainLength, int rerun, int validatorCount, bool twoThirdsMajorityTransition)
        {
            Address[] blockCreators = TestItem.Addresses.Take(validatorCount).ToArray();
            _validatorStore.GetValidators(Arg.Any<ulong?>()).Returns(blockCreators);

            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree();
            AuRaBlockFinalizationManager finalizationManager = new(
                blockTreeBuilder.TestObject,
                blockTreeBuilder.ChainLevelInfoRepository,
                _validatorStore,
                _logManager,
                twoThirdsMajorityTransition ? 0UL : ulong.MaxValue);
            finalizationManager.SetMainBlockBranchProcessor(_blockProcessor);

            blockTreeBuilder.OfChainLength(chainLength, blockBeneficiaries: blockCreators);
            FinalizeToLevel((ulong)chainLength, blockTreeBuilder.ChainLevelInfoRepository);

            List<Block> blocks = Enumerable.Range(1, rerun)
                .Select(i => blockTreeBuilder.TestObject.FindBlock((ulong)(chainLength - i), BlockTreeLookupOptions.None))
                .Reverse()
                .ToList();

            _blockProcessor.BlocksProcessing += Raise.EventWith(new BlocksProcessingEventArgs(blocks));

            int majority = (twoThirdsMajorityTransition ? (validatorCount - 1) * 2 / 3 : (validatorCount - 1) / 2) + 1;
            for (int i = 1; i < rerun + majority; i++)
            {
                Assert.That(blockTreeBuilder.ChainLevelInfoRepository.LoadLevel((ulong)(chainLength - i)).MainChainBlock.IsFinalized, Is.False);
            }

            Assert.That(blockTreeBuilder.ChainLevelInfoRepository.LoadLevel((ulong)(chainLength - rerun - majority - 1)).MainChainBlock.IsFinalized, Is.True);
        }
    }
}
