using System.Collections.Generic;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Containers
{
    public class IndexedAttestation
    {
        private List<ValidatorIndex> _custodyBit0Indices;
        private List<ValidatorIndex> _custodyBit1Indices;

        public IndexedAttestation(
            IEnumerable<ValidatorIndex> custodyBit0Indices,
            IEnumerable<ValidatorIndex> custodyBit1Indices,
            AttestationData data,
            BlsSignature signature)
        {
            _custodyBit0Indices = new List<ValidatorIndex>(custodyBit0Indices);
            _custodyBit1Indices = new List<ValidatorIndex>(custodyBit1Indices);
            Data = data;
            Signature = signature;
        }

        /// <summary>Gets indices with custody bit equal to 0</summary>
        public IList<ValidatorIndex> CustodyBit0Indices { get { return _custodyBit0Indices; } }

        /// <summary>Gets indices with custody bit equal to 1</summary>
        public IList<ValidatorIndex> CustodyBit1Indices { get { return _custodyBit1Indices; } }

        public AttestationData Data { get; }

        public BlsSignature Signature { get; }

        public override string ToString()
        {
            return $"C:{Data.Index} S:{Data.Slot} Sig:{Signature.ToString().Substring(0, 12)}";
        }
    }
}
