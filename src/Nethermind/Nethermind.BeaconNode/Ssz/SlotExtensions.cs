using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Ssz
{
    public static class SlotExtensions
    {
        public static SszElement ToSszBasicElement(this Slot item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
