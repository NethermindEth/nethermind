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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.OApiClient;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.Configuration;
using Nethermind.Logging.Microsoft;
using Attestation = Nethermind.Core2.Containers.Attestation;
using AttestationData = Nethermind.BeaconNode.Containers.AttestationData;
using AttesterSlashing = Nethermind.BeaconNode.Containers.AttesterSlashing;
using BeaconBlock = Nethermind.BeaconNode.Containers.BeaconBlock;
using BeaconBlockBody = Nethermind.BeaconNode.Containers.BeaconBlockBody;
using BeaconBlockHeader = Nethermind.BeaconNode.Containers.BeaconBlockHeader;
using Checkpoint = Nethermind.BeaconNode.Containers.Checkpoint;
using Deposit = Nethermind.BeaconNode.Containers.Deposit;
using Eth1Data = Nethermind.BeaconNode.Containers.Eth1Data;
using Fork = Nethermind.Core2.Containers.Fork;
using IndexedAttestation = Nethermind.BeaconNode.Containers.IndexedAttestation;
using ProposerSlashing = Nethermind.BeaconNode.Containers.ProposerSlashing;
using ValidatorDuty = Nethermind.BeaconNode.ValidatorDuty;

namespace Nethermind.HonestValidator.Services
{
    public class BeaconNodeProxy : IBeaconNodeApi
    {
        private readonly ILogger _logger;
        private readonly IOptionsMonitor<BeaconNodeConnection> _beaconNodeConnectionOptions;
        private readonly IBeaconNodeOApiClientFactory _oapiClientFactory;
        static SemaphoreSlim _connectionAttemptSemaphore = new SemaphoreSlim(1, 1);
        private IBeaconNodeOApiClient? _oapiClient;
        private int _connectionIndex = -1;

        public BeaconNodeProxy(ILogger<BeaconNodeProxy> logger, 
            IOptionsMonitor<BeaconNodeConnection> beaconNodeConnectionOptions,
            IBeaconNodeOApiClientFactory oapiClientFactory)
        {
            _logger = logger;
            _beaconNodeConnectionOptions = beaconNodeConnectionOptions;
            _oapiClientFactory = oapiClientFactory;
        }
        
        // The proxy needs to take care of this (i.e. transparent to worker)
        // Not connected: (remote vs local)
        // connect to beacon node (priority order)
        // if not connected, wait and try next

