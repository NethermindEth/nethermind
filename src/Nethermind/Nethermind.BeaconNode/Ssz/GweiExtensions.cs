using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class GweiExtensions
    {
        public static SszElement ToSszBasicElement(this Gwei item)
        {
            return new SszBasicElement((ulong)item);
        }

        public static SszBasicList ToSszBasicList(this IEnumerable<Gwei> list, ulong limit)
        {
            return new SszBasicList(list.Select(x => (ulong)x).ToArray(), limit);
        }

        public static SszElement ToSszBasicVector(this IEnumerable<Gwei> vector)
        {
            return new SszBasicVector(vector.Select(x => (ulong)x).ToArray());
        }
    }
}
