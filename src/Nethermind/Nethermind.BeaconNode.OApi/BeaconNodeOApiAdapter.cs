using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Containers;
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

        /// <summary>Publish a signed attestation.</summary>
        /// <param name="attestation">An `IndexedAttestation` structure, as originally provided by the beacon node, but now with the signature field completed.</param>
        /// <returns>The attestation was validated successfully and has been broadcast. It has also been integrated into the beacon node's database.</returns>
        public Task Attestation2Async(IndexedAttestation attestation)
        {
            throw new NotImplementedException();
        }

        /// <summary>Produce an attestation, without signature.</summary>
        /// <param name="validator_pubkey">Uniquely identifying which validator this attestation is to be produced for.</param>
        /// <param name="poc_bit">The proof-of-custody bit that is to be reported by the requesting validator. This bit will be inserted into the appropriate location in the returned `IndexedAttestation`.</param>
        /// <param name="slot">The slot for which the attestation should be proposed.</param>
        /// <param name="shard">The shard number for which the attestation is to be proposed.</param>
        /// <returns>Success response</returns>
        public Task<IndexedAttestation> AttestationAsync(byte[] validator_pubkey, int poc_bit, int slot, int shard)
        {
            throw new NotImplementedException();
        }

        /// <summary>Publish a signed block.</summary>
        /// <param name="beacon_block">The `BeaconBlock` object, as sent from the beacon node originally, but now with the signature field completed.</param>
        /// <returns>The block was validated successfully and has been broadcast. It has also been integrated into the beacon node's database.</returns>
        public Task Block2Async(BeaconBlock beacon_block)
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
                await _beaconNode.NewBlockAsync(new Slot(slot), new BlsSignature(randao_reveal));
            
            OApi.BeaconBlock result = new OApi.BeaconBlock()
            {
                Slot = (ulong)data.Slot,
                Body = new OApi.BeaconBlockBody()
                {
                    Randao_reveal = data.Body!.RandaoReveal.AsSpan().ToArray()
                }
            };
            return result;
        }

        /// <summary>Get validator duties for the requested validators.</summary>
        /// <param name="validator_pubkeys">An array of hex-encoded BLS public keys</param>
        /// <returns>Success response</returns>
        public async Task<ICollection<ValidatorDuty>> DutiesAsync(System.Collections.Generic.IEnumerable<byte[]> validator_pubkeys, int? epoch)
        {
            IEnumerable<BlsPublicKey> publicKeys = validator_pubkeys.Select(x => new BlsPublicKey(x));
            Epoch targetEpoch = epoch.HasValue ? new Epoch((ulong)epoch) : Epoch.None;
            IList<BeaconNode.ValidatorDuty> duties = await _beaconNode.ValidatorDutiesAsync(publicKeys, targetEpoch);
            List<ValidatorDuty> result = duties.Select(x =>
                {
                    ValidatorDuty validatorDuty = new ValidatorDuty();
                    validatorDuty.Validator_pubkey = x.ValidatorPublicKey.Bytes;
                    validatorDuty.Attestation_slot = (int)x.AttestationSlot;
                    validatorDuty.Attestation_shard = (int)(ulong)x.AttestationShard;
                    validatorDuty.Block_proposal_slot = x.BlockProposalSlot == Slot.None ? null : (int?)x.BlockProposalSlot;
                    return validatorDuty;
                })
                .ToList();
            return result;
        }

        /// <summary>Get fork information from running beacon node.</summary>
        /// <returns>Request successful</returns>
        public async Task<Response2> ForkAsync()
        {
            Core2.Containers.Fork fork = await _beaconNode.GetNodeForkAsync();
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
            response.Is_syncing = await _beaconNode.GetIsSyncingAsync();
            response.Sync_status = new SyncingStatus();
            //response.Sync_status.Current_slot =
            return response;
        }

        /// <summary>Get the genesis_time parameter from beacon node configuration.</summary>
        /// <returns>Request successful</returns>
        public async Task<ulong> TimeAsync()
        {
            return await _beaconNode.GetGenesisTimeAsync();
        }

        /// <summary>Get version string of the running beacon node.</summary>
        /// <returns>Request successful</returns>
        public async Task<string> VersionAsync()
        {
            return await _beaconNode.GetNodeVersionAsync();
        }
    }
}
