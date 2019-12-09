using System.Collections.Generic;
using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class AttestationDataAndCustodyBitExtensions
    {
        public static Hash32 HashTreeRoot(this AttestationDataAndCustodyBit item)
        {
            var tree = new SszTree(item.ToSszContainer());
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszContainer ToSszContainer(this AttestationDataAndCustodyBit item)
        {
            return new SszContainer(GetValues(item));
        }

        private static IEnumerable<SszElement> GetValues(AttestationDataAndCustodyBit item)
        {
            yield return item.Data.ToSszContainer();
            // Challengeable bit (SSZ-bool, 1 byte) for the custody of crosslink data
            yield return item.CustodyBit.ToSszBasicElement();
        }
    }
}
