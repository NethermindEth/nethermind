using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class VoluntaryExitExtensions
    {
        public static Hash32 SigningRoot(this VoluntaryExit item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this VoluntaryExit item)
        {
            return new SszContainer(GetValues(item, false));
        }

        public static SszList ToSszList(this IEnumerable<VoluntaryExit> list, ulong limit)
        {
            return new SszList(list.Select(x => x.ToSszContainer()), limit);
        }

        private static IEnumerable<SszElement> GetValues(VoluntaryExit item, bool forSigning)
        {
            yield return item.Epoch.ToSszBasicElement();
            yield return item.ValidatorIndex.ToSszBasicElement();
            if (!forSigning)
            {
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
