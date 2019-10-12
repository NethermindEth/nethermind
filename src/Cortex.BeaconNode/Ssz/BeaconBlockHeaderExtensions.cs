using System.Collections.Generic;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BeaconBlockHeaderExtensions
    {
        public static SszContainer ToSszContainer(this BeaconBlockHeader item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlockHeader item)
        {
            //slot: Slot
            yield return new SszBasicElement(item.Slot);
            //parent_root: Hash
            yield return item.ParentRoot.ToSszBasicVector();
            //state_root: Hash
            yield return item.StateRoot.ToSszBasicVector();
            //body_root: Hash
            yield return item.BodyRoot.ToSszBasicVector();
            //signature: BLSSignature
            yield return item.Signature.ToSszBasicVector();
        }
    }
}
