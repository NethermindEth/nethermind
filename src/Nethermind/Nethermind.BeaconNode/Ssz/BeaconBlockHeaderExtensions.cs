using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class BeaconBlockHeaderExtensions
    {
        public static Hash32 SigningRoot(this BeaconBlockHeader item)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlockHeader item)
        {
            return new SszContainer(GetValues(item, false));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlockHeader item, bool forSigning)
        {
            //slot: Slot
            yield return new SszBasicElement((ulong)item.Slot);
            //parent_root: Hash
            yield return item.ParentRoot.ToSszBasicVector();
            //state_root: Hash
            yield return item.StateRoot.ToSszBasicVector();
            //body_root: Hash
            yield return item.BodyRoot.ToSszBasicVector();
            if (!forSigning)
            {
                //signature: BLSSignature
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
