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
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using BeaconBlock = Nethermind.BeaconNode.OApi.BeaconBlock;
using BeaconBlockBody = Nethermind.BeaconNode.OApi.BeaconBlockBody;
using IndexedAttestation = Nethermind.BeaconNode.OApi.IndexedAttestation;

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
        
        /// <summary>Publish a signed block.</summary>
        /// <param name="beacon_block">The `BeaconBlock` object, as sent from the beacon node originally, but now with the signature field completed.</param>
        /// <returns>The block was validated successfully and has been broadcast. It has also been integrated into the beacon node's database.</returns>
        public Task Block2Async(BeaconBlock beacon_block)
        {
            // TODO: Map and call facade.
            
            throw new NotImplementedException();
        }

        Task<Attestation> IBeaconNodeOApiController.AttestationAsync(byte[] validator_pubkey, int poc_bit, int slot, int shard)
        {
            throw new NotImplementedException();
        }

        public Task Attestation2Async(Attestation body)
        {
            throw new NotImplementedException();
        }

        /// <summary>Produce a new block, without signature.</summary>
        /// <param name="slot">The slot for which the block should be proposed.</param>
        /// <param name="randao_reveal">The validator's randao reveal value.</param>
        /// <returns>Success response</returns>
        public async Task<BeaconBlock> BlockAsync(ulong slot, byte[] randao_reveal)
        {
            Containers.BeaconBlock data =
                await _beaconNode.NewBlockAsync(new Slot(slot), new BlsSignature(randao_reveal), CancellationToken.None);
            
            OApi.BeaconBlock result = new OApi.BeaconBlock()
            {
                Slot = (ulong)data.Slot,
                Parent_root = data.ParentRoot.ToString(),
                State_root = data.StateRoot.ToString(),
                Signature = data.Signature.ToString(),
                Body = new OApi.BeaconBlockBody()
                {
                    Randao_reveal = data.Body!.RandaoReveal.AsSpan().ToArray(),
                    Eth1_data = new Eth1_data()
                    {
                        Block_hash = data.Body.Eth1Data.BlockHash.Bytes,
                        Deposit_count = (int)data.Body.Eth1Data.DepositCount,
                        Deposit_root = data.Body.Eth1Data.DepositRoot.Bytes 
                    },
                    Graffiti = data.Body.Graffiti.AsSpan().ToArray(),
                    Proposer_slashings = data.Body.ProposerSlashings.Select(x => new Proposer_slashings()
                    {
                        Header_1 = MapBeaconBlockHeader(x.Header1),
                        Header_2 = MapBeaconBlockHeader(x.Header2),
                        Proposer_index = (int)x.ProposerIndex
                    }).ToList(),
                    Attester_slashings = data.Body.AttesterSlashings.Select(x => new Attester_slashings()
                    {
                        Attestation_1 = MapIndexedAttestation(x.Attestation1),
                        Attestation_2 = MapIndexedAttestation(x.Attestation2)
                    }).ToList(),
                    Attestations = data.Body.Attestations.Select(x => new Attestations()
                    {
                        Signature = x.Signature.Bytes,
                        Aggregation_bits = x.AggregationBits.Cast<byte>().ToArray(),
                        Custody_bits = x.CustodyBits.Cast<byte>().ToArray(),
                        Data = MapAttestationData(x.Data)
                    }).ToList(),
                    Voluntary_exits = data.Body.VoluntaryExits.Select(x => new Voluntary_exits()
                    {
                        Validator_index = (int)x.ValidatorIndex,
                        Epoch = x.Epoch,
                        Signature = x.Signature.Bytes
                    }).ToList(),
                    Deposits = data.Body.Deposits.Select((x, index) => new Deposits()
                    {
                        Index = index,
                        Proof = x.Proof.Select(y => y.Bytes).ToList(),
                        Data = new Data()
                        {
                            Amount = (int)(ulong)x.Data.Amount,
                            Pubkey = x.Data.PublicKey.Bytes,
                            Signature = x.Data.Signature.Bytes,
                            Withdrawal_credentials = x.Data.WithdrawalCredentials.Bytes
                        }
                    }).ToList(),
                }
            };
            return result;
        }

        private static IndexedAttestation MapIndexedAttestation(Containers.IndexedAttestation indexedAttestation)
        {
            return new IndexedAttestation()
            {
                Signature = indexedAttestation.Signature.ToString(),
                Custody_bit_0_indices = indexedAttestation.CustodyBit0Indices.Select(y => (int)y).ToList(),
                Custody_bit_1_indices = indexedAttestation.CustodyBit1Indices.Select(y => (int)y).ToList(),
                Data = MapAttestationData(indexedAttestation.Data)
            };
        }

        private static AttestationData MapAttestationData(Containers.AttestationData attestationData)
        {
            // NOTE: This mapping isn't right, spec changes (sharding)
            return new AttestationData()
            {
                Beacon_block_root = attestationData.BeaconBlockRoot.Bytes,
                Crosslink = new Crosslink(),
                Source_epoch = attestationData.Source.Epoch,
                Source_root = attestationData.Source.Root.Bytes,
                Target_epoch =  attestationData.Target.Epoch,
                Target_root = attestationData.Target.Root.Bytes
            };
        }

        private static BeaconBlockHeader MapBeaconBlockHeader(Containers.BeaconBlockHeader value)
        {
            return new BeaconBlockHeader()
            {
                Body_root = value.BodyRoot.ToString(),
                Parent_root = value.ParentRoot.ToString(),
                Slot = value.Slot,
                State_root = value.StateRoot.ToString(),
                Signature = value.Signature.ToString()
            };
        }

        public Task<Validator> ValidatorAsync(byte[] pubkey)
        {
            throw new NotImplementedException();
        }

        /// <summary>Get validator duties for the requested validators.</summary>
        /// <param name="validator_pubkeys">An array of hex-encoded BLS public keys</param>
        /// <returns>Success response</returns>
        public async Task<ICollection<ValidatorDuty>> DutiesAsync(System.Collections.Generic.IEnumerable<byte[]> validator_pubkeys, int? epoch)
        {
            IEnumerable<BlsPublicKey> publicKeys = validator_pubkeys.Select(x => new BlsPublicKey(x));
            Epoch targetEpoch = epoch.HasValue ? new Epoch((ulong)epoch) : Epoch.None;
            var duties = _beaconNode.ValidatorDutiesAsync(publicKeys, targetEpoch, CancellationToken.None);
            List<ValidatorDuty> result = new List<ValidatorDuty>();
            await foreach(var duty in duties)
            {
                ValidatorDuty validatorDuty = new ValidatorDuty();
                validatorDuty.Validator_pubkey = duty.ValidatorPublicKey.Bytes;
                validatorDuty.Attestation_slot = (int)duty.AttestationSlot;
                validatorDuty.Attestation_shard = (int)(ulong)duty.AttestationShard;
                validatorDuty.Block_proposal_slot = duty.BlockProposalSlot == Slot.None ? null : (int?)duty.BlockProposalSlot;
                result.Add(validatorDuty);
            }
            return result;
        }

        /// <summary>Get fork information from running beacon node.</summary>
        /// <returns>Request successful</returns>
        public async Task<Response2> ForkAsync()
        {
            Core2.Containers.Fork fork = await _beaconNode.GetNodeForkAsync(CancellationToken.None);
            Response2 response2 = new Response2();
            // TODO: Not sure what chain ID should be.
            response2.Chain_id = 0;
            response2.Fork = new Fork();
            response2.Fork.Epoch = fork.Epoch;
            response2.Fork.Current_version = fork.CurrentVersion.AsSpan().ToArray();
            response2.Fork.Previous_version = fork.PreviousVersion.AsSpan().ToArray();
            return response2;
        }

        /// <summary>Poll to see if the the beacon node is syncing.</summary>
        /// <returns>Request successful</returns>
        public async Task<Response> SyncingAsync()
        {
            Response response = new Response();
            response.Is_syncing = await _beaconNode.GetIsSyncingAsync(CancellationToken.None);
            response.Sync_status = new SyncingStatus();
            //response.Sync_status.Current_slot =
            return response;
        }

        /// <summary>Get the genesis_time parameter from beacon node configuration.</summary>
        /// <returns>Request successful</returns>
        public async Task<ulong> TimeAsync()
        {
            return await _beaconNode.GetGenesisTimeAsync(CancellationToken.None);
        }

        /// <summary>Get version string of the running beacon node.</summary>
        /// <returns>Request successful</returns>
        public async Task<string> VersionAsync()
        {
            return await _beaconNode.GetNodeVersionAsync(CancellationToken.None);
        }
    }
}
