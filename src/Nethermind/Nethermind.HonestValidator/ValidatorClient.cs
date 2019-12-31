﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.Configuration;
using Nethermind.HonestValidator.Services;
using Nethermind.Logging.Microsoft;

namespace Nethermind.HonestValidator
{
    public class ValidatorClient
    {
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly IBeaconNodeApi _beaconNodeApi;
        private readonly IValidatorKeyProvider _validatorKeyProvider;
        private readonly BeaconChain _beaconChain;
        private readonly ValidatorState _validatorState;

        public ValidatorClient(ILogger<ValidatorClient> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IBeaconNodeApi beaconNodeApi,
            IValidatorKeyProvider validatorKeyProvider,
            BeaconChain beaconChain)
        {
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _beaconNodeApi = beaconNodeApi;
            _validatorKeyProvider = validatorKeyProvider;
            _beaconChain = beaconChain;
            
            _validatorState = new ValidatorState();
        }
        
        public Slot GetCurrentSlot(BeaconChain beaconChain)
        {
            ulong slotValue = (beaconChain.Time - beaconChain.GenesisTime) / _timeParameterOptions.CurrentValue.SecondsPerSlot;
            return new Slot(slotValue);
        }
        
        public async Task OnTickAsync(BeaconChain beaconChain, ulong time, CancellationToken cancellationToken)
        {
            // update time
            Slot previousSlot = GetCurrentSlot(beaconChain);
            await beaconChain.SetTimeAsync(time).ConfigureAwait(false);
            Slot currentSlot = GetCurrentSlot(beaconChain);
            
            // TODO: attestation is done 1/3 way through slot

            // Not a new slot, return
            bool isNewSlot = currentSlot > previousSlot;
            if (!isNewSlot)
            {
                return;
            }
            
            Epoch currentEpoch = ComputeEpochAtSlot(currentSlot);

            await UpdateDutiesAsync(currentEpoch, cancellationToken).ConfigureAwait(false);
            
            // Check start of each slot
            // Get duties
            //await _validatorClient.UpdateDutiesAsync(time);

            //await _validatorClient.ProcessProposerDutiesAsync(time);
                        
            // If proposer, get block, sign block, return to node
            // Retry if not successful; need to queue this up to send immediately if connection issue. (or broadcast?)
                        
            // If upcoming attester, join (or change) topics
            // Subscribe to topics
                        
            // Attest 1/3 way through slot
        }

        public async Task UpdateDutiesAsync(Epoch epoch, CancellationToken cancellationToken)
        {
            IEnumerable<BlsPublicKey> publicKeys = _validatorKeyProvider.GetPublicKeys();

            IAsyncEnumerable<ValidatorDuty> validatorDuties = _beaconNodeApi.ValidatorDutiesAsync(publicKeys, epoch, cancellationToken);

            await foreach (ValidatorDuty validatorDuty in validatorDuties.ConfigureAwait(false))
            {
                Slot? currentAttestationSlot =
                    _validatorState.AttestationSlot.GetValueOrDefault(validatorDuty.ValidatorPublicKey);
                Shard? currentAttestationShard =
                    _validatorState.AttestationShard.GetValueOrDefault(validatorDuty.ValidatorPublicKey);
                if (validatorDuty.AttestationSlot != currentAttestationSlot ||
                    validatorDuty.AttestationShard != currentAttestationShard)
                {
                    _validatorState.SetAttestationDuty(validatorDuty.ValidatorPublicKey, validatorDuty.AttestationSlot,
                        validatorDuty.AttestationShard);
                    if (_logger.IsInfo())
                        Log.ValidatorDutyAttestationChanged(_logger, validatorDuty.ValidatorPublicKey, epoch,
                            validatorDuty.AttestationSlot, validatorDuty.AttestationShard, null);
                }
            }

            await foreach (ValidatorDuty validatorDuty in validatorDuties.ConfigureAwait(false))
            {
                Slot? currentProposalSlot = _validatorState.ProposalSlot.GetValueOrDefault(validatorDuty.ValidatorPublicKey);
                if (validatorDuty.BlockProposalSlot != Slot.None &&
                    validatorDuty.BlockProposalSlot != currentProposalSlot)
                {
                    _validatorState.SetProposalDuty(validatorDuty.ValidatorPublicKey, validatorDuty.BlockProposalSlot);
                    if (_logger.IsInfo())
                        Log.ValidatorDutyProposalChanged(_logger, validatorDuty.ValidatorPublicKey, epoch,
                            validatorDuty.BlockProposalSlot, null);
                }
            }
        }

//        public async Task ProcessProposerDutiesAsync(ulong time)
//        {
//            throw new System.NotImplementedException();
//        }
        
        /// <summary>
        /// Return the epoch number of ``slot``.
        /// </summary>
        public Epoch ComputeEpochAtSlot(Slot slot)
        {
            return new Epoch(slot / _timeParameterOptions.CurrentValue.SlotsPerEpoch);
        }

    }
}