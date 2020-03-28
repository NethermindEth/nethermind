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

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode.OApi
{
    public class BeaconNodeOApiAdapter : IBeaconNodeOApiController
    {
        private readonly ILogger _logger;
        private readonly IBeaconNodeApi _beaconNode;

        public BeaconNodeOApiAdapter(ILogger<BeaconNodeOApiAdapter> logger,
            IBeaconNodeApi beaconNode)
        {
            _logger = logger;
            _beaconNode = beaconNode;
        }
        
        // /// <summary>Publish a signed block.</summary>
        // /// <param name="beacon_block">The `BeaconBlock` object, as sent from the beacon node originally, but now with the signature field completed.</param>
        // /// <returns>The block was validated successfully and has been broadcast. It has also been integrated into the beacon node's database.</returns>
        // public async Task Block2Async(BeaconBlock beacon_block)
        // {
        //     if (_logger.IsInfo())
        //         Log.BlockPublished(_logger, beacon_block.Slot,
        //             Bytes.ToHexString(beacon_block.Body.Randao_reveal),
        //             beacon_block.Parent_root, beacon_block.State_root,
        //             Bytes.ToHexString(beacon_block.Body.Graffiti),
        //             beacon_block.Signature, null);
        //     
        //     Core2.Containers.BeaconBlock block = new Core2.Containers.BeaconBlock(
        //         new Slot((ulong) beacon_block.Slot),
        //         new Root(Bytes.FromHexString(beacon_block.Parent_root)), 
        //         new Root(Bytes.FromHexString(beacon_block.State_root)),
        //         new Core2.Containers.BeaconBlockBody(
        //             new BlsSignature(beacon_block.Body.Randao_reveal),
        //             new Eth1Data(
        //                 new Root(beacon_block.Body.Eth1_data.Deposit_root),
        //                 (ulong) beacon_block.Body.Eth1_data.Deposit_count,
        //                 new Bytes32(beacon_block.Body.Eth1_data.Block_hash)
        //             ),
        //             new Bytes32(beacon_block.Body.Graffiti),
        //             beacon_block.Body.Proposer_slashings.Select(x => new ProposerSlashing(
        //                 new ValidatorIndex((ulong) x.Proposer_index),
        //                 new SignedBeaconBlockHeader(MapBeaconBlockHeader(x.Header_1), BlsSignature.Zero),
        //                 new SignedBeaconBlockHeader(MapBeaconBlockHeader(x.Header_2), BlsSignature.Zero)
        //             )),
        //             beacon_block.Body.Attester_slashings.Select(x => new AttesterSlashing(
        //                 MapIndexedAttestation(x.Attestation_1),
        //                 MapIndexedAttestation(x.Attestation_2)
        //             )),
        //             beacon_block.Body.Attestations.Select(x =>
        //                 new Core2.Containers.Attestation(
        //                     new BitArray(x.Aggregation_bits),
        //                     MapAttestationData(x.Data),
        //                     new BlsSignature(x.Signature)
        //                 )
        //             ),
        //             beacon_block.Body.Deposits.Select(x =>
        //                 new Core2.Containers.Deposit(
        //                     x.Proof.Select(y => new Bytes32(y)),
        //                     new DepositData(
        //                         new BlsPublicKey(x.Data.Pubkey),
        //                         new Bytes32(x.Data.Withdrawal_credentials),
        //                         new Gwei((ulong) x.Data.Amount),
        //                         new BlsSignature(x.Data.Signature)
        //                     )
        //                 )
        //             ),
        //             beacon_block.Body.Voluntary_exits.Select(x =>
        //                 new Core2.Crypto.SignedVoluntaryExit(
        //                 new Core2.Containers.VoluntaryExit(
        //                     new Epoch((ulong) x.Epoch),
        //                     new ValidatorIndex((ulong) x.Validator_index)
        //                     ),
        //                     BlsSignature.Zero
        //                 )
        //             )
        //         )
        //     );
        //     SignedBeaconBlock signedBlock = new SignedBeaconBlock(block, new BlsSignature(Bytes.FromHexString(beacon_block.Signature)));
        //     bool acceptedLocally = await _beaconNode.PublishBlockAsync(signedBlock, CancellationToken.None);
        //     
        //     // TODO: return 200 or 202 based on whether accepted locally or not
        // }
        //
        // Task<Attestation> IBeaconNodeOApiController.AttestationAsync(byte[] validator_pubkey, int poc_bit, ulong slot, ulong shard)
        // {
        //     throw new NotImplementedException();
        // }
        //
        // public Task Attestation2Async(Attestation body)
        // {
        //     throw new NotImplementedException();
        // }
        //
        // /// <summary>Produce a new block, without signature.</summary>
        // /// <param name="slot">The slot for which the block should be proposed.</param>
        // /// <param name="randao_reveal">The validator's randao reveal value.</param>
        // /// <returns>Success response</returns>
        // public async Task<BeaconBlock> BlockAsync(ulong slot, byte[] randao_reveal)
        // {
        //     if (_logger.IsInfo()) Log.NewBlockRequested(_logger, slot, Bytes.ToHexString(randao_reveal), null);
        //     
        //     Core2.Containers.BeaconBlock data =
        //         await _beaconNode.NewBlockAsync(new Slot(slot), new BlsSignature(randao_reveal), CancellationToken.None);
        //     
        //     OApi.BeaconBlock result = new OApi.BeaconBlock()
        //     {
        //         Slot = (ulong)data.Slot,
        //         Parent_root = data.ParentRoot.ToString(),
        //         State_root = data.StateRoot.ToString(),
        //         Body = new OApi.BeaconBlockBody()
        //         {
        //             Randao_reveal = data.Body!.RandaoReveal.AsSpan().ToArray(),
        //             Eth1_data = new Eth1_data()
        //             {
        //                 Block_hash = data.Body.Eth1Data.BlockHash.AsSpan().ToArray(),
        //                 Deposit_count = data.Body.Eth1Data.DepositCount,
        //                 Deposit_root = data.Body.Eth1Data.DepositRoot.AsSpan().ToArray() 
        //             },
        //             Graffiti = data.Body.Graffiti.AsSpan().ToArray(),
        //             Proposer_slashings = data.Body.ProposerSlashings.Select(x => new Proposer_slashings()
        //             {
        //                 Header_1 = MapBeaconBlockHeader(x.SignedHeader1.Message),
        //                 Header_2 = MapBeaconBlockHeader(x.SignedHeader2.Message),
        //                 Proposer_index = x.ProposerIndex
        //             }).ToList(),
        //             Attester_slashings = data.Body.AttesterSlashings.Select(x => new Attester_slashings()
        //             {
        //                 Attestation_1 = MapIndexedAttestation(x.Attestation1),
        //                 Attestation_2 = MapIndexedAttestation(x.Attestation2)
        //             }).ToList(),
        //             Attestations = data.Body.Attestations.Select(x => new Attestations()
        //             {
        //                 Signature = x.Signature.Bytes,
        //                 Aggregation_bits = x.AggregationBits.Cast<byte>().ToArray(),
        //                 Custody_bits = new byte[0],
        //                 Data = MapAttestationData(x.Data)
        //             }).ToList(),
        //             Voluntary_exits = data.Body.VoluntaryExits.Select(x => new Voluntary_exits()
        //             {
        //                 Validator_index = x.Message.ValidatorIndex,
        //                 Epoch = x.Message.Epoch,
        //                 Signature = x.Signature.Bytes
        //             }).ToList(),
        //             Deposits = data.Body.Deposits.Select((x, index) => new Deposits()
        //             {
        //                 Index = (ulong)index,
        //                 Proof = x.Proof.Select(y => y.AsSpan().ToArray()).ToList(),
        //                 Data = new Data()
        //                 {
        //                     Amount = x.Data.Amount,
        //                     Pubkey = x.Data.PublicKey.Bytes,
        //                     Signature = x.Data.Signature.Bytes,
        //                     Withdrawal_credentials = x.Data.WithdrawalCredentials.AsSpan().ToArray()
        //                 }
        //             }).ToList(),
        //             Transfers = new List<Transfers>()
        //         }
        //     };
        //     return result;
        // }
        //
        // public Task<Validator> ValidatorAsync(byte[] pubkey)
        // {
        //     throw new NotImplementedException();
        // }
        //
        // /// <summary>Get validator duties for the requested validators.</summary>
        // /// <param name="validator_pubkeys">An array of hex-encoded BLS public keys</param>
        // /// <returns>Success response</returns>
        // public async Task<ICollection<ValidatorDuty>> DutiesAsync(System.Collections.Generic.IEnumerable<byte[]> validator_pubkeys, ulong? epoch)
        // {
        //     IEnumerable<BlsPublicKey> publicKeys = validator_pubkeys.Select(x => new BlsPublicKey(x));
        //     Epoch targetEpoch = epoch.HasValue ? new Epoch((ulong)epoch) : Epoch.None;
        //     var duties = _beaconNode.ValidatorDutiesAsync(publicKeys, targetEpoch, CancellationToken.None);
        //     List<ValidatorDuty> result = new List<ValidatorDuty>();
        //     await foreach(var duty in duties)
        //     {
        //         ValidatorDuty validatorDuty = new ValidatorDuty();
        //         validatorDuty.Validator_pubkey = duty.ValidatorPublicKey.Bytes;
        //         validatorDuty.Attestation_slot = duty.AttestationSlot;
        //         validatorDuty.Attestation_shard = (ulong)duty.AttestationShard;
        //         validatorDuty.Block_proposal_slot = duty.BlockProposalSlot == Slot.None ? null : (ulong?)duty.BlockProposalSlot;
        //         result.Add(validatorDuty);
        //     }
        //     return result;
        // }
        //
        // /// <summary>Get fork information from running beacon node.</summary>
        // /// <returns>Request successful</returns>
        // public async Task<Response2> ForkAsync()
        // {
        //     Core2.Containers.Fork fork = await _beaconNode.GetNodeForkAsync(CancellationToken.None);
        //     Response2 response2 = new Response2();
        //     // TODO: Not sure what chain ID should be.
        //     response2.Chain_id = 0;
        //     response2.Fork = new Fork();
        //     response2.Fork.Epoch = fork.Epoch;
        //     response2.Fork.Current_version = fork.CurrentVersion.AsSpan().ToArray();
        //     response2.Fork.Previous_version = fork.PreviousVersion.AsSpan().ToArray();
        //     return response2;
        // }
        //
        // /// <summary>Poll to see if the the beacon node is syncing.</summary>
        // /// <returns>Request successful</returns>
        // public async Task<Response> SyncingAsync()
        // {
        //     Response response = new Response();
        //     //response.Is_syncing = await _beaconNode.GetSyncingAsync(CancellationToken.None);
        //     response.Sync_status = new SyncingStatus();
        //     //response.Sync_status.Current_slot =
        //     return response;
        // }
        //
        // /// <summary>Get the genesis_time parameter from beacon node configuration.</summary>
        // /// <returns>Request successful</returns>
        // public async Task<ulong> TimeAsync()
        // {
        //     return await _beaconNode.GetGenesisTimeAsync(CancellationToken.None);
        // }
        //
        // /// <summary>Get version string of the running beacon node.</summary>
        // /// <returns>Request successful</returns>
        // public async Task<string> VersionAsync()
        // {
        //     return await _beaconNode.GetNodeVersionAsync(CancellationToken.None);
        // }
        //
        // private static Core2.Containers.IndexedAttestation MapIndexedAttestation(BeaconNode.OApi.IndexedAttestation indexedAttestation)
        // {
        //     return new Core2.Containers.IndexedAttestation(
        //         indexedAttestation.Custody_bit_0_indices.Select(y => new ValidatorIndex((ulong)y)),
        //         MapAttestationData(indexedAttestation.Data),
        //         new BlsSignature(Bytes.FromHexString(indexedAttestation.Signature))
        //     );
        // }
        //
        // private static Core2.Containers.AttestationData MapAttestationData(BeaconNode.OApi.AttestationData attestationData)
        // {
        //     // NOTE: This mapping isn't right, spec changes (sharding)
        //     return new Core2.Containers.AttestationData(
        //         Slot.None,
        //         CommitteeIndex.None, 
        //         new Root(attestationData.Beacon_block_root), 
        //         new Checkpoint(
        //             new Epoch((ulong)attestationData.Source_epoch), 
        //             new Root(attestationData.Source_root) 
        //         ),
        //         new Checkpoint(
        //             new Epoch((ulong)attestationData.Target_epoch), 
        //             new Root(attestationData.Target_root) 
        //         )
        //     );
        // }
        //
        // private static Core2.Containers.BeaconBlockHeader MapBeaconBlockHeader(BeaconNode.OApi.BeaconBlockHeader value)
        // {
        //     return new Core2.Containers.BeaconBlockHeader(
        //         new Slot((ulong)value.Slot),
        //         new Root(Bytes.FromHexString(value.Parent_root)), 
        //         new Root(Bytes.FromHexString(value.State_root)), 
        //         new Root(Bytes.FromHexString(value.Body_root))
        //     );
        // }
        //
        // private static IndexedAttestation MapIndexedAttestation(Core2.Containers.IndexedAttestation indexedAttestation)
        // {
        //     return new IndexedAttestation()
        //     {
        //         Signature = indexedAttestation.Signature.ToString(),
        //         Custody_bit_0_indices = indexedAttestation.AttestingIndices.Select(y => (int)y).ToList(),
        //         Custody_bit_1_indices = new List<int>(),
        //         Data = MapAttestationData(indexedAttestation.Data)
        //     };
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
        //         Target_epoch =  attestationData.Target.Epoch,
        //         Target_root = attestationData.Target.Root.AsSpan().ToArray()
        //     };
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
        
        public Task<string> VersionAsync()
        {
            throw new NotImplementedException();
        }

        public Task<Response> SyncingAsync()
        {
            throw new NotImplementedException();
        }

        public Task<Response2> ForkAsync()
        {
            throw new NotImplementedException();
        }

        public Task<Validator> ValidatorAsync(byte[] pubkey)
        {
            throw new NotImplementedException();
        }

        public Task<ICollection<ValidatorDuty>> DutiesAsync(IEnumerable<byte[]> validator_pubkeys, ulong? epoch)
        {
            throw new NotImplementedException();
        }

        public Task<BeaconBlock> BlockAsync(ulong slot, byte[] randao_reveal)
        {
            throw new NotImplementedException();
        }

        public Task Block2Async(BeaconBlock body)
        {
            throw new NotImplementedException();
        }

        public Task<Attestation> AttestationAsync(byte[] validator_pubkey, int poc_bit, ulong slot, ulong shard)
        {
            throw new NotImplementedException();
        }

        public Task Attestation2Async(Attestation body)
        {
            throw new NotImplementedException();
        }

        public Task<ulong> TimeAsync()
        {
            throw new NotImplementedException();
        }
    }
}
