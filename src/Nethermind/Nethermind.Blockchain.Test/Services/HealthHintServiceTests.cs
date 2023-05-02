// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Services;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Services
{
    public class HealthHintServiceTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void GetBlockProcessorAndProducerIntervalHint_returns_expected_result(
            [ValueSource(nameof(BlockProcessorIntervalHintTestCases))]
            BlockProcessorIntervalHint test)
        {
            IHealthHintService healthHintService = new HealthHintService(test.ChainSpec);
            ulong? actualProcessing = healthHintService.MaxSecondsIntervalForProcessingBlocksHint();
            ulong? actualProducing = healthHintService.MaxSecondsIntervalForProducingBlocksHint();
            Assert.That(actualProcessing, Is.EqualTo(test.ExpectedProcessingHint));
            Assert.That(actualProducing, Is.EqualTo(test.ExpectedProducingHint));
        }

        public class BlockProcessorIntervalHint
        {
            public ChainSpec ChainSpec { get; set; }

            public ulong? ExpectedProcessingHint { get; set; }

            public ulong? ExpectedProducingHint { get; set; }

            public override string ToString() =>
                $"SealEngineType: {ChainSpec.SealEngineType}, ExpectedProcessingHint: {ExpectedProcessingHint}, ExpectedProducingHint: {ExpectedProducingHint}";
        }

        public static IEnumerable<BlockProcessorIntervalHint> BlockProcessorIntervalHintTestCases
        {
            get
            {
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() { SealEngineType = SealEngineType.NethDev, }
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() { SealEngineType = SealEngineType.Ethash },
                    ExpectedProcessingHint = 180
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() { SealEngineType = "Interval" }
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() { SealEngineType = SealEngineType.None }
                };
            }
        }
    }
}
