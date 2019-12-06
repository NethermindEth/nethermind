using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class SlotExtensions
    {
        public static SszElement ToSszBasicElement(this Slot item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
