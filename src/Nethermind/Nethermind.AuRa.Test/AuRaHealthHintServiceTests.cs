// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
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
            ManualTimestamper manualTimestamper = new(DateTime.Now);
            AuRaStepCalculator stepCalculator = new(new Dictionary<long, long>() { { 0, test.StepDuration } }, manualTimestamper, LimboLogs.Instance);
            IValidatorStore validatorStore = Substitute.For<IValidatorStore>();
            validatorStore.GetValidators().Returns(new Address[test.ValidatorsCount]);
            IHealthHintService healthHintService = new AuraHealthHintService(stepCalculator, validatorStore);
            ulong? actualProcessing = healthHintService.MaxSecondsIntervalForProcessingBlocksHint();
            ulong? actualProducing = healthHintService.MaxSecondsIntervalForProducingBlocksHint();
            Assert.That(actualProcessing, Is.EqualTo(test.ExpectedProcessingHint));
            Assert.That(actualProducing, Is.EqualTo(test.ExpectedProducingHint));
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
