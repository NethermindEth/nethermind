using System.Collections;

namespace Cortex.Containers
{
    public class PendingAttestation
    {
        public PendingAttestation(BitArray aggregationBits, AttestationData data, Slot inclusionDelay)
        {
            AggregationBits = aggregationBits;
            Data = data;
            InclusionDelay = inclusionDelay;
        }

        public BitArray AggregationBits { get; }

        public AttestationData Data { get; }

        /// <summary>Gets a challengable bit (SSZ-bool, 1 byte) for the custody of crosslink data</summary>
        public Slot InclusionDelay { get; }

        public ValidatorIndex ProposerIndex { get; }
    }
}
