using System;
using System.Collections.Generic;
using System.Text;

namespace Cortex.BeaconNode
{
    public class BeaconChainParameters
    {
        public ulong MinGenesisTime { get; set; }
        public int MinGenesisActiveValidatorCount { get; set; }
    }
}
