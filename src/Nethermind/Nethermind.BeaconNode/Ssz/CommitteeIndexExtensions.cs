using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class CommitteeIndexExtensions
    {
        public static SszElement ToSszBasicElement(this CommitteeIndex item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
