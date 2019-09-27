using System;
using Cortex.Containers;

namespace Cortex.BeaconNode
{
    public class BeaconChain
    {
        public BeaconChain()
        {
            State = new BeaconState(106185600);
        }

        public BeaconState State { get; }
    }
}
