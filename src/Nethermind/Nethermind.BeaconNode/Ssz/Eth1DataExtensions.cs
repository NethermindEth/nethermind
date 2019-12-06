using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class Eth1DataExtensions
    {
        public static SszContainer ToSszContainer(this Eth1Data item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(Eth1Data item)
        {
            yield return item.DepositRoot.ToSszBasicVector();
            yield return item.DepositCount.ToSszBasicElement();
            yield return item.BlockHash.ToSszBasicVector();
        }
    }
}
