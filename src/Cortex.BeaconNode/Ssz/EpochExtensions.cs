using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class EpochExtensions
    {
        public static SszElement ToSszBasicElement(this Epoch item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
