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
// 

using System.Collections.Generic;
using Nethermind.Blockchain;
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
            Assert.AreEqual(test.ExpectedProcessingHint, actualProcessing);
            Assert.AreEqual(test.ExpectedProducingHint, actualProducing);
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
                    ChainSpec = new ChainSpec() {SealEngineType = SealEngineType.Clique, Clique = new CliqueParameters() { Period = 15}},
                    ExpectedProcessingHint = 60,
                    ExpectedProducingHint = 30
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() {SealEngineType = SealEngineType.Clique, Clique = new CliqueParameters() { Period = 23}},
                    ExpectedProcessingHint = 92,
                    ExpectedProducingHint = 46
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ValidatorsCount = 10,
                    ChainSpec = new ChainSpec() {SealEngineType = SealEngineType.Clique, Clique = new CliqueParameters() { Period = 23}},
                    ExpectedProcessingHint = 92,
                    ExpectedProducingHint = 460
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ValidatorsCount = 2,
                    ChainSpec = new ChainSpec() {SealEngineType = SealEngineType.Clique, Clique = new CliqueParameters() { Period = 10}},
                    ExpectedProcessingHint = 40,
                    ExpectedProducingHint = 40
                };
            }
        }
    }
}
