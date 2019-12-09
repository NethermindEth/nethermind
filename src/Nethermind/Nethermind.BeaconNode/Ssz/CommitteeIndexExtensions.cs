using Cortex.SimpleSerialize;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode.Ssz
{
    public static class CommitteeIndexExtensions
    {
        public static SszElement ToSszBasicElement(this CommitteeIndex item)
        {
            return new SszBasicElement((ulong)item);
        }
    }
}
