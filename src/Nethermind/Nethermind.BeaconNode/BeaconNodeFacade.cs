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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class BeaconNodeFacade : IBeaconNodeApi
    {
        private readonly BlockProducer _blockProducer;
        private readonly IClientVersion _clientVersion;
        private readonly IForkChoice _forkChoice;
        private readonly ILogger<BeaconNodeFacade> _logger;
        private readonly INetworkPeering _networkPeering;
        private readonly IStore _store;
        private readonly ValidatorAssignments _validatorAssignments;

        public BeaconNodeFacade(
            ILogger<BeaconNodeFacade> logger,
            IClientVersion clientVersion,
            IForkChoice forkChoice,
            IStore store,
            INetworkPeering networkPeering,
            ValidatorAssignments validatorAssignments,
            BlockProducer blockProducer)
        {
            _logger = logger;
            _clientVersion = clientVersion;
            _forkChoice = forkChoice;
            _store = store;
            _networkPeering = networkPeering;
            _validatorAssignments = validatorAssignments;
            _blockProducer = blockProducer;
        }

        public async Task<ApiResponse<ulong>> GetGenesisTimeAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!_store.IsInitialized)
                {
                    // Beacon chain is currently syncing or waiting for genesis.
                    return ApiResponse.Create<ulong>(StatusCode.InternalError);
                }

                BeaconState state = await GetHeadStateAsync().ConfigureAwait(false);
                return ApiResponse.Create(StatusCode.Success, state.GenesisTime);
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorGetGenesisTime(_logger, ex);
                throw;
            }
        }

        public async Task<ApiResponse<Fork>> GetNodeForkAsync(CancellationToken cancellationToken)
        {
            try
            {
                BeaconState state = await GetHeadStateAsync().ConfigureAwait(false);
                return ApiResponse.Create(StatusCode.Success, state.Fork);
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorGetFork(_logger, ex);
                throw;
            }
        }

        public Task<ApiResponse<string>> GetNodeVersionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var versionDescription = _clientVersion.Description;
                return Task.FromResult(ApiResponse.Create(StatusCode.Success, versionDescription));
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorGetVersion(_logger, ex);
                throw;
            }
        }

        public async Task<ApiResponse<Syncing>> GetSyncingAsync(CancellationToken cancellationToken)
        {
            try
            {
                Slot currentSlot = Slot.Zero;
                if (_store.IsInitialized)
                {
                    Root head = await _forkChoice.GetHeadAsync(_store).ConfigureAwait(false);
                    BeaconBlock block = (await _store.GetSignedBlockAsync(head).ConfigureAwait(false)).Message;
                    currentSlot = block.Slot;
                }

                Slot highestSlot = Slot.Max(currentSlot, _networkPeering.HighestPeerSlot);

                Slot startingSlot = _networkPeering.SyncStartingSlot;

                bool isSyncing = highestSlot > currentSlot;

                Syncing syncing = new Syncing(isSyncing, new SyncingStatus(startingSlot, currentSlot, highestSlot));

                return ApiResponse.Create(StatusCode.Success, syncing);
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorGetSyncing(_logger, ex);
                throw;
            }
        }

        public async Task<ApiResponse<BeaconBlock>> NewBlockAsync(Slot slot, BlsSignature randaoReveal,
            CancellationToken cancellationToken)
        {
            try
            {
                BeaconBlock unsignedBlock = await _blockProducer.NewBlockAsync(slot, randaoReveal, cancellationToken)
                    .ConfigureAwait(false);
                return ApiResponse.Create(StatusCode.Success, unsignedBlock);
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorNewBlock(_logger, ex);
                throw;
            }
        }

        public async Task<ApiResponse> PublishBlockAsync(SignedBeaconBlock signedBlock,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!_store.IsInitialized)
                {
                    return new ApiResponse(StatusCode.CurrentlySyncing);
                }

                bool acceptedLocally = false;
                try
                {
                    await _forkChoice.OnBlockAsync(_store, signedBlock).ConfigureAwait(false);
                    // TODO: validate as per honest validator spec and return true/false
                    acceptedLocally = true;
                }
                catch (Exception ex)
                {
                    if (_logger.IsWarn()) Log.BlockNotAcceptedLocally(_logger, signedBlock.Message, ex);
                }

                if (_logger.IsDebug()) LogDebug.PublishingBlockToNetwork(_logger, signedBlock.Message, null);
                await _networkPeering.PublishBeaconBlockAsync(signedBlock).ConfigureAwait(false);

                if (acceptedLocally)
                {
                    return new ApiResponse(StatusCode.Success);
                }
                else
                {
                    return new ApiResponse(StatusCode.BroadcastButFailedValidation);
                }
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorPublishBlock(_logger, ex);
                throw;
            }
        }

        public async Task<ApiResponse<IList<ValidatorDuty>>> ValidatorDutiesAsync(
            IList<BlsPublicKey> validatorPublicKeys,
            Epoch? epoch,
            CancellationToken cancellationToken)
        {
            if (validatorPublicKeys.Count < 1)
            {
                return ApiResponse.Create<IList<ValidatorDuty>>(StatusCode.InvalidRequest);
            }

            if (!_store.IsInitialized)
            {
                // Beacon chain is currently syncing or waiting for genesis.
                return ApiResponse.Create<IList<ValidatorDuty>>(StatusCode.CurrentlySyncing);
            }

            try
            {
                var validatorDuties = await _validatorAssignments.GetValidatorDutiesAsync(validatorPublicKeys, epoch);
                ApiResponse<IList<ValidatorDuty>> response = ApiResponse.Create(StatusCode.Success, validatorDuties);
                return response;
            }
            catch (ArgumentOutOfRangeException outOfRangeException) when (outOfRangeException.ParamName == "epoch")
            {
                return ApiResponse.Create<IList<ValidatorDuty>>(StatusCode.DutiesNotAvailableForRequestedEpoch);
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorValidatorDuties(_logger, ex);
                throw;
            }
        }

        private async Task<BeaconState> GetHeadStateAsync()
        {
            Root head = await _forkChoice.GetHeadAsync(_store).ConfigureAwait(false);
            BeaconState state = await _store.GetBlockStateAsync(head).ConfigureAwait(false);

            return state;
        }
    }
}