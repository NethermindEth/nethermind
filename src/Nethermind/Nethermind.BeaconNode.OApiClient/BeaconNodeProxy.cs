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
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.BeaconNode.OApiClient.Configuration;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApiClient
{
    public class BeaconNodeProxy : IBeaconNodeApi
    {
        private readonly IOptionsMonitor<BeaconNodeConnection> _beaconNodeConnectionOptions;
        private static readonly SemaphoreSlim _connectionAttemptSemaphore = new SemaphoreSlim(1, 1);
        private int _connectionIndex = -1;
        private readonly ILogger _logger;
        private IBeaconNodeOApiClient? _oapiClient;
        private readonly IBeaconNodeOApiClientFactory _oapiClientFactory;

        public BeaconNodeProxy(ILogger<BeaconNodeProxy> logger,
            IOptionsMonitor<BeaconNodeConnection> beaconNodeConnectionOptions,
            IBeaconNodeOApiClientFactory oapiClientFactory)
        {
            _logger = logger;
            _beaconNodeConnectionOptions = beaconNodeConnectionOptions;
            _oapiClientFactory = oapiClientFactory;
        }

        // public async Task<ulong> GetGenesisTimeAsync(CancellationToken cancellationToken)
        // {
        //     ulong result = 0;
        //     await ClientOperationWithRetry(
        //         async (oapiClient, innerCancellationToken) =>
        //         {
        //             result = await oapiClient.TimeAsync(innerCancellationToken).ConfigureAwait(false);
        //         }, cancellationToken).ConfigureAwait(false);
        //
        //     return result;
        // }
        //

        public async Task<ApiResponse<Core2.Containers.Fork>> GetNodeForkAsync(CancellationToken cancellationToken)
        {
            Response2? result = null;
            await ClientOperationWithRetry(
                async (oapiClient, innerCancellationToken) =>
                {
                    result = await oapiClient.ForkAsync(innerCancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
        
            Core2.Containers.Fork fork = new Core2.Containers.Fork(
                new ForkVersion(result!.Fork.Previous_version),
                new ForkVersion(result!.Fork.Current_version),
                new Epoch((ulong) result!.Fork.Epoch)
            );
        
            return ApiResponse.Create(StatusCode.Success, fork);
        }
        
        // // The proxy needs to take care of this (i.e. transparent to worker)
        // // Not connected: (remote vs local)
        // // connect to beacon node (priority order)
        // // if not connected, wait and try next
        //
        // public async Task<string> GetNodeVersionAsync(CancellationToken cancellationToken)
        // {
        //     string? result = null;
        //     await ClientOperationWithRetry(
        //         async (oapiClient, innerCancellationToken) =>
        //         {
        //             result = await oapiClient.VersionAsync(innerCancellationToken).ConfigureAwait(false);
        //         }, cancellationToken).ConfigureAwait(false);
        //
        //     return result!;
        // }
        //
        // public Task<Syncing> GetSyncingAsync(CancellationToken cancellationToken)
        // {
        //     throw new NotImplementedException();
        // }
        //
        // public async Task<Core2.Containers.BeaconBlock> NewBlockAsync(Slot slot, BlsSignature randaoReveal,
        //     CancellationToken cancellationToken)
        // {
        //     ulong slotValue = (ulong) slot;
        //     byte[] randaoRevealBytes = randaoReveal.Bytes;
        //
        //     BeaconBlock? result = null;
        //     await ClientOperationWithRetry(
        //         async (oapiClient, innerCancellationToken) =>
        //         {
        //             result = await oapiClient.BlockAsync(slotValue, randaoRevealBytes, innerCancellationToken)
        //                 .ConfigureAwait(false);
        //         }, cancellationToken).ConfigureAwait(false);
        //
        //     BeaconBlock oapiBeaconBlock = result!;
        //     Core2.Containers.BeaconBlock beaconBlock = new Core2.Containers.BeaconBlock(
        //         new Slot((ulong) oapiBeaconBlock.Slot),
        //         new Root(Bytes.FromHexString(oapiBeaconBlock.Parent_root)),
        //         new Root(Bytes.FromHexString(oapiBeaconBlock.State_root)),
        //         new Core2.Containers.BeaconBlockBody(
        //             new BlsSignature(oapiBeaconBlock.Body.Randao_reveal),
        //             new Eth1Data(
        //                 new Root(oapiBeaconBlock.Body.Eth1_data.Deposit_root),
        //                 (ulong) oapiBeaconBlock.Body.Eth1_data.Deposit_count,
        //                 new Bytes32(oapiBeaconBlock.Body.Eth1_data.Block_hash)
        //             ),
        //             new Bytes32(oapiBeaconBlock.Body.Graffiti),
        //             oapiBeaconBlock.Body.Proposer_slashings.Select(x => new ProposerSlashing(
        //                 new ValidatorIndex((ulong) x.Proposer_index),
        //                 new SignedBeaconBlockHeader(MapBeaconBlockHeader(x.Header_1), BlsSignature.Zero),
        //                 new SignedBeaconBlockHeader(MapBeaconBlockHeader(x.Header_2), BlsSignature.Zero)
        //             )),
        //             oapiBeaconBlock.Body.Attester_slashings.Select(x => new AttesterSlashing(
        //                 MapIndexedAttestation(x.Attestation_1),
        //                 MapIndexedAttestation(x.Attestation_2)
        //             )),
        //             oapiBeaconBlock.Body.Attestations.Select(x =>
        //                 new Core2.Containers.Attestation(
        //                     new BitArray(x.Aggregation_bits),
        //                     MapAttestationData(x.Data),
        //                     new BlsSignature(x.Signature)
        //                 )
        //             ),
        //             oapiBeaconBlock.Body.Deposits.Select(x =>
        //                 new Deposit(
        //                     x.Proof.Select(y => new Bytes32(y)),
        //                     new DepositData(
        //                         new BlsPublicKey(x.Data.Pubkey),
        //                         new Bytes32(x.Data.Withdrawal_credentials),
        //                         new Gwei((ulong) x.Data.Amount),
        //                         new BlsSignature(x.Data.Signature)
        //                     )
        //                 )
        //             ),
        //             oapiBeaconBlock.Body.Voluntary_exits.Select(x =>
        //                 new SignedVoluntaryExit(
        //                     new VoluntaryExit(
        //                         new Epoch((ulong) x.Epoch),
        //                         new ValidatorIndex((ulong) x.Validator_index)
        //                     ),
        //                     BlsSignature.Zero
        //                 )
        //             )
        //         )
        //     );
        //
        //     return beaconBlock;
        // }
        //
        // public async Task<bool> PublishBlockAsync(SignedBeaconBlock signedBlock, CancellationToken cancellationToken)
        // {
        //     var block = signedBlock.Message;
        //     BeaconBlock data = new BeaconBlock()
        //     {
        //         Slot = block.Slot,
        //         Parent_root = block.ParentRoot.ToString(),
        //         State_root = block.StateRoot.ToString(),
        //         Body = new BeaconBlockBody()
        //         {
        //             Randao_reveal = block.Body!.RandaoReveal.AsSpan().ToArray(),
        //             Eth1_data = new Eth1_data()
        //             {
        //                 Block_hash = block.Body.Eth1Data.BlockHash.AsSpan().ToArray(),
        //                 Deposit_count = block.Body.Eth1Data.DepositCount,
        //                 Deposit_root = block.Body.Eth1Data.DepositRoot.AsSpan().ToArray()
        //             },
        //             Graffiti = block.Body.Graffiti.AsSpan().ToArray(),
        //             Proposer_slashings = block.Body.ProposerSlashings.Select(x => new Proposer_slashings()
        //             {
        //                 Header_1 = MapBeaconBlockHeader(x.SignedHeader1.Message),
        //                 Header_2 = MapBeaconBlockHeader(x.SignedHeader2.Message),
        //                 Proposer_index = x.ProposerIndex
        //             }).ToList(),
        //             Attester_slashings = block.Body.AttesterSlashings.Select(x => new Attester_slashings()
        //             {
        //                 Attestation_1 = MapIndexedAttestation(x.Attestation1),
        //                 Attestation_2 = MapIndexedAttestation(x.Attestation2)
        //             }).ToList(),
        //             Attestations = block.Body.Attestations.Select(x => new Attestations()
        //             {
        //                 Signature = x.Signature.Bytes,
        //                 Aggregation_bits = x.AggregationBits.Cast<byte>().ToArray(),
        //                 Custody_bits = new byte[0],
        //                 Data = MapAttestationData(x.Data)
        //             }).ToList(),
        //             Voluntary_exits = block.Body.VoluntaryExits.Select(x => new Voluntary_exits()
        //             {
        //                 Validator_index = x.Message.ValidatorIndex,
        //                 Epoch = x.Message.Epoch,
        //                 Signature = x.Signature.Bytes
        //             }).ToList(),
        //             Deposits = block.Body.Deposits.Select((x, index) => new Deposits()
        //             {
        //                 Index = (ulong) index,
        //                 Proof = x.Proof.Select(y => y.AsSpan().ToArray()).ToList(),
        //                 Data = new Data()
        //                 {
        //                     Amount = x.Data.Amount,
        //                     Pubkey = x.Data.PublicKey.Bytes,
        //                     Signature = x.Data.Signature.Bytes,
        //                     Withdrawal_credentials = x.Data.WithdrawalCredentials.AsSpan().ToArray()
        //                 }
        //             }).ToList(),
        //         }
        //     };
        //
        //     await ClientOperationWithRetry(
        //         async (oapiClient, innerCancellationToken) =>
        //         {
        //             await oapiClient.Block2Async(data, signedBlock.Signature, innerCancellationToken)
        //                 .ConfigureAwait(false);
        //         }, cancellationToken).ConfigureAwait(false);
        //
        //     // TODO: Parse 202 result separate from 200 result
        //
        //     return true;
        // }
        //
        // public async IAsyncEnumerable<Core2.Api.ValidatorDuty> ValidatorDutiesAsync(
        //     IEnumerable<BlsPublicKey> validatorPublicKeys,
        //     Epoch epoch, [EnumeratorCancellation] CancellationToken cancellationToken)
        // {
        //     IEnumerable<byte[]> validator_pubkeys = validatorPublicKeys.Select(x => x.Bytes);
        //     ulong? epochValue = (epoch != Epoch.None) ? (ulong?) epoch : null;
        //
        //     ICollection<ValidatorDuty>? result = null;
        //     await ClientOperationWithRetry(
        //         async (oapiClient, innerCancellationToken) =>
        //         {
        //             result = await oapiClient.DutiesAsync(validator_pubkeys, epochValue, innerCancellationToken)
        //                 .ConfigureAwait(false);
        //         }, cancellationToken).ConfigureAwait(false);
        //
        //     foreach (var value in result!)
        //     {
        //         var validatorPublicKey = new BlsPublicKey(value.Validator_pubkey);
        //         var proposalSlot = value.Block_proposal_slot.HasValue
        //             ? new Slot(value.Block_proposal_slot.Value)
        //             : Slot.None;
        //         var validatorDuty = new Core2.Api.ValidatorDuty(validatorPublicKey, new Slot(value.Attestation_slot),
        //             new Shard(value.Attestation_shard), proposalSlot);
        //         yield return validatorDuty;
        //     }
        // }
        
        private async Task ClientOperationWithRetry(
            Func<IBeaconNodeOApiClient, CancellationToken, Task> clientOperation, CancellationToken cancellationToken)
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
                        IBeaconNodeOApiClient? exchangeResult =
                            Interlocked.CompareExchange(ref _oapiClient, null, localClient);
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
                            await Task.Delay(beaconNodeConnection.ConnectionFailureLoopMillisecondsDelay,
                                cancellationToken).ConfigureAwait(false);
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
        
        // private static Core2.Containers.AttestationData MapAttestationData(AttestationData attestationData)
        // {
        //     // NOTE: This mapping isn't right, spec changes (sharding)
        //     return new Core2.Containers.AttestationData(
        //         Slot.None,
        //         CommitteeIndex.None,
        //         new Root(attestationData.Beacon_block_root),
        //         new Checkpoint(
        //             new Epoch((ulong) attestationData.Source_epoch),
        //             new Root(attestationData.Source_root)
        //         ),
        //         new Checkpoint(
        //             new Epoch((ulong) attestationData.Target_epoch),
        //             new Root(attestationData.Target_root)
        //         )
        //     );
        // }
        //
        // private static AttestationData MapAttestationData(Core2.Containers.AttestationData attestationData)
        // {
        //     // NOTE: This mapping isn't right, spec changes (sharding)
        //     return new AttestationData()
        //     {
        //         Beacon_block_root = attestationData.BeaconBlockRoot.AsSpan().ToArray(),
        //         Crosslink = new Crosslink(),
        //         Source_epoch = attestationData.Source.Epoch,
        //         Source_root = attestationData.Source.Root.AsSpan().ToArray(),
        //         Target_epoch = attestationData.Target.Epoch,
        //         Target_root = attestationData.Target.Root.AsSpan().ToArray()
        //     };
        // }
        //
        // private static Core2.Containers.BeaconBlockHeader MapBeaconBlockHeader(BeaconBlockHeader value)
        // {
        //     return new Core2.Containers.BeaconBlockHeader(
        //         new Slot((ulong) value.Slot),
        //         new Root(Bytes.FromHexString(value.Parent_root)),
        //         new Root(Bytes.FromHexString(value.State_root)),
        //         new Root(Bytes.FromHexString(value.Body_root))
        //     );
        // }
        //
        // private static BeaconBlockHeader MapBeaconBlockHeader(Core2.Containers.BeaconBlockHeader value)
        // {
        //     return new BeaconBlockHeader()
        //     {
        //         Body_root = value.BodyRoot.ToString(),
        //         Parent_root = value.ParentRoot.ToString(),
        //         Slot = value.Slot,
        //         State_root = value.StateRoot.ToString()
        //     };
        // }
        //
        // private static Core2.Containers.IndexedAttestation MapIndexedAttestation(IndexedAttestation indexedAttestation)
        // {
        //     return new Core2.Containers.IndexedAttestation(
        //         indexedAttestation.Custody_bit_0_indices.Select(y => new ValidatorIndex((ulong) y)),
        //         MapAttestationData(indexedAttestation.Data),
        //         new BlsSignature(Bytes.FromHexString(indexedAttestation.Signature))
        //     );
        // }
        //
        // private static IndexedAttestation MapIndexedAttestation(Core2.Containers.IndexedAttestation indexedAttestation)
        // {
        //     return new IndexedAttestation()
        //     {
        //         Signature = indexedAttestation.Signature.ToString(),
        //         Custody_bit_0_indices = indexedAttestation.AttestingIndices.Select(y => (int) y).ToList(),
        //         Custody_bit_1_indices = new int[0],
        //         Data = MapAttestationData(indexedAttestation.Data)
        //     };
        // }
        
        public Task<ApiResponse<string>> GetNodeVersionAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<ApiResponse<ulong>> GetGenesisTimeAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<ApiResponse<Syncing>> GetSyncingAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<ApiResponse<IList<Core2.Api.ValidatorDuty>>> ValidatorDutiesAsync(IList<BlsPublicKey> validatorPublicKeys, Epoch? epoch, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<ApiResponse<Core2.Containers.BeaconBlock>> NewBlockAsync(Slot slot, BlsSignature randaoReveal, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        public Task<ApiResponse> PublishBlockAsync(SignedBeaconBlock signedBlock, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}