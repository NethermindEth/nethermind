using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class ValidatorIndexExtensions
    {
        public static SszElement ToSszBasicElement(this ValidatorIndex item)
        {
            return new SszBasicElement((ulong)item);
        }

        public static SszBasicList ToSszBasicList(this IEnumerable<ValidatorIndex> list, ulong limit)
        {
            return new SszBasicList(list.Cast<ulong>().ToArray(), limit);
        }
    }
}
