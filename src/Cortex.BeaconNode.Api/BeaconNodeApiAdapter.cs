using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cortex.BeaconNode.Api
{
    public class BeaconNodeApiAdapter : IBeaconNodeApiController
    {
        public BeaconNodeApiAdapter()
        {
        }

        /// <summary>Get version string of the running beacon node.</summary>
        /// <returns>Request successful</returns>
        public async Task<string> VersionAsync()
        {
            return "Test-Cortex/0.0.1";
        }

        /// <summary>Get the genesis_time parameter from beacon node configuration.</summary>
        /// <returns>Request successful</returns>
        public Task<int> TimeAsync()
        {
            throw new NotImplementedException();
        }

        /// <summary>Poll to see if the the beacon node is syncing.</summary>
        /// <returns>Request successful</returns>
        public Task<Response> SyncingAsync()
        {
            throw new NotImplementedException();
        }
    
        /// <summary>Get fork information from running beacon node.</summary>
        /// <returns>Request successful</returns>
        public Task<Response2> ForkAsync()
        {
            throw new NotImplementedException();
        }
    
        /// <summary>Get validator duties for the requested validators.</summary>
        /// <param name="validator_pubkeys">An array of hex-encoded BLS public keys</param>
        /// <returns>Success response</returns>
        public Task<ICollection<ValidatorDuty>> DutiesAsync(System.Collections.Generic.IEnumerable<byte[]> validator_pubkeys, int? epoch)
        {
            throw new NotImplementedException();
        }
    
        /// <summary>Produce a new block, without signature.</summary>
        /// <param name="slot">The slot for which the block should be proposed.</param>
        /// <param name="randao_reveal">The validator's randao reveal value.</param>
        /// <returns>Success response</returns>
        public Task<BeaconBlock> BlockAsync(int slot, byte[] randao_reveal)
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
    
        /// <summary>Publish a signed attestation.</summary>
        /// <param name="attestation">An `IndexedAttestation` structure, as originally provided by the beacon node, but now with the signature field completed.</param>
        /// <returns>The attestation was validated successfully and has been broadcast. It has also been integrated into the beacon node's database.</returns>
        public Task Attestation2Async(IndexedAttestation attestation)
        {
            throw new NotImplementedException();
        }
    
    }
}