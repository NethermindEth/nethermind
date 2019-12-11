using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class AttesterSlashingExtensions
    {
        public static SszContainer ToSszContainer(this AttesterSlashing item, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters));
        }

        public static SszList ToSszList(this IEnumerable<AttesterSlashing> list, ulong limit, MiscellaneousParameters miscellaneousParameters)
        {
            return new SszList(list.Select(x => x.ToSszContainer(miscellaneousParameters)), limit);
        }

        private static IEnumerable<SszElement> GetValues(AttesterSlashing item, MiscellaneousParameters miscellaneousParameters)
        {
            yield return item.Attestation1.ToSszContainer(miscellaneousParameters);
            yield return item.Attestation2.ToSszContainer(miscellaneousParameters);
        }
    }
}
