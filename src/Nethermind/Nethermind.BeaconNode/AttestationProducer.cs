using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    public class AttestationProducer
    {
        public async Task<Attestation> NewAttestationAsync(BlsPublicKey validatorPublicKey,
            bool proofOfCustodyBit, Slot targetSlot, Shard targetShard,
            CancellationToken cancellationToken)
        {
            await Task.Delay(0);
            return Attestation.Zero;
        }
    }
}