// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.Clique;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Clique.Test
{
    public class CliqueHealthHintServiceTests
    {
        [Test]
        public void GetBlockProcessorAndProducerIntervalHint_returns_expected_result(
            [ValueSource(nameof(BlockProcessorIntervalHintTestCases))]
            BlockProcessorIntervalHint test)
        {
            ISnapshotManager snapshotManager = Substitute.For<ISnapshotManager>();
            snapshotManager.GetLastSignersCount().Returns(test.ValidatorsCount);
            IHealthHintService healthHintService = new CliqueHealthHintService(snapshotManager, test.ChainSpec);
            ulong? actualProcessing = healthHintService.MaxSecondsIntervalForProcessingBlocksHint();
            ulong? actualProducing = healthHintService.MaxSecondsIntervalForProducingBlocksHint();
            Assert.That(actualProcessing, Is.EqualTo(test.ExpectedProcessingHint));
            Assert.That(actualProducing, Is.EqualTo(test.ExpectedProducingHint));
        }

        public class BlockProcessorIntervalHint
        {
            public ChainSpec ChainSpec { get; set; }

            public ulong ValidatorsCount { get; set; }

            public ulong? ExpectedProcessingHint { get; set; }

            public ulong? ExpectedProducingHint { get; set; }

            public override string ToString() =>
                $"SealEngineType: {ChainSpec.SealEngineType}, ValidatorsCount: {ValidatorsCount}, ExpectedProcessingHint: {ExpectedProcessingHint}, ExpectedProducingHint: {ExpectedProducingHint}";
        }

        public static IEnumerable<BlockProcessorIntervalHint> BlockProcessorIntervalHintTestCases
        {
            get
            {
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() { SealEngineType = SealEngineType.Clique, Clique = new CliqueParameters() { Period = 15 } },
                    ExpectedProcessingHint = 60,
                    ExpectedProducingHint = 30
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() { SealEngineType = SealEngineType.Clique, Clique = new CliqueParameters() { Period = 23 } },
                    ExpectedProcessingHint = 92,
                    ExpectedProducingHint = 46
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ValidatorsCount = 10,
                    ChainSpec = new ChainSpec() { SealEngineType = SealEngineType.Clique, Clique = new CliqueParameters() { Period = 23 } },
                    ExpectedProcessingHint = 92,
                    ExpectedProducingHint = 460
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ValidatorsCount = 2,
                    ChainSpec = new ChainSpec() { SealEngineType = SealEngineType.Clique, Clique = new CliqueParameters() { Period = 10 } },
                    ExpectedProcessingHint = 40,
                    ExpectedProducingHint = 40
                };
            }
        }
    }
}
