// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Services;
using Nethermind.Consensus.AuRa.Validators;

namespace Nethermind.Consensus.AuRa.Services
{
    public class AuraHealthHintService(
        IAuRaStepCalculator auRaStepCalculator,
        IValidatorStore validatorStore) : IHealthHintService
    {
        private readonly IAuRaStepCalculator _auRaStepCalculator = auRaStepCalculator;
        private readonly IValidatorStore _validatorStore = validatorStore;

        public ulong? MaxSecondsIntervalForProcessingBlocksHint() => CurrentStepDuration() * HealthHintConstants.ProcessingSafetyMultiplier;

        public ulong? MaxSecondsIntervalForProducingBlocksHint() => (ulong)Math.Max(_validatorStore.GetValidators().Length, 1) * CurrentStepDuration() * HealthHintConstants.ProducingSafetyMultiplier;

        private uint CurrentStepDuration() => Math.Max((uint)_auRaStepCalculator.CurrentStepDuration, 1);
    }
}
