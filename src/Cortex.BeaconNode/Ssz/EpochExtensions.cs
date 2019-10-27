using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class EpochExtensions
    {
        public static Hash32 HashTreeRoot(this Epoch item)
        {
            var tree = new SszTree(item.ToSszBasicElement());
            return new Hash32(tree.HashTreeRoot());
        }

        public static SszElement ToSszBasicElement(this Epoch item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
