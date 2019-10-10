using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class Hash32Extensions
    {
        public static SszElement ToSszBasicVector(this Hash32 item)
        {
            return new SszBasicVector(item);
        }
    }
}
