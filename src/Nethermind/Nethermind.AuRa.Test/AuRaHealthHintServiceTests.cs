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

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.AuRa.Services;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaHealthHintServiceTests
    {
        [Test]
        public void GetBlockProcessorAndProducerIntervalHint_returns_expected_result(
            [ValueSource(nameof(BlockProcessorIntervalHintTestCases))]
            BlockProcessorIntervalHint test)
        {
            ManualTimestamper manualTimestamper = new ManualTimestamper(DateTime.Now);
            AuRaStepCalculator stepCalculator = new AuRaStepCalculator(new Dictionary<long, long>() {{0, test.StepDuration}}, manualTimestamper, LimboLogs.Instance);
            IValidatorStore validatorStore = Substitute.For<IValidatorStore>();
            validatorStore.GetValidators().Returns(new Address[test.ValidatorsCount]);
            IHealthHintService healthHintService = new AuraHealthHintService(stepCalculator, validatorStore);
            ulong? actualProcessing = healthHintService.MaxSecondsIntervalForProcessingBlocksHint();
            ulong? actualProducing = healthHintService.MaxSecondsIntervalForProducingBlocksHint();
            Assert.AreEqual(test.ExpectedProcessingHint, actualProcessing);
            Assert.AreEqual(test.ExpectedProducingHint, actualProducing);
        }

        public class BlockProcessorIntervalHint
        {
            public long StepDuration { get; set; }
            
            public long ValidatorsCount { get; set; }

            public ulong? ExpectedProcessingHint { get; set; }

            public ulong? ExpectedProducingHint { get; set; }

            public override string ToString() =>
                $"StepDuration: {StepDuration}, ValidatorsCount: {ValidatorsCount}, ExpectedProcessingHint: {ExpectedProcessingHint}, ExpectedProducingHint: {ExpectedProducingHint}";
        }

        public static IEnumerable<BlockProcessorIntervalHint> BlockProcessorIntervalHintTestCases
        {
            get
            {
                yield return new BlockProcessorIntervalHint()
                {
                    StepDuration = 1,
                    ExpectedProcessingHint = 4,
                    ExpectedProducingHint = 2
                };
                yield return new BlockProcessorIntervalHint()
                {
                    StepDuration = 2,
                    ValidatorsCount = 2,
                    ExpectedProcessingHint = 8,
                    ExpectedProducingHint = 8
                };
                yield return new BlockProcessorIntervalHint()
                {
                    StepDuration = 3,
                    ValidatorsCount = 2,
                    ExpectedProcessingHint = 12,
                    ExpectedProducingHint = 12
                };
                yield return new BlockProcessorIntervalHint()
                {
                    StepDuration = 4,
                    ValidatorsCount = 10,
                    ExpectedProcessingHint = 16,
                    ExpectedProducingHint = 80
                };
                yield return new BlockProcessorIntervalHint()
                {
                    StepDuration = 6,
                    ValidatorsCount = 12,
                    ExpectedProcessingHint = 24,
                    ExpectedProducingHint = 144
                };
            }
        }
    }
}
