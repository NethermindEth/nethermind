using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using BeaconBlock = Nethermind.BeaconNode.Api.BeaconBlock;
using BeaconBlockBody = Nethermind.BeaconNode.Api.BeaconBlockBody;
using IndexedAttestation = Nethermind.BeaconNode.Api.IndexedAttestation;

namespace Nethermind.BeaconNode.Api
{
    public class BeaconNodeApiAdapter : IBeaconNodeApiController
    {
        private readonly BeaconNodeConfiguration _beaconNodeConfiguration;
        private readonly BlockProducer _blockProducer;
        private readonly ILogger _logger;

        public BeaconNodeApiAdapter(ILogger<BeaconNodeApiAdapter> logger,
            BeaconNodeConfiguration beaconNodeConfiguration,
            BlockProducer blockProducer)
        {
            _logger = logger;
            _beaconNodeConfiguration = beaconNodeConfiguration;
            _blockProducer = blockProducer;
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
            var data = await _blockProducer.NewBlockAsync(new Slot(slot), new BlsSignature(randao_reveal));

            var result = new BeaconBlock()
            {
                Slot = (ulong)data.Slot,
                Body = new BeaconBlockBody()
                {
                    Randao_reveal = data.Body!.RandaoReveal.AsSpan().ToArray()
                }
            };
            return result;
        }

        /// <summary>Get validator duties for the requested validators.</summary>
        /// <param name="validator_pubkeys">An array of hex-encoded BLS public keys</param>
        /// <returns>Success response</returns>
        public Task<ICollection<ValidatorDuty>> DutiesAsync(System.Collections.Generic.IEnumerable<byte[]> validator_pubkeys, int? epoch)
        {
            throw new NotImplementedException();
        }

        /// <summary>Get fork information from running beacon node.</summary>
        /// <returns>Request successful</returns>
        public Task<Response2> ForkAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>Poll to see if the the beacon node is syncing.</summary>
        /// <returns>Request successful</returns>
        public Task<Response> SyncingAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>Get the genesis_time parameter from beacon node configuration.</summary>
        /// <returns>Request successful</returns>
        public async Task<ulong> TimeAsync()
        {
            var state = await _blockProducer.GetHeadStateAsync();
            if (state != null)
            {
                return state.GenesisTime;
            }
            return 0;
        }

        /// <summary>Get version string of the running beacon node.</summary>
        /// <returns>Request successful</returns>
        public async Task<string> VersionAsync()
        {
            return await Task.Run(() => _beaconNodeConfiguration.Version);
        }
    }
}
