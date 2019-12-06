using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class AttestationExtensions
    {
        public static SszContainer ToSszContainer(this Attestation item, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters));
        }

        public static SszList ToSszList(this IEnumerable<Attestation> list, ulong limit, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszList(list.Select(x => x.ToSszContainer(miscellaneousParameters)), limit);
        }

        private static IEnumerable<SszElement> GetValues(Attestation item, MiscellaneousParameters miscellaneousParameters)
        {
            yield return item.AggregationBits.ToSszBitlist(miscellaneousParameters.MaximumValidatorsPerCommittee);
            yield return item.Data.ToSszContainer();
            yield return item.CustodyBits.ToSszBitlist(miscellaneousParameters.MaximumValidatorsPerCommittee);
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
