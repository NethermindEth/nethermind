using System;
using System.Collections.Generic;
using System.Text;

using Slot = System.UInt64;

namespace Cortex.BeaconNode
{
    public class TimeParameters
    {
        public Slot SlotsPerEpoch { get; set; }
    }
}
