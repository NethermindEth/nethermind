using System.Collections.Generic;
using System.Linq;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class DepositExtensions
    {
        public static SszContainer ToSszContainer(this Deposit item)
        {
            return new SszContainer(GetValues(item));
        }

        public static SszList ToSszList(this IEnumerable<Deposit> list, ulong limit)
        {
            return new SszList(list.Select(x => x.ToSszContainer()), limit);
        }

        private static IEnumerable<SszElement> GetValues(Deposit item)
        {
            // TODO: vector of byte arrays
            //yield return new SszVector(item.Proof.AsSpan());
            yield return item.Data.ToSszContainer();
        }
    }
}
