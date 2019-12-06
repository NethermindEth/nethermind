using System.Collections.Generic;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class IndexedAttestationExtensions
    {
        public static SszContainer ToSszContainer(this IndexedAttestation item, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters));
        }

        private static IEnumerable<SszElement> GetValues(IndexedAttestation item, MiscellaneousParameters miscellaneousParameters)
        {
            yield return item.CustodyBit0Indices.ToSszBasicList(miscellaneousParameters.MaximumValidatorsPerCommittee);
            yield return item.CustodyBit1Indices.ToSszBasicList(miscellaneousParameters.MaximumValidatorsPerCommittee);
            yield return item.Data.ToSszContainer();
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
