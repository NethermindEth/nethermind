using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class AttestationDataExtensions
    {
        public static SszContainer ToSszContainer(this AttestationData item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(AttestationData item)
        {
            yield return item.Slot.ToSszBasicElement();
            yield return item.Index.ToSszBasicElement();
            // LMD GHOST vote
            yield return item.BeaconBlockRoot.ToSszBasicVector();
            // FFG vote
            yield return item.Source.ToSszContainer();
            yield return item.Target.ToSszContainer();
        }
    }
}
