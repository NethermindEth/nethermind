using System;
using System.Collections.Generic;
using System.Text;
using Cortex.Containers;
using Cortex.SimpleSerialize;

namespace Cortex.BeaconNode.Ssz
{
    public static class ForkVersionExtensions
    {
        public static SszElement ToSszBasicVector(this ForkVersion item)
        {
            return new SszBasicVector(item.AsSpan());
        }
    }
}
