using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

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
