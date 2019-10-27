using System.Collections;

namespace Cortex.Containers
{
    public class Attestation
    {
        public Attestation(BitArray aggregationBits, AttestationData data, BitArray custodyBits, BlsSignature signature)
        {
            AggregationBits = aggregationBits;
            Data = data;
            CustodyBits = custodyBits;
            Signature = signature;
        }

        public BitArray AggregationBits { get; }

        public BitArray CustodyBits { get; }

        public AttestationData Data { get; }

        public BlsSignature Signature { get; private set; }

        public void SetSignature(BlsSignature signature)
        {
            Signature = signature;
        }
    }
}
