using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class BeaconBlockExtensions
    {
        public static Hash32 HashTreeRoot(this BeaconBlock item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(item.ToSszContainer(miscellaneousParameters, maxOperationsPerBlock));
            return new Hash32(tree.HashTreeRoot());
        }

        public static Hash32 SigningRoot(this BeaconBlock item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            var tree = new SszTree(new SszContainer(GetValues(item, miscellaneousParameters, maxOperationsPerBlock, true)));
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this BeaconBlock item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock)
        {
            return new SszContainer(GetValues(item, miscellaneousParameters, maxOperationsPerBlock, false));
        }

        private static IEnumerable<SszElement> GetValues(BeaconBlock item, MiscellaneousParameters miscellaneousParameters, MaxOperationsPerBlock maxOperationsPerBlock, bool forSigning)
        {
            yield return item.Slot.ToSszBasicElement();
            yield return item.ParentRoot.ToSszBasicVector();
            yield return item.StateRoot.ToSszBasicVector();
            yield return item.Body.ToSszContainer(miscellaneousParameters, maxOperationsPerBlock);
            if (!forSigning)
            {
                //signature: BLSSignature
                yield return item.Signature.ToSszBasicVector();
            }
        }
    }
}
