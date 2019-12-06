using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class PendingAttestationExtensions
    {
        public static SszContainer ToSszContainer(this PendingAttestation item, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters));
        }

        public static SszList ToSszList(this IEnumerable<PendingAttestation> list, ulong limit, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszList(list.Select(x => x.ToSszContainer(miscellaneousParameters)), limit);
        }

        private static IEnumerable<SszElement> GetValues(PendingAttestation item, MiscellaneousParameters miscellaneousParameters)
        {
            yield return item.AggregationBits.ToSszBitlist(miscellaneousParameters.MaximumValidatorsPerCommittee);
            yield return item.Data.ToSszContainer();
            yield return item.InclusionDelay.ToSszBasicElement();
            yield return item.ProposerIndex.ToSszBasicElement();
        }
    }
}
