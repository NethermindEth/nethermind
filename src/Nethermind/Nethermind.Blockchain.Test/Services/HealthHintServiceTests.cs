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
using Nethermind.Blockchain.Services;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Services
{
    public class HealthHintServiceTests
    {
        [Test]
        public void GetBlockProcessorAndProducerIntervalHint_returns_expected_result(
            [ValueSource(nameof(BlockProcessorIntervalHintTestCases))]
            BlockProcessorIntervalHint test)
        {
            IHealthHintService healthHintService = new HealthHintService(test.ChainSpec);
            ulong? actualProcessing = healthHintService.MaxSecondsIntervalForProcessingBlocksHint();
            ulong? actualProducing = healthHintService.MaxSecondsIntervalForProducingBlocksHint();
            Assert.AreEqual(test.ExpectedProcessingHint, actualProcessing);
            Assert.AreEqual(test.ExpectedProducingHint, actualProducing);
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
                    ChainSpec = new ChainSpec() {SealEngineType = SealEngineType.NethDev,}
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() {SealEngineType = SealEngineType.Ethash },
                    ExpectedProcessingHint = 180
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() {SealEngineType = "Interval" }
                };
                yield return new BlockProcessorIntervalHint()
                {
                    ChainSpec = new ChainSpec() {SealEngineType = SealEngineType.None }
                };
            }
        }
    }
}
