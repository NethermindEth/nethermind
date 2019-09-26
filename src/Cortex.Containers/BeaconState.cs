using System;

namespace Cortex.Containers
{
    public class BeaconState
    {
        public UInt64 GenesisTime  { get; }

        public BeaconState(UInt64 genesisTime)
        {
            GenesisTime = genesisTime;
        }
    }
}
