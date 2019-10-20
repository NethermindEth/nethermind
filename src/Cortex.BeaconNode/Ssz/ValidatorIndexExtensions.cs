using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class ValidatorIndexExtensions
    {
        public static SszElement ToSszBasicElement(this ValidatorIndex item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
