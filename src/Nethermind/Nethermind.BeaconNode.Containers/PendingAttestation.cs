using System.Collections;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class PendingAttestation
    {
        public PendingAttestation(
            BitArray aggregationBits,
            AttestationData data,
            Slot inclusionDelay,
            ValidatorIndex proposerIndex)
        {
            AggregationBits = aggregationBits;
            Data = data;
            InclusionDelay = inclusionDelay;
            ProposerIndex = proposerIndex;
        }

        public BitArray AggregationBits { get; }

        public AttestationData Data { get; }

        /// <summary>Gets a challengable bit (SSZ-bool, 1 byte) for the custody of crosslink data</summary>
        public Slot InclusionDelay { get; }

        public ValidatorIndex ProposerIndex { get; }

        public static PendingAttestation Clone(PendingAttestation other)
        {
            var clone = new PendingAttestation(
                new BitArray(other.AggregationBits),
                AttestationData.Clone(other.Data),
                other.InclusionDelay,
                other.ProposerIndex);
            return clone;
        }
    }
}
