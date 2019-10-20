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

        public static SszVector ToSszVector(this IEnumerable<Hash32> vector)
        {
            return new SszVector(vector.Select(x => x.ToSszBasicVector()));
        }
    }
}
