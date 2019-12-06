using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class ProposerSlashingExtensions
    {
        public static SszContainer ToSszContainer(this ProposerSlashing item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszList ToSszList(this IEnumerable<ProposerSlashing> list, ulong limit)
        {
            return new SszList(list.Select(x => x.ToSszContainer()), limit);
        }

        private static IEnumerable<SszElement> GetValues(ProposerSlashing item)
        {
            yield return item.ProposerIndex.ToSszBasicElement();
            yield return item.Header1.ToSszContainer();
            yield return item.Header2.ToSszContainer();
        }
    }
}
