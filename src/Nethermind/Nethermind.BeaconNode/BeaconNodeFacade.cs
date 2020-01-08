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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Services;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class BeaconNodeFacade : IBeaconNodeApi
    {
        private readonly ILogger<BeaconNodeFacade> _logger;
        private readonly ClientVersion _clientVersion;
        private readonly ForkChoice _forkChoice;
        private readonly IStoreProvider _storeProvider;
        private readonly INetworkPeering _networkPeering;
        private readonly ValidatorAssignments _validatorAssignments;
        private readonly BlockProducer _blockProducer;

        public BeaconNodeFacade(
            ILogger<BeaconNodeFacade> logger,
            ClientVersion clientVersion,
            ForkChoice forkChoice,
            IStoreProvider storeProvider,
            INetworkPeering networkPeering,
            ValidatorAssignments validatorAssignments,
            BlockProducer blockProducer)
        {
            _logger = logger;
            _clientVersion = clientVersion;
            _forkChoice = forkChoice;
            _storeProvider = storeProvider;
            _networkPeering = networkPeering;
            _validatorAssignments = validatorAssignments;
            _blockProducer = blockProducer;
        }

        public Task<string> GetNodeVersionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var versionDescription = _clientVersion.Description;
                return Task.FromResult(versionDescription);
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorGetVersion(_logger, ex);
                throw;
            }
        }

        public async Task<ulong> GetGenesisTimeAsync(CancellationToken cancellationToken)
        {
            try
            {
                BeaconState state = await GetHeadStateAsync().ConfigureAwait(false);
                return state.GenesisTime;
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorGetGenesisTime(_logger, ex);
                throw;
            }
        }

        public Task<bool> GetIsSyncingAsync(CancellationToken cancellationToken)
        {
            throw new System.NotImplementedException();
        }

        public async Task<Fork> GetNodeForkAsync(CancellationToken cancellationToken)
        {
            try
            {
                BeaconState state = await GetHeadStateAsync().ConfigureAwait(false);
                return state.Fork;
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorGetFork(_logger, ex);
                throw;
            }
        }

        public async IAsyncEnumerable<ValidatorDuty> ValidatorDutiesAsync(IEnumerable<BlsPublicKey> validatorPublicKeys,
            Epoch epoch, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // TODO: Rather than check one by one (each of which loops through potentially all slots for the epoch), optimise by either checking multiple, or better possibly caching or pre-calculating
            foreach (BlsPublicKey validatorPublicKey in validatorPublicKeys)
            {
                ValidatorDuty validatorDuty;
                try
                {
                    validatorDuty =
                        await _validatorAssignments.GetValidatorDutyAsync(validatorPublicKey, epoch)
                            .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_logger.IsWarn()) Log.ApiErrorValidatorDuties(_logger, ex);
                    throw;
                }
                yield return validatorDuty;
            }
        }

        public async Task<BeaconBlock> NewBlockAsync(Slot slot, BlsSignature randaoReveal, CancellationToken cancellationToken)
        {
            try
            {
                return await _blockProducer.NewBlockAsync(slot, randaoReveal).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.ApiErrorNewBlock(_logger, ex);
                throw;
            }
        }

        public async Task<bool> PublishBlockAsync(BeaconBlock signedBlock, CancellationToken cancellationToken)
        {
            if (!_storeProvider.TryGetStore(out IStore? retrievedStore))
            {
                throw new Exception("Beacon chain is currently syncing or waiting for genesis.");
            }

            IStore store = retrievedStore!;

            bool acceptedLocally = false;
            try
            {
                await _forkChoice.OnBlockAsync(store, signedBlock).ConfigureAwait(false);
                // TODO: validate as per honest validator spec and return true/false
                acceptedLocally = true;
            }
            catch (Exception ex)
            {
                if (_logger.IsWarn()) Log.BlockNotAcceptedLocally(_logger, signedBlock, ex);
            }
            
            await _networkPeering.PublishBeaconBlockAsync(signedBlock).ConfigureAwait(false);
            
            return acceptedLocally;
        }

        private async Task<BeaconState> GetHeadStateAsync()
        {
            if (!_storeProvider.TryGetStore(out IStore? retrievedStore))
            {
                throw new Exception("Beacon chain is currently syncing or waiting for genesis.");
            }

            IStore store = retrievedStore!;
            Hash32 head = await _forkChoice.GetHeadAsync(store).ConfigureAwait(false);
            BeaconState state = await store.GetBlockStateAsync(head).ConfigureAwait(false);

            return state;
        }
    }
}