        public async Task<string> GetNodeVersionAsync(CancellationToken cancellationToken)
        {
            string? result = null;
            await ClientOperationWithRetry(async (oapiClient, innerCancellationToken) =>
            {
                result = await oapiClient.VersionAsync(innerCancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            return result!;
        }

        public async Task<ulong> GetGenesisTimeAsync(CancellationToken cancellationToken)
        {
            ulong result = 0;
            await ClientOperationWithRetry(async (oapiClient, innerCancellationToken) =>
            {
                result = await oapiClient.TimeAsync(innerCancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            return result;
        }

        public Task<bool> GetIsSyncingAsync(CancellationToken cancellationToken)
        {
            throw new System.NotImplementedException();
        }

        public async Task<Fork> GetNodeForkAsync(CancellationToken cancellationToken)
        {
            Response2 result = null;
            await ClientOperationWithRetry(async (oapiClient, innerCancellationToken) =>
            {
                result = await oapiClient.ForkAsync(innerCancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            Fork fork = new Fork(
                new ForkVersion(result.Fork.Previous_version),
                new ForkVersion(result.Fork.Current_version), 
                new Epoch((ulong) result.Fork.Epoch)
            );

            return fork;
        }

        public async IAsyncEnumerable<ValidatorDuty> ValidatorDutiesAsync(IEnumerable<BlsPublicKey> validatorPublicKeys,
            Epoch epoch, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            IEnumerable<byte[]> validator_pubkeys = validatorPublicKeys.Select(x => x.Bytes);
            ulong? epochValue = (epoch != Epoch.None) ? (ulong?) epoch : null;

            ICollection<Nethermind.BeaconNode.OApiClient.ValidatorDuty>? result = null;
            await ClientOperationWithRetry(async (oapiClient, innerCancellationToken) =>
            {
                result = await oapiClient.DutiesAsync(validator_pubkeys, epochValue, innerCancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            foreach (var value in result!)
            {
                var validatorPublicKey = new BlsPublicKey(value.Validator_pubkey);
                var proposalSlot = value.Block_proposal_slot.HasValue
                    ? new Slot(value.Block_proposal_slot.Value)
                    : Slot.None;
                var validatorDuty = new ValidatorDuty(validatorPublicKey, new Slot(value.Attestation_slot),
                    new Shard(value.Attestation_shard), proposalSlot);
                yield return validatorDuty;
            }
        }

        public async Task<BeaconBlock> NewBlockAsync(Slot slot, BlsSignature randaoReveal, CancellationToken cancellationToken)
        {
            ulong slotValue = (ulong) slot;
            byte[] randaoRevealBytes = randaoReveal.Bytes;
            
            BeaconNode.OApiClient.BeaconBlock result = null;
            await ClientOperationWithRetry(async (oapiClient, innerCancellationToken) =>
            {
                result = await oapiClient.BlockAsync(slotValue, randaoRevealBytes, innerCancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);

            BeaconBlock beaconBlock = new BeaconBlock(
                new Slot((ulong)result.Slot),
                new Hash32(Bytes.FromHexString(result.Parent_root)),
                new Hash32(Bytes.FromHexString(result.State_root)),
                new BeaconBlockBody(
                    new BlsSignature(result.Body.Randao_reveal), 
                    new Eth1Data(
                        new Hash32(result.Body.Eth1_data.Deposit_root),
                        (ulong)result.Body.Eth1_data.Deposit_count,
                        new Hash32(result.Body.Eth1_data.Block_hash)
                        ), 
                    new Bytes32(result.Body.Graffiti), 
                    result.Body.Proposer_slashings.Select(x => new ProposerSlashing(
                        new ValidatorIndex((ulong)x.Proposer_index),
                        MapBeaconBlockHeader(x.Header_1),
                        MapBeaconBlockHeader(x.Header_2)
                        )),
                    result.Body.Attester_slashings.Select(x => new AttesterSlashing(
                        MapIndexedAttestation(x.Attestation_1),
                        MapIndexedAttestation(x.Attestation_2)
                    )),
                    result.Body.Attestations.Select(x => 
                        new BeaconNode.Containers.Attestation(
                            new BitArray(x.Aggregation_bits),
                            MapAttestationData(x.Data),
                            new BitArray(x.Custody_bits),
                            new BlsSignature(x.Signature)
                        )
                    ),
                    new Deposit[0],
                    new VoluntaryExit[0]),
                BlsSignature.Empty
            );

            return beaconBlock;
        }

        public Task<bool> PublishBlockAsync(BeaconBlock signedBlock, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        private async Task ClientOperationWithRetry(Func<IBeaconNodeOApiClient, CancellationToken, Task> clientOperation, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                IBeaconNodeOApiClient? localClient = _oapiClient;
                if (!(localClient is null))
                {
                    try
                    {
                        await clientOperation(localClient, cancellationToken).ConfigureAwait(false);
                        // exit loop and complete on success
                        break;
                    }
                    catch (HttpRequestException ex)
                    {
                        // Only null out if the same client is still there (i.e. no one else has replaced)
                        IBeaconNodeOApiClient? exchangeResult = Interlocked.CompareExchange(ref _oapiClient, null, localClient);
                        if (exchangeResult == localClient)
                        {
                            if (_logger.IsWarn()) Log.NodeConnectionFailed(_logger, localClient.BaseUrl, ex);
                        }
                    }
                }

                // take turns trying the first connection
                await _connectionAttemptSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    // this routine's turn to try and connect (if no one else has) 
                    if (_oapiClient is null)
                    {
                        // create new client
                        _connectionIndex++; 
                        BeaconNodeConnection beaconNodeConnection = _beaconNodeConnectionOptions.CurrentValue;
                        if (_connectionIndex >= beaconNodeConnection.RemoteUrls.Length)
                        {
                            _connectionIndex = 0;
                            if (_logger.IsWarn())
                                Log.AllNodeConnectionsFailing(_logger, beaconNodeConnection.RemoteUrls.Length,
                                    beaconNodeConnection.ConnectionFailureLoopMillisecondsDelay, null);
                            await Task.Delay(beaconNodeConnection.ConnectionFailureLoopMillisecondsDelay, cancellationToken).ConfigureAwait(false);
                        }
                        string baseUrl = beaconNodeConnection.RemoteUrls[_connectionIndex];
                        if (_logger.IsDebug())
                            LogDebug.AttemptingConnectionToNode(_logger, baseUrl, _connectionIndex, null);
                        IBeaconNodeOApiClient newClient = _oapiClientFactory.CreateClient(baseUrl);
                        
                        // check if it works
                        await clientOperation(newClient, cancellationToken).ConfigureAwait(false);

                        // success! set the client, and if not the first, set the connection index to restart from first
                        if (_logger.IsInfo()) Log.NodeConnectionSuccess(_logger, baseUrl, _connectionIndex, null);
                        _oapiClient = newClient;
                        if (_connectionIndex > 0)
                        {
                            _connectionIndex = -1;
                        }
                        // exit loop and complete on success
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    // Continue
                }
                finally
                {
                    _connectionAttemptSemaphore.Release();
                }
            }
        }
        
        private static IndexedAttestation MapIndexedAttestation(BeaconNode.OApiClient.IndexedAttestation indexedAttestation)
        {
            return new IndexedAttestation(
                indexedAttestation.Custody_bit_0_indices.Select(y => new ValidatorIndex((ulong)y)),
                indexedAttestation.Custody_bit_1_indices.Select(y => new ValidatorIndex((ulong)y)),
                MapAttestationData(indexedAttestation.Data),
                new BlsSignature(Bytes.FromHexString(indexedAttestation.Signature))
            );
        }

        private static AttestationData MapAttestationData(BeaconNode.OApiClient.AttestationData attestationData)
        {
            // NOTE: This mapping isn't right, spec changes (sharding)
            return new AttestationData(
                Slot.None,
                CommitteeIndex.None, 
                new Hash32(attestationData.Beacon_block_root), 
                new Checkpoint(
                    new Epoch((ulong)attestationData.Source_epoch), 
                    new Hash32(attestationData.Source_root) 
                ),
                new Checkpoint(
                    new Epoch((ulong)attestationData.Target_epoch), 
                    new Hash32(attestationData.Target_root) 
                )
            );
        }

        private static BeaconBlockHeader MapBeaconBlockHeader(BeaconNode.OApiClient.BeaconBlockHeader value)
        {
            return new BeaconBlockHeader(
                new Slot((ulong)value.Slot),
                new Hash32(Bytes.FromHexString(value.Parent_root)), 
                new Hash32(Bytes.FromHexString(value.State_root)), 
                new Hash32(Bytes.FromHexString(value.Body_root)),
                new BlsSignature(Bytes.FromHexString(value.Signature))
            );
        }
        
    }
}