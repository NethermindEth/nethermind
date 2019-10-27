using System.Collections.Generic;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BeaconBlockExtensions
    {
        public static Hash32 HashTreeRoot(this BeaconBlock item, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(item.ToSszContainer(maxOperationsPerBlock));
            return new Hash32(tree.HashTreeRoot());
        }

        public static Hash32 SigningRoot(this BeaconBlock item, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, maxOperationsPerBlock, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlock item, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            return new SszContainer(GetValues(item, maxOperationsPerBlock, false));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlock item, MaxOperationsPerBlock maxOperationsPerBlock, bool forSigning)
        {
            yield return item.Slot.ToSszBasicElement();
            yield return item.ParentRoot.ToSszBasicVector();
            yield return item.StateRoot.ToSszBasicVector();
            yield return item.Body.ToSszContainer(maxOperationsPerBlock);
            if (!forSigning)
            {
                //signature: BLSSignature
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
