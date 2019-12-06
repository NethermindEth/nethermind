using System.Collections.Generic;
using System.Linq;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class Hash32Extensions
    {
        public static SszBasicVector ToSszBasicVector(this Hash32 item)
        {
            return new SszBasicVector(item.AsSpan());
        }

        public static SszList ToSszList(this IEnumerable<Hash32> list, ulong limit)
        {
            return new SszList(list.Select(x => x.ToSszBasicVector()), limit);
        }

        public static SszVector ToSszVector(this IEnumerable<Hash32> vector)
        {
            return new SszVector(vector.Select(x => x.ToSszBasicVector()));
        }
    }
}
