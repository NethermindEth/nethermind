using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class BasicExtensions
    {
        public static SszElement ToSszBasicVector(this byte[] item)
        {
            return new SszBasicVector(item);
        }
    }
}
