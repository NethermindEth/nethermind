using System;

namespace Cortex.Containers
{
    public class BeaconState
    {
        public ulong GenesisTime  { get; }

        public BeaconState(ulong genesisTime)
        {
            GenesisTime = genesisTime;
        }
    }
}
