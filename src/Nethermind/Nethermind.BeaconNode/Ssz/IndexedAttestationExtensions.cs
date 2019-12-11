using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
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
