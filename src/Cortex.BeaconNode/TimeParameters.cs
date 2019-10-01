using System;
using System.Collections.Generic;
using System.Text;

using Slot = System.UInt64;

namespace Cortex.BeaconNode
{
    public class TimeParameters
    {
        public readonly ulong SecondsPerDay = 86400;

        public Slot SlotsPerEpoch { get; set; }
    }
}
