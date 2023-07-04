// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.AuRa.Validators;

namespace Nethermind.Consensus.AuRa.Services
{
    public class AuraHealthHintService : IHealthHintService
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator;
        private readonly IValidatorStore _validatorStore;

        public AuraHealthHintService(
            IAuRaStepCalculator auRaStepCalculator,
            IValidatorStore validatorStore)
        {
            _auRaStepCalculator = auRaStepCalculator;
            _validatorStore = validatorStore;
        }

        public ulong? MaxSecondsIntervalForProcessingBlocksHint()
        {
            return CurrentStepDuration() * HealthHintConstants.ProcessingSafetyMultiplier;
        }

        public ulong? MaxSecondsIntervalForProducingBlocksHint()
        {
            return (ulong)Math.Max(_validatorStore.GetValidators().Length, 1) * CurrentStepDuration() * HealthHintConstants.ProducingSafetyMultiplier;
        }

        private uint CurrentStepDuration()
        {
            return Math.Max((uint)_auRaStepCalculator.CurrentStepDuration, 1);
        }
    }
}